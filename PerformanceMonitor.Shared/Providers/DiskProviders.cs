using Hardware.Info;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;

namespace TaskManager.Providers;

public abstract class DiskProviderBase : IDiskProvider
{
    private readonly HardwareInfo _hardwareInfo = new();

    public virtual DiskSpecs GetSpecs()
    {
        var s = new DiskSpecs();
        try
        {
            DriveInfo? root = DriveInfo.GetDrives()
                .FirstOrDefault(d => d.IsReady && (d.Name.StartsWith("C") || d.Name == "/"))
                ?? DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);

            if (root != null)
            {
                double totalGB = Math.Round(root.TotalSize / (1024.0 * 1024.0 * 1024.0), 0);
                s.Capacity = $"{totalGB} GB";
                s.Formatted = $"{totalGB} GB";
                s.SystemDisk = IsSystemDrive(root) ? "Yes" : "No";
            }

            _hardwareInfo.RefreshDriveList();
            s.ModelName = _hardwareInfo.DriveList.Count > 0
                ? _hardwareInfo.DriveList[0].Model
                : GetFallbackModelName();

            LoadPlatformDetails(s);
        }
        catch
        {
            s.ModelName = "Generic Storage Device";
        }
        return s;
    }

    public abstract DiskSample? Sample();

    protected virtual bool IsSystemDrive(DriveInfo drive) => drive.Name == "/";
    protected virtual string GetFallbackModelName() => "System Storage Device";
    protected virtual void LoadPlatformDetails(DiskSpecs s) { }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsDiskProvider : DiskProviderBase, IDiskListProvider
{
    private List<DiskInfo>? _cachedSpecs;
    private readonly object _lock = new();

    // ---- Legacy Paths (Backward Compatibility) ----
    
    public override DiskSpecs GetSpecs()
    {
        var list = GetDiskList();
        var primary = list.FirstOrDefault(d => d.SystemDisk == "Yes") ?? list.FirstOrDefault();
        if (primary == null) return new DiskSpecs { DiskName = "Disk", ModelName = "No disk detected", Type = "Unknown" };

        return new DiskSpecs
        {
            DiskName = primary.DiskName, ModelName = primary.ModelName, Capacity = primary.Capacity,
            Formatted = primary.Formatted, SystemDisk = primary.SystemDisk, PageFile = primary.PageFile,
            Type = primary.Type, UsedText = primary.UsedText, FreeText = primary.FreeText,
            AppsAndDataText = primary.AppsAndDataText, SystemReservedText = primary.SystemReservedText,
            PressureText = primary.PressureText
        };
    }

    public override DiskSample? Sample()
    {
        var samples = SampleDiskList();
        var primary = samples.FirstOrDefault(d => d.SystemDisk == "Yes") ?? samples.FirstOrDefault();
        if (primary == null) return null;

        return new DiskSample
        {
            ReadBytesPerSec = primary.ReadBytesPerSec, WriteBytesPerSec = primary.WriteBytesPerSec,
            ActivePercent = primary.ActivePercent, ResponseMs = primary.ResponseMs,
            NotSupported = primary.NotSupported, NotSupportedMessage = primary.NotSupportedMessage
        };
    }

    // ---- Multi-Disk Path ----

    public IReadOnlyList<DiskInfo> GetDiskList()
    {
        lock (_lock)
        {
            _cachedSpecs ??= BuildDiskList();
            return _cachedSpecs;
        }
    }

    public IReadOnlyList<DiskInfo> SampleDiskList()
    {
        var specs = GetDiskList();
        var result = new List<DiskInfo>(specs.Count);
        
        // Fetch performance data for all disks at once
        var perfData = GetPerfData();

        foreach (var spec in specs)
        {
            var info = CloneSpec(spec);
            if (spec.DeviceId != null && perfData.TryGetValue(spec.DeviceId, out var perf))
            {
                info.ReadBytesPerSec = perf.Read;
                info.WriteBytesPerSec = perf.Write;
                info.ActivePercent = perf.Active;
                info.ResponseMs = perf.Response;
            }
            result.Add(info);
        }
        return result;
    }

    private static DiskInfo CloneSpec(DiskInfo src) => new()
    {
        DeviceId = src.DeviceId, DiskName = src.DiskName, ModelName = src.ModelName, Type = src.Type,
        Capacity = src.Capacity, Formatted = src.Formatted, SystemDisk = src.SystemDisk, PageFile = src.PageFile,
        UsedText = src.UsedText, FreeText = src.FreeText, AppsAndDataText = src.AppsAndDataText,
        SystemReservedText = src.SystemReservedText, PressureText = src.PressureText
    };

    private List<DiskInfo> BuildDiskList()
    {
        var result = new List<DiskInfo>();
        string osDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        
        // 1. Get MSFT PhysicalDisk data (BusType, MediaType) keyed by DeviceID (Index)
        var msftMap = new Dictionary<string, (ushort MediaType, ushort BusType)>();
        try
        {
            using var mediaSearcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (ManagementBaseObject? media in mediaSearcher.Get())
            {
                if (media == null) continue;
                string id = media["DeviceId"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(id))
                    msftMap[id] = (Convert.ToUInt16(media["MediaType"]), Convert.ToUInt16(media["BusType"]));
            }
        }
        catch { }

        // 2. Iterate Physical Disks
        try
        {
            using var diskSearcher = new ManagementObjectSearcher("SELECT Index, DeviceID, Model, Size FROM Win32_DiskDrive");
            foreach (ManagementBaseObject? disk in diskSearcher.Get())
            {
                if (disk == null) continue;
                string indexStr = disk["Index"]?.ToString() ?? "0";
                string model = disk["Model"]?.ToString()?.Trim() ?? "Generic Storage Device";
                double sizeBytes = Convert.ToDouble(disk["Size"] ?? 0);

                // Determine Type (Admin-free IOCTL -> MSFT fallback -> Unknown)
                string type = "Unknown";
                bool? isSsd = SeekPenaltyClassifier.IsSsd(int.Parse(indexStr));
                
                if (isSsd == true) type = "SSD";
                else if (isSsd == false) type = "HDD";
                else if (msftMap.TryGetValue(indexStr, out var msft))
                {
                    type = msft.BusType == 17 ? "NVMe SSD" : msft.MediaType == 4 ? "SSD" : msft.MediaType == 3 ? "HDD" : "Unknown";
                }

                // Find corresponding logical drives (letters) for this physical disk
                var letters = GetLogicalDrivesForPhysicalDisk(indexStr);
                double totalSpace = letters.Sum(l => l.Size);
                double freeSpace = letters.Sum(l => l.Free);
                
                // Fallback to physical size if logical partitions aren't mounted
                double capacityBytes = totalSpace > 0 ? totalSpace : sizeBytes;
                double usedBytes = capacityBytes - freeSpace;

                string capacityText = capacityBytes > 0 ? $"{Math.Round(capacityBytes / (1024.0 * 1024 * 1024), 0)} GB" : "-";
                string? usedText = capacityBytes > 0 ? $"{Math.Round(usedBytes / (1024.0 * 1024 * 1024), 1)} GB ({(usedBytes / capacityBytes) * 100:F1}%)" : null;
                string? freeText = capacityBytes > 0 ? $"{Math.Round(freeSpace / (1024.0 * 1024 * 1024), 1)} GB ({(freeSpace / capacityBytes) * 100:F1}%)" : null;
                
                bool isSystem = letters.Any(l => l.Letter.StartsWith(osDrive, StringComparison.OrdinalIgnoreCase));
                bool hasPageFile = CheckPageFile(letters.Select(l => l.Letter));

                result.Add(new DiskInfo
                {
                    DeviceId = indexStr,
                    DiskName = $"Disk {indexStr}",
                    ModelName = model,
                    Type = type,
                    Capacity = capacityText,
                    Formatted = capacityText,
                    SystemDisk = isSystem ? "Yes" : "No",
                    PageFile = hasPageFile ? "Yes" : "No",
                    UsedText = usedText,
                    FreeText = freeText,
                    FreeBytes = freeSpace > 0 ? freeSpace : null
                });
            }
        }
        catch { }

        // If no disks were found via Win32_DiskDrive, fall back to a single generic disk
        if (result.Count == 0)
        {
            result.Add(new DiskInfo { DiskName = "Disk", ModelName = "No disk detected", Type = "Unknown" });
        }

        return result;
    }

    private static List<(string Letter, double Size, double Free)> GetLogicalDrivesForPhysicalDisk(string index)
    {
        var result = new List<(string, double, double)>();
        try
        {
            // WMI association query to find partitions for this specific disk
            string query = $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE{index}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition";
            using var partSearcher = new ManagementObjectSearcher(query);
            
            foreach (ManagementBaseObject? part in partSearcher.Get())
            {
                if (part == null) continue;
                string partDeviceId = part["DeviceID"]?.ToString()?.Replace("\\", "\\\\") ?? "";
                if (string.IsNullOrEmpty(partDeviceId)) continue;

                string ldQuery = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partDeviceId}'}} WHERE AssocClass=Win32_LogicalDiskToPartition";
                using var ldSearcher = new ManagementObjectSearcher(ldQuery);
                
                foreach (ManagementBaseObject? ld in ldSearcher.Get())
                {
                    if (ld == null) continue;
                    string letter = ld["DeviceID"]?.ToString() ?? "";
                    double size = Convert.ToDouble(ld["Size"] ?? 0);
                    double free = Convert.ToDouble(ld["FreeSpace"] ?? 0);
                    if (!string.IsNullOrEmpty(letter)) result.Add((letter, size, free));
                }
            }
        }
        catch { }
        return result;
    }

    private static bool CheckPageFile(IEnumerable<string> driveLetters)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileSetting");
            foreach (ManagementBaseObject? pf in searcher.Get())
            {
                if (pf == null) continue;
                string name = pf["Name"]?.ToString() ?? "";
                if (driveLetters.Any(l => name.StartsWith(l, StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        catch { }
        return false;
    }

    private static Dictionary<string, (double Read, double Write, double Active, double Response)> GetPerfData()
    {
        var dict = new Dictionary<string, (double, double, double, double)>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DiskReadBytesPerSec, DiskWriteBytesPerSec, PercentIdleTime, AvgDiskSecPerTransfer FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
            foreach (ManagementBaseObject? obj in searcher.Get())
            {
                if (obj == null) continue;
                string name = obj["Name"]?.ToString() ?? "";
                if (name == "_Total") continue;

                // Name format is usually "0 C:" or "1 D:" -> extract the physical index
                int spaceIdx = name.IndexOf(' ');
                string index = spaceIdx > 0 ? name[..spaceIdx] : name;

                dict[index] = (
                    Convert.ToDouble(obj["DiskReadBytesPerSec"]),
                    Convert.ToDouble(obj["DiskWriteBytesPerSec"]),
                    Math.Clamp(100 - Convert.ToDouble(obj["PercentIdleTime"]), 0, 100),
                    Convert.ToDouble(obj["AvgDiskSecPerTransfer"]) * 1000.0
                );
            }
        }
        catch { }
        return dict;
    }
}

/// <summary>
/// Admin-free SSD/HDD classifier using DeviceIoControl (StorageDeviceSeekPenaltyProperty).
/// </summary>
internal static class SeekPenaltyClassifier
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    public static bool? IsSsd(int physicalDriveIndex)
    {
        try
        {
            using var handle = CreateFile(
                $"\\\\.\\PhysicalDrive{physicalDriveIndex}",
                0, // No access required for properties
                0x00000001 | 0x00000002, // FILE_SHARE_READ | FILE_SHARE_WRITE
                IntPtr.Zero, 3, 0, IntPtr.Zero); // OPEN_EXISTING

            if (handle.IsInvalid) return null;

            // Allocate buffers
            int inBufferSize = 12; // STORAGE_PROPERTY_QUERY
            int outBufferSize = 12; // DEVICE_SEEK_PENALTY_DESCRIPTOR
            IntPtr inBuffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(inBufferSize);
            IntPtr outBuffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(outBufferSize);

            try
            {
                // Setup STORAGE_PROPERTY_QUERY
                System.Runtime.InteropServices.Marshal.WriteInt32(inBuffer, StorageDeviceSeekPenaltyProperty);
                System.Runtime.InteropServices.Marshal.WriteInt32(inBuffer, 4, 0); // PropertyStandardQuery

                if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, inBuffer, (uint)inBufferSize, outBuffer, (uint)outBufferSize, out _, IntPtr.Zero))
                {
                    // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version (4), Size (4), IncursSeekPenalty (1) at offset 8.
                    int descriptorSize = System.Runtime.InteropServices.Marshal.ReadInt32(outBuffer, 4);
                    if (descriptorSize < 9) return null;
                    bool incursSeekPenalty = System.Runtime.InteropServices.Marshal.ReadByte(outBuffer, 8) != 0;
                    return !incursSeekPenalty; // False penalty = SSD, True penalty = HDD
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(inBuffer);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(outBuffer);
            }
        }
        catch { }
        return null;
    }
}

public sealed class LinuxDiskProvider : DiskProviderBase, IDiskListProvider
{
    private const int DiskstatsSectorBytes = 512;
    private const string SysBlockRoot = "/sys/block";

    private sealed class DeviceState
    {
        public long LastReadBytes;
        public long LastWriteBytes;
        public long LastIoTicksMs;
        public long LastReadsCompleted;
        public long LastWritesCompleted;
        public long LastReadTimeMs;
        public long LastWriteTimeMs;
        public DateTime LastSampleUtc;
        public bool HaveBaseline;
    }

    private readonly Dictionary<string, DeviceState> _state = new();
    private List<DiskInfo>? _cachedSpecs;
    private readonly object _lock = new();

    // ---- Legacy single-disk path (keeps DiskViewModel working for now) ----

    public override DiskSpecs GetSpecs()
    {
        var list = GetDiskList();
        var primary = list.FirstOrDefault(d => d.SystemDisk == "Yes") ?? list.FirstOrDefault();
        if (primary == null)
            return new DiskSpecs { DiskName = "Disk", ModelName = "No disk detected", Type = "Unknown" };

        return new DiskSpecs
        {
            DiskName = primary.DiskName,
            ModelName = primary.ModelName,
            Capacity = primary.Capacity,
            Formatted = primary.Formatted,
            SystemDisk = primary.SystemDisk,
            PageFile = primary.PageFile,
            Type = primary.Type,
            UsedText = primary.UsedText,
            FreeText = primary.FreeText,
            AppsAndDataText = primary.AppsAndDataText,
            SystemReservedText = primary.SystemReservedText,
            PressureText = primary.PressureText
        };
    }

    public override DiskSample? Sample()
    {
        var samples = SampleDiskList();
        var primary = samples.FirstOrDefault(d => d.SystemDisk == "Yes") ?? samples.FirstOrDefault();
        if (primary == null) return null;

        return new DiskSample
        {
            ReadBytesPerSec = primary.ReadBytesPerSec,
            WriteBytesPerSec = primary.WriteBytesPerSec,
            ActivePercent = primary.ActivePercent,
            ResponseMs = primary.ResponseMs,
            NotSupported = primary.NotSupported,
            NotSupportedMessage = primary.NotSupportedMessage
        };
    }

    // ---- New multi-disk path ----

    public IReadOnlyList<DiskInfo> GetDiskList()
    {
        lock (_lock)
        {
            _cachedSpecs ??= BuildDiskList();
            return _cachedSpecs;
        }
    }

    public IReadOnlyList<DiskInfo> SampleDiskList()
    {
        var specs = GetDiskList();
        var now = DateTime.UtcNow;
        var result = new List<DiskInfo>(specs.Count);

        foreach (var spec in specs)
        {
            var info = CloneSpec(spec);
            if (spec.DeviceId != null)
            {
                try { FillSample(info, spec.DeviceId, now); } catch { }
            }
            result.Add(info);
        }
        return result;
    }

    private static DiskInfo CloneSpec(DiskInfo src) => new()
    {
        DeviceId = src.DeviceId,
        DiskName = src.DiskName,
        ModelName = src.ModelName,
        Type = src.Type,
        Capacity = src.Capacity,
        Formatted = src.Formatted,
        SystemDisk = src.SystemDisk,
        PageFile = src.PageFile,
        UsedText = src.UsedText,
        FreeText = src.FreeText,
        AppsAndDataText = src.AppsAndDataText,
        SystemReservedText = src.SystemReservedText,
        PressureText = src.PressureText
    };

    private List<DiskInfo> BuildDiskList()
    {
        var result = new List<DiskInfo>();
        string? systemDev = FindSystemBlockDevice();
        var swapDevices = ReadSwapDevices();   // /proc/swaps -> set of backing device names
        string? ioPressure = LinuxPressure.Io();

        try
        {
            foreach (var dir in Directory.GetDirectories(SysBlockRoot))
            {
                string dev = Path.GetFileName(dir);
                if (IsIgnoredDevice(dev)) continue;

                string? model = ReadTrimmed(Path.Combine(dir, "device", "model"));
                string? rotational = ReadTrimmed(Path.Combine(dir, "queue", "rotational"));
                string? sizeSectors = ReadTrimmed(Path.Combine(dir, "size"));

                string type = dev.StartsWith("nvme") ? "NVMe SSD"
                            : rotational == "0" ? "SSD"
                            : rotational == "1" ? "HDD"
                            : "Unknown";

                string capacity = "-";
                if (long.TryParse(sizeSectors, out long sectors) && sectors > 0)
                {
                    double gb = sectors * DiskstatsSectorBytes / (1024.0 * 1024 * 1024);
                    capacity = $"{Math.Round(gb, 0)} GB";
                }

                result.Add(new DiskInfo
                {
                    DeviceId = dev,
                    DiskName = dev,
                    ModelName = model ?? "Generic Storage Device",
                    Type = type,
                    Capacity = capacity,
                    Formatted = capacity,
                    SystemDisk = dev == systemDev ? "Yes" : "No",
                    PageFile = HasSwapOnDevice(swapDevices, dev) ? "Yes" : "No",
                    UsedText = GetUsedText(dev),
                    FreeText = GetFreeText(dev),
                    PressureText = ioPressure
                });
            }
        }
        catch { }

        return result;
    }

    // /proc/swaps: "Filename Type Size Used Priority" — the backing device may be
    // a partition (sda2), a whole disk (sdb), or a zram device. We mark any disk
    // whose name is a prefix of a swap file device.
    private static HashSet<string> ReadSwapDevices()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists("/proc/swaps")) return set;
            foreach (string line in File.ReadAllLines("/proc/swaps"))
            {
                if (line.StartsWith("Filename", StringComparison.OrdinalIgnoreCase)) continue;
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string file = line[..space].Trim();
                if (!file.StartsWith("/dev/", StringComparison.Ordinal)) continue;
                set.Add(Path.GetFileName(file));
            }
        }
        catch { }
        return set;
    }

    private static bool HasSwapOnDevice(HashSet<string> swapDevices, string dev)
    {
        foreach (string s in swapDevices)
        {
            if (s == dev || s.StartsWith(dev, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private void FillSample(DiskInfo info, string dev, DateTime now)
    {
        if (!File.Exists("/proc/diskstats"))
        {
            info.NotSupported = true;
            info.NotSupportedMessage = "I/O counters unavailable (/proc/diskstats)";
            return;
        }

        string? line = File.ReadLines("/proc/diskstats").FirstOrDefault(l =>
        {
            var cols = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return cols.Length >= 14 && cols[2] == dev;
        });
        if (line == null)
        {
            info.NotSupported = true;
            info.NotSupportedMessage = "No /proc/diskstats entry for this device";
            return;
        }

        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 14) return;

        long readsCompleted = long.Parse(p[3]);
        long sectorsRead = long.Parse(p[5]);
        long readTimeMs = long.Parse(p[6]);
        long writesCompleted = long.Parse(p[7]);
        long sectorsWritten = long.Parse(p[9]);
        long writeTimeMs = long.Parse(p[10]);
        long ioTicksMs = long.Parse(p[12]);

        long readBytes = sectorsRead * DiskstatsSectorBytes;
        long writeBytes = sectorsWritten * DiskstatsSectorBytes;

        var st = GetOrCreateState(dev);

        if (st.HaveBaseline)
        {
            long dRead = Math.Max(0, readBytes - st.LastReadBytes);
            long dWrite = Math.Max(0, writeBytes - st.LastWriteBytes);
            long dIoTicks = Math.Max(0, ioTicksMs - st.LastIoTicksMs);
            long dReads = Math.Max(0, readsCompleted - st.LastReadsCompleted);
            long dWrites = Math.Max(0, writesCompleted - st.LastWritesCompleted);
            long dReadTime = Math.Max(0, readTimeMs - st.LastReadTimeMs);
            long dWriteTime = Math.Max(0, writeTimeMs - st.LastWriteTimeMs);

            double elapsedMs = (now - st.LastSampleUtc).TotalMilliseconds;
            if (elapsedMs < 200)
            {
                // Too soon after the baseline sample: deltas are unrepresentative
                // (rates would look like 0 KB/s with 100% active). Skip this tick
                // and let the next sample produce meaningful values.
                st.LastReadBytes = readBytes;
                st.LastWriteBytes = writeBytes;
                st.LastIoTicksMs = ioTicksMs;
                st.LastReadsCompleted = readsCompleted;
                st.LastWritesCompleted = writesCompleted;
                st.LastReadTimeMs = readTimeMs;
                st.LastWriteTimeMs = writeTimeMs;
                st.LastSampleUtc = now;
                return;
            }

            info.ReadBytesPerSec = dRead / (elapsedMs / 1000.0);
            info.WriteBytesPerSec = dWrite / (elapsedMs / 1000.0);
            info.ActivePercent = Math.Clamp(dIoTicks / elapsedMs * 100.0, 0, 100);

            long dIoCount = dReads + dWrites;
            long dIoTimeMs = dReadTime + dWriteTime;
            info.ResponseMs = dIoCount > 0 ? (double)dIoTimeMs / dIoCount : 0;
        }

        st.LastReadBytes = readBytes;
        st.LastWriteBytes = writeBytes;
        st.LastIoTicksMs = ioTicksMs;
        st.LastReadsCompleted = readsCompleted;
        st.LastWritesCompleted = writesCompleted;
        st.LastReadTimeMs = readTimeMs;
        st.LastWriteTimeMs = writeTimeMs;
        st.LastSampleUtc = now;
        st.HaveBaseline = true;
    }

    private DeviceState GetOrCreateState(string dev)
    {
        if (!_state.TryGetValue(dev, out var st))
        {
            st = new DeviceState();
            _state[dev] = st;
        }
        return st;
    }

    private static bool IsIgnoredDevice(string dev)
    {
        if (dev.StartsWith("loop")) return true;
        if (dev.StartsWith("ram")) return true;
        if (dev.StartsWith("dm-")) return true;
        if (dev.StartsWith("sr")) return true;
        if (dev.StartsWith("zram")) return true;
        return false;
    }

    private static string? ReadTrimmed(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static string? FindSystemBlockDevice()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/mounts"))
            {
                var cols = line.Split(' ');
                if (cols.Length < 2 || cols[1] != "/") continue;
                if (!cols[0].StartsWith("/dev/")) continue;

                string name = Path.GetFileName(cols[0]);
                foreach (var block in Directory.GetDirectories(SysBlockRoot).Select(Path.GetFileName))
                {
                    if (block != null && (name == block || name.StartsWith(block)))
                        return block;
                }
            }
        }
        catch { }
        return null;
    }

    // Locate the first mount point that belongs to this block device.
    private static string? FindMountPoint(string dev)
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/mounts"))
            {
                var cols = line.Split(' ');
                if (cols.Length < 2 || !cols[0].StartsWith("/dev/")) continue;
                string name = Path.GetFileName(cols[0]);
                if (name == dev || name.StartsWith(dev))
                {
                    string mp = cols[1];
                    if (mp == "/") return "/";
                    // Skip virtual filesystems that share the same device name prefix.
                    if (mp.StartsWith("/dev/") || mp.StartsWith("/proc/") || mp.StartsWith("/sys/"))
                        continue;
                    return mp;
                }
            }
        }
        catch { }
        return null;
    }

    // DriveInfo on Linux names drives by mount point; "/" stays "/" (never stripped).
    private static bool DriveNameMatches(string driveName, string mountPoint)
    {
        string name = driveName.TrimEnd('/');
        if (name.Length == 0) return mountPoint == "/";
        return name == mountPoint;
    }

    private static string? GetUsedText(string dev)
    {
        string? mp = FindMountPoint(dev);
        if (mp == null) return null;
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!DriveNameMatches(d.Name, mp)) continue;
                if (!d.IsReady || d.TotalSize <= 0) return null;
                double total = d.TotalSize / (1024.0 * 1024 * 1024);
                double used = (d.TotalSize - d.AvailableFreeSpace) / (1024.0 * 1024 * 1024);
                return $"{Math.Round(used, 1)} GB ({used / total * 100:F1}%)";
            }
        }
        catch { }
        return null;
    }

    private static string? GetFreeText(string dev)
    {
        string? mp = FindMountPoint(dev);
        if (mp == null) return null;
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!DriveNameMatches(d.Name, mp)) continue;
                if (!d.IsReady || d.TotalSize <= 0) return null;
                double total = d.TotalSize / (1024.0 * 1024 * 1024);
                double free = d.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                return $"{Math.Round(free, 1)} GB ({free / total * 100:F1}%)";
            }
        }
        catch { }
        return null;
    }
}