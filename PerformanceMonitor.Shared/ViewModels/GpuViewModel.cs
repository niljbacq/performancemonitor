using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManager.ViewModels;

public partial class GpuViewModel : ViewModelBase
{
    private readonly List<PerformanceCounter> _winGpuCounters = new();

    private string _gpuName = "Detecting GPU...";
    public string GpuName
    {
        get => _gpuName;
        set => SetProperty(ref _gpuName, value);
    }

    private string _utilization = "0%";
    public string Utilization
    {
        get => _utilization;
        set => SetProperty(ref _utilization, value);
    }

    private double _utilizationValue = 0;
    public double UtilizationValue
    {
        get => _utilizationValue;
        set => SetProperty(ref _utilizationValue, value);
    }

    private string _temperature = "N/A";
    public string Temperature
    {
        get => _temperature;
        set => SetProperty(ref _temperature, value);
    }

    private string _driverVersion = "Unknown";
    public string DriverVersion
    {
        get => _driverVersion;
        set => SetProperty(ref _driverVersion, value);
    }

    private string _driverDate = "Unknown";
    public string DriverDate
    {
        get => _driverDate;
        set => SetProperty(ref _driverDate, value);
    }

    private string _directXVersion = "N/A";
    public string DirectXVersion
    {
        get => _directXVersion;
        set => SetProperty(ref _directXVersion, value);
    }

    private string _physicalLocation = "Unknown";
    public string PhysicalLocation
    {
        get => _physicalLocation;
        set => SetProperty(ref _physicalLocation, value);
    }

    // Fixed: Added missing properties expected by MainWindow.axaml
    private string _memoryUsage = "N/A";
    public string MemoryUsage
    {
        get => _memoryUsage;
        set => SetProperty(ref _memoryUsage, value);
    }

    private string _sharedMemoryUsage = "N/A";
    public string SharedMemoryUsage
    {
        get => _sharedMemoryUsage;
        set => SetProperty(ref _sharedMemoryUsage, value);
    }

    public GpuViewModel()
    {
        InitCounters();
        _ = LoadGpuSpecsAsync();
        _ = StartMonitoringAsync();
    }

    private void InitCounters()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                foreach (var instance in category.GetInstanceNames())
                {
                    if (instance.EndsWith("engtype_3D"))
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance);
                        counter.NextValue();
                        _winGpuCounters.Add(counter);
                    }
                }
            }
            catch { }
        }
    }

    private async Task LoadGpuSpecsAsync()
    {
        await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LoadWindowsSpecs();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                LoadLinuxSpecs();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                LoadMacSpecs();
            }
        });
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int tick = 0;

        // Resume off the UI thread: on Windows/Linux this path can spawn an
        // nvidia-smi (or lspci) subprocess, which previously ran synchronously
        // on the UI thread every second - a major source of the drag-lag.
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            tick++;

            // Spawning an external process every single second is wasteful even
            // off the UI thread; every other tick is plenty for a live readout.
            if (tick % 2 != 1) continue;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    UpdateWindowsMetrics();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    UpdateLinuxMetrics();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    UpdateMacMetrics();
                }
            }
            catch { }
        }
    }

    private void LoadWindowsSpecs()
    {
        string nvidiaName = ExecuteCommand("nvidia-smi", "--query-gpu=name,driver_version --format=csv,noheader");
        if (!string.IsNullOrWhiteSpace(nvidiaName) && nvidiaName.Contains(","))
        {
            var parts = nvidiaName.Split(',');
            GpuName = parts[0].Trim();
            DriverVersion = parts[1].Trim();
            DirectXVersion = "DirectX 12";
            PhysicalLocation = "PCI bus 1, device 0, function 0";
            return;
        }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            string selectedName = string.Empty;

            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Generic GPU";

                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    AssignWmiDetails(obj);
                    break;
                }

                if (string.IsNullOrEmpty(selectedName))
                {
                    selectedName = name;
                    AssignWmiDetails(obj);
                }
            }
        }
        catch
        {
            GpuName = "Windows Graphics Device";
        }
    }

    private void AssignWmiDetails(System.Management.ManagementObject obj)
    {
        GpuName = obj["Name"]?.ToString() ?? "Generic GPU";
        DriverVersion = obj["DriverVersion"]?.ToString() ?? "N/A";
        DirectXVersion = "DirectX 12";
        PhysicalLocation = obj["PNPDeviceID"]?.ToString() ?? "PCI Bus";

        if (DateTime.TryParse(obj["DriverDate"]?.ToString(), out var date))
            DriverDate = date.ToShortDateString();
    }

    private void UpdateWindowsMetrics()
    {
        string smiOutput = ExecuteCommand("nvidia-smi", "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits");

        if (!string.IsNullOrWhiteSpace(smiOutput) && smiOutput.Contains(","))
        {
            var parts = smiOutput.Split(',');
            double? util = parts.Length >= 2 && double.TryParse(parts[0].Trim(), out double u) ? u : null;
            double? temp = parts.Length >= 2 && double.TryParse(parts[1].Trim(), out double t) ? t : null;
            string? memoryText = parts.Length >= 4 ? $"{parts[2].Trim()} MB / {parts[3].Trim()} MB" : null;

            Dispatcher.UIThread.Post(() =>
            {
                if (util.HasValue)
                {
                    UtilizationValue = util.Value;
                    Utilization = $"{util.Value}%";
                }
                if (temp.HasValue) Temperature = $"{temp.Value} °C";
                if (memoryText != null) MemoryUsage = memoryText;
            });
            return;
        }

        if (_winGpuCounters.Count > 0)
        {
            try
            {
                float totalUtil = 0;
                foreach (var counter in _winGpuCounters)
                {
                    totalUtil += counter.NextValue();
                }

                double usage = Math.Min(Math.Round(totalUtil, 1), 100);
                Dispatcher.UIThread.Post(() =>
                {
                    UtilizationValue = usage;
                    Utilization = $"{usage}%";
                });
            }
            catch { }
        }
    }

    private void LoadLinuxSpecs()
    {
        string nameOutput = ExecuteCommand("nvidia-smi", "--query-gpu=name,driver_version --format=csv,noheader");
        if (!string.IsNullOrWhiteSpace(nameOutput) && nameOutput.Contains(","))
        {
            var parts = nameOutput.Split(',');
            GpuName = parts[0].Trim();
            DriverVersion = parts[1].Trim();
            DirectXVersion = "Vulkan / OpenGL";
            PhysicalLocation = "/dev/nvidia0";
            return;
        }

        string lspciOutput = ExecuteCommand("lspci", "");
        foreach (var line in lspciOutput.Split('\n'))
        {
            if (line.Contains("VGA compatible controller") || line.Contains("3D controller"))
            {
                GpuName = line.Substring(line.IndexOf(':') + 1).Trim();
                DirectXVersion = "Vulkan / OpenGL";
                PhysicalLocation = line.Split(' ')[0];
                break;
            }
        }
    }

    private void UpdateLinuxMetrics()
    {
        string smiResult = ExecuteCommand("nvidia-smi", "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits");
        if (!string.IsNullOrWhiteSpace(smiResult) && smiResult.Contains(","))
        {
            var parts = smiResult.Split(',');
            if (double.TryParse(parts[0].Trim(), out double util) && double.TryParse(parts[1].Trim(), out double temp))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UtilizationValue = util;
                    Utilization = $"{util}%";
                    Temperature = $"{temp} °C";
                });
                return;
            }
        }

        try
        {
            if (File.Exists("/sys/class/drm/card0/device/gpu_busy_percent"))
            {
                string busyText = File.ReadAllText("/sys/class/drm/card0/device/gpu_busy_percent").Trim();
                if (double.TryParse(busyText, out double util))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        UtilizationValue = util;
                        Utilization = $"{util}%";
                    });
                }
            }
        }
        catch { }
    }

    private void LoadMacSpecs()
    {
        string profilerOutput = ExecuteCommand("system_profiler", "SPDisplaysDataType");
        foreach (var line in profilerOutput.Split('\n'))
        {
            if (line.Contains("Chipset Model:"))
            {
                GpuName = line.Replace("Chipset Model:", "").Trim();
                DirectXVersion = "Metal";
                PhysicalLocation = "Integrated / Apple Silicon";
                break;
            }
        }
    }

    private void UpdateMacMetrics()
    {
        Dispatcher.UIThread.Post(() => Utilization = "Active");
    }

    private string ExecuteCommand(string command, string arguments)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string result = process.StandardOutput.ReadToEnd();
            process.WaitForExit(500);
            return result;
        }
        catch
        {
            return string.Empty;
        }
    }
}