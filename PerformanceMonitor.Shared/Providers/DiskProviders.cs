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
public sealed class WindowsDiskProvider : DiskProviderBase
{
    private PerformanceCounter? _read, _write, _idle, _response;

    public WindowsDiskProvider()
    {
        try
        {
            _read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            _write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            _idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
            _response = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");

            _read.NextValue();
            _write.NextValue();
            _idle.NextValue();
            _response.NextValue();
        }
        catch { }
    }

    protected override bool IsSystemDrive(DriveInfo drive)
    {
        string osDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        return drive.Name.StartsWith(osDriveLetter, StringComparison.OrdinalIgnoreCase);
    }

    protected override void LoadPlatformDetails(DiskSpecs s)
    {
        try
        {
            using var driveSearcher = new ManagementObjectSearcher("SELECT Index, DeviceID FROM Win32_DiskDrive");
            foreach (var drive in driveSearcher.Get())
            {
                s.DiskName = $"Disk {drive["Index"]?.ToString() ?? "0"}";
                break;
            }

            using var mediaSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (var media in mediaSearcher.Get())
            {
                ushort mediaType = Convert.ToUInt16(media["MediaType"]);
                ushort busType = Convert.ToUInt16(media["BusType"]);

                s.Type = busType == 17 ? "NVMe SSD"
                       : mediaType == 4 ? "SSD"
                       : mediaType == 3 ? "HDD"
                       : "SSD/HDD";
                break;
            }

            using var pageFileSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileSetting");
            s.PageFile = pageFileSearcher.Get().Count > 0 ? "Yes" : "No";
        }
        catch
        {
            s.Type = "SSD";
        }
    }

    public override DiskSample? Sample()
    {
        if (_read == null || _write == null || _idle == null || _response == null) return null;

        return new DiskSample
        {
            ReadBytesPerSec = _read.NextValue(),
            WriteBytesPerSec = _write.NextValue(),
            ActivePercent = Math.Clamp(Math.Round(100 - _idle.NextValue()), 0, 100),
            ResponseMs = _response.NextValue() * 1000.0
        };
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
            SystemReservedText = primary.SystemReservedText
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
        SystemReservedText = src.SystemReservedText
    };

    private List<DiskInfo> BuildDiskList()
    {
        var result = new List<DiskInfo>();
        string? systemDev = FindSystemBlockDevice();
        bool hasSwap = File.Exists("/proc/swaps") && File.ReadAllLines("/proc/swaps").Length > 1;

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
                    PageFile = hasSwap ? "Yes" : "No"
                });
            }
        }
        catch { }

        return result;
    }

    private void FillSample(DiskInfo info, string dev, DateTime now)
    {
        if (!File.Exists("/proc/diskstats"))
        {
            info.NotSupported = true;
            info.NotSupportedMessage = "N/A";
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
            info.NotSupportedMessage = "N/A";
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
            if (elapsedMs < 100) elapsedMs = 1000;

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
}
    