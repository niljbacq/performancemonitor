using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Hardware.Info;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManager.ViewModels;

public partial class DiskViewModel : ViewModelBase
{
    private readonly HardwareInfo _hardwareInfo = new();
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private PerformanceCounter? _idleTimeCounter;
    private PerformanceCounter? _responseTimeCounter;

    private string _diskName = "Disk";
    public string DiskName
    {
        get => _diskName;
        set => SetProperty(ref _diskName, value);
    }

    private string _modelName = "Detecting Disk...";
    public string ModelName
    {
        get => _modelName;
        set => SetProperty(ref _modelName, value);
    }

    // Dynamic Live Stats
    private string _activeTime = "0%";
    public string ActiveTime
    {
        get => _activeTime;
        set => SetProperty(ref _activeTime, value);
    }

    private double _activeTimeValue = 0;
    public double ActiveTimeValue
    {
        get => _activeTimeValue;
        set => SetProperty(ref _activeTimeValue, value);
    }

    private string _averageResponseTime = "0.0 ms";
    public string AverageResponseTime
    {
        get => _averageResponseTime;
        set => SetProperty(ref _averageResponseTime, value);
    }

    private string _readSpeed = "0 KB/s";
    public string ReadSpeed
    {
        get => _readSpeed;
        set => SetProperty(ref _readSpeed, value);
    }

    private string _writeSpeed = "0 KB/s";
    public string WriteSpeed
    {
        get => _writeSpeed;
        set => SetProperty(ref _writeSpeed, value);
    }

    // Hardware Technical Specs
    private string _capacity = "-";
    public string Capacity
    {
        get => _capacity;
        set => SetProperty(ref _capacity, value);
    }

    private string _formatted = "-";
    public string Formatted
    {
        get => _formatted;
        set => SetProperty(ref _formatted, value);
    }

    private string _systemDisk = "No";
    public string SystemDisk
    {
        get => _systemDisk;
        set => SetProperty(ref _systemDisk, value);
    }

    private string _pageFile = "No";
    public string PageFile
    {
        get => _pageFile;
        set => SetProperty(ref _pageFile, value);
    }

    private string _type = "Unknown";
    public string Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    public DiskViewModel()
    {
        InitPerformanceCounters();
        _ = LoadDiskSpecsAsync();
        _ = StartMonitoringAsync();
    }

    private void InitPerformanceCounters()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                _idleTimeCounter = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
                _responseTimeCounter = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");

                _diskReadCounter.NextValue();
                _diskWriteCounter.NextValue();
                _idleTimeCounter.NextValue();
                _responseTimeCounter.NextValue();
            }
            catch { }
        }
    }

    private async Task LoadDiskSpecsAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // Drive Info & Capacity
                DriveInfo rootDrive = DriveInfo.GetDrives()
                    .FirstOrDefault(d => d.IsReady && (d.Name.StartsWith("C") || d.Name == "/"))
                    ?? DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady)!;

                if (rootDrive != null)
                {
                    double totalGB = Math.Round(rootDrive.TotalSize / (1024.0 * 1024.0 * 1024.0), 0);
                    Capacity = $"{totalGB} GB";
                    Formatted = $"{totalGB} GB";

                    string osDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                    SystemDisk = rootDrive.Name.StartsWith(osDriveLetter, StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
                }

                _hardwareInfo.RefreshDriveList();
                if (_hardwareInfo.DriveList.Count > 0)
                {
                    ModelName = _hardwareInfo.DriveList[0].Model;
                }
                else
                {
                    ModelName = GetFallbackModelName();
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    LoadWindowsDiskDetails();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    LoadLinuxDiskDetails();
                }
            }
            catch
            {
                ModelName = "Generic Storage Device";
            }
        });
    }

    private void LoadWindowsDiskDetails()
    {
        try
        {
            // Dynamic Disk Index & System Disk Check
            using var driveSearcher = new ManagementObjectSearcher("SELECT Index, DeviceID FROM Win32_DiskDrive");
            foreach (var drive in driveSearcher.Get())
            {
                string index = drive["Index"]?.ToString() ?? "0";
                DiskName = $"Disk {index}";
                break;
            }

            // Media Type Detection (SSD vs HDD)
            using var mediaSearcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (var media in mediaSearcher.Get())
            {
                ushort mediaType = Convert.ToUInt16(media["MediaType"]);
                ushort busType = Convert.ToUInt16(media["BusType"]);

                if (busType == 17) // NVMe Bus Type
                    Type = "NVMe SSD";
                else if (mediaType == 4)
                    Type = "SSD";
                else if (mediaType == 3)
                    Type = "HDD";
                else
                    Type = "SSD/HDD";
                break;
            }

            // Dynamic PageFile Detection
            using var pageFileSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileSetting");
            PageFile = pageFileSearcher.Get().Count > 0 ? "Yes" : "No";
        }
        catch
        {
            Type = "SSD";
        }
    }

    private void LoadLinuxDiskDetails()
    {
        try
        {
            DiskName = "Disk 0";

            // SSD vs HDD Check via rota
            if (File.Exists("/sys/block/sda/queue/rotational"))
            {
                string rota = File.ReadAllText("/sys/block/sda/queue/rotational").Trim();
                Type = rota == "0" ? "SSD" : "HDD";
            }
            else if (Directory.Exists("/sys/block/nvme0n1"))
            {
                Type = "NVMe SSD";
            }

            // Swap/PageFile Check
            if (File.Exists("/proc/swaps"))
            {
                string[] swaps = File.ReadAllLines("/proc/swaps");
                PageFile = swaps.Length > 1 ? "Yes" : "No";
            }
        }
        catch { }
    }

    private async Task StartMonitoringAsync()
    {
        long lastReadBytes = 0;
        long lastWriteBytes = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        // Resume off the UI thread - perf-counter reads and /proc/diskstats file IO
        // have no business blocking window/input handling.
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (_diskReadCounter != null && _diskWriteCounter != null && _idleTimeCounter != null && _responseTimeCounter != null)
                    {
                        float readBytesPerSec = _diskReadCounter.NextValue();
                        float writeBytesPerSec = _diskWriteCounter.NextValue();
                        float idlePercent = _idleTimeCounter.NextValue();
                        float avgResponseSeconds = _responseTimeCounter.NextValue();

                        double activePercent = Math.Clamp(Math.Round(100 - idlePercent), 0, 100);
                        string activeTimeText = $"{activePercent}%";
                        string responseText = $"{avgResponseSeconds * 1000.0:F1} ms";
                        string readText = FormatSpeed(readBytesPerSec);
                        string writeText = FormatSpeed(writeBytesPerSec);

                        Dispatcher.UIThread.Post(() =>
                        {
                            ActiveTimeValue = activePercent;
                            ActiveTime = activeTimeText;
                            AverageResponseTime = responseText;
                            ReadSpeed = readText;
                            WriteSpeed = writeText;
                        });
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (File.Exists("/proc/diskstats"))
                    {
                        string[] lines = File.ReadAllLines("/proc/diskstats");
                        var targetLine = lines.FirstOrDefault(l => l.Contains("sda") || l.Contains("nvme0n1"));

                        if (targetLine != null)
                        {
                            var parts = targetLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 10)
                            {
                                long readSectors = long.Parse(parts[5]);
                                long writeSectors = long.Parse(parts[9]);

                                int sectorSize = GetLinuxSectorSize();
                                long currentReadBytes = readSectors * sectorSize;
                                long currentWriteBytes = writeSectors * sectorSize;

                                if (lastReadBytes > 0 && lastWriteBytes > 0)
                                {
                                    long readDiff = Math.Max(0, currentReadBytes - lastReadBytes);
                                    // Fixed: Subtract lastWriteBytes from currentWriteBytes instead of currentReadBytes
                                    long writeDiff = Math.Max(0, currentWriteBytes - lastWriteBytes);

                                    string readText = FormatSpeed(readDiff);
                                    string writeText = FormatSpeed(writeDiff);

                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        ReadSpeed = readText;
                                        WriteSpeed = writeText;
                                    });
                                }

                                lastReadBytes = currentReadBytes;
                                lastWriteBytes = currentWriteBytes;
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }

    private int GetLinuxSectorSize()
    {
        try
        {
            if (File.Exists("/sys/block/sda/queue/hw_sector_size"))
            {
                if (int.TryParse(File.ReadAllText("/sys/block/sda/queue/hw_sector_size").Trim(), out int size))
                    return size;
            }
            else if (File.Exists("/sys/block/nvme0n1/queue/hw_sector_size"))
            {
                if (int.TryParse(File.ReadAllText("/sys/block/nvme0n1/queue/hw_sector_size").Trim(), out int size))
                    return size;
            }
        }
        catch { }
        return 512;
    }

    private string GetFallbackModelName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (File.Exists("/sys/block/sda/device/model"))
            {
                return File.ReadAllText("/sys/block/sda/device/model").Trim();
            }
            if (File.Exists("/sys/block/nvme0n1/device/model"))
            {
                return File.ReadAllText("/sys/block/nvme0n1/device/model").Trim();
            }
        }
        return "System Storage Device";
    }

    private string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
        {
            return $"{Math.Round(bytesPerSec / (1024.0 * 1024.0), 1)} MB/s";
        }
        return $"{Math.Round(bytesPerSec / 1024.0, 0)} KB/s";
    }
}