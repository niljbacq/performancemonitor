using Hardware.Info;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;

namespace TaskManager.Providers;

public abstract class DiskProviderBase : IDiskProvider
{
    private readonly HardwareInfo _hardwareInfo = new();

    public DiskSpecs GetSpecs()
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

public sealed class LinuxDiskProvider : DiskProviderBase
{
    private string? _dev;
    private bool _devResolved;
    private long _lastReadBytes, _lastWriteBytes;

    private const int DiskstatsSectorBytes = 512;

    private string? PrimaryDevice
    {
        get
        {
            if (!_devResolved)
            {
                _dev = FindPrimaryBlockDevice();
                _devResolved = true;
            }
            return _dev;
        }
    }

    protected override string GetFallbackModelName()
    {
        string? dev = PrimaryDevice;
        if (dev != null)
        {
            string path = $"/sys/block/{dev}/device/model";
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
        }
        return base.GetFallbackModelName();
    }

    protected override void LoadPlatformDetails(DiskSpecs s)
    {
        try
        {
            s.DiskName = "Disk 0";
            string dev = PrimaryDevice ?? "sda";

            string rotaPath = $"/sys/block/{dev}/queue/rotational";
            if (File.Exists(rotaPath))
                s.Type = File.ReadAllText(rotaPath).Trim() == "0" ? "SSD" : "HDD";
            else if (dev.StartsWith("nvme"))
                s.Type = "NVMe SSD";

            if (File.Exists("/proc/swaps"))
                s.PageFile = File.ReadAllLines("/proc/swaps").Length > 1 ? "Yes" : "No";
        }
        catch { }
    }

    public override DiskSample? Sample()
    {
        string? dev = PrimaryDevice;
        if (dev == null || !File.Exists("/proc/diskstats")) return null;

        string? line = File.ReadLines("/proc/diskstats").FirstOrDefault(l =>
        {
            var cols = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return cols.Length >= 3 && cols[2] == dev;
        });
        if (line == null) return null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 10) return null;

        long readBytes = long.Parse(parts[5]) * DiskstatsSectorBytes;
        long writeBytes = long.Parse(parts[9]) * DiskstatsSectorBytes;

        DiskSample? result = null;
        if (_lastReadBytes > 0 && _lastWriteBytes > 0)
        {
            result = new DiskSample
            {
                ReadBytesPerSec = Math.Max(0, readBytes - _lastReadBytes),
                WriteBytesPerSec = Math.Max(0, writeBytes - _lastWriteBytes)
            };
        }

        _lastReadBytes = readBytes;
        _lastWriteBytes = writeBytes;
        return result;
    }

    private static string? FindPrimaryBlockDevice()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/mounts"))
            {
                var cols = line.Split(' ');
                if (cols.Length < 2 || cols[1] != "/") continue;
                if (!cols[0].StartsWith("/dev/")) continue;

                string name = Path.GetFileName(cols[0]);
                foreach (var block in Directory.GetDirectories("/sys/block").Select(Path.GetFileName))
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