using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace TaskManager.Providers;

[SupportedOSPlatform("windows")]
public sealed class WindowsGpuProvider : IGpuProvider
{
    private readonly List<PerformanceCounter> _counters = new();

    public WindowsGpuProvider()
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
                    _counters.Add(counter);
                }
            }
        }
        catch { }
    }

    public GpuSpecs GetSpecs()
    {
        var s = new GpuSpecs();

        string nvidia = CommandRunner.Run("nvidia-smi", "--query-gpu=name,driver_version --format=csv,noheader");
        if (!string.IsNullOrWhiteSpace(nvidia) && nvidia.Contains(','))
        {
            var parts = nvidia.Split(',');
            s.Name = parts[0].Trim();
            s.DriverVersion = parts[1].Trim();
            s.GraphicsApi = "DirectX 12";
            s.PhysicalLocation = "PCI bus 1, device 0, function 0";
            return s;
        }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            bool picked = false;

            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Generic GPU";

                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    AssignWmi(s, obj);
                    break;
                }

                if (!picked)
                {
                    picked = true;
                    AssignWmi(s, obj);
                }
            }
        }
        catch
        {
            s.Name = "Windows Graphics Device";
        }
        return s;
    }

    private static void AssignWmi(GpuSpecs s, System.Management.ManagementObject obj)
    {
        s.Name = obj["Name"]?.ToString() ?? "Generic GPU";
        s.DriverVersion = obj["DriverVersion"]?.ToString() ?? "N/A";
        s.GraphicsApi = "DirectX 12";
        s.PhysicalLocation = obj["PNPDeviceID"]?.ToString() ?? "PCI Bus";
        if (DateTime.TryParse(obj["DriverDate"]?.ToString(), out var date))
            s.DriverDate = date.ToShortDateString();
    }

    public GpuSample? Sample()
    {
        string smi = CommandRunner.Run("nvidia-smi",
            "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits");

        if (!string.IsNullOrWhiteSpace(smi) && smi.Contains(','))
        {
            var parts = smi.Split(',');
            return new GpuSample
            {
                Utilization = parts.Length >= 2 && double.TryParse(parts[0].Trim(), out double u) ? u : null,
                TemperatureC = parts.Length >= 2 && double.TryParse(parts[1].Trim(), out double t) ? t : null,
                MemoryUsage = parts.Length >= 4 ? $"{parts[2].Trim()} MB / {parts[3].Trim()} MB" : null
            };
        }

        if (_counters.Count > 0)
        {
            float total = 0;
            foreach (var c in _counters) total += c.NextValue();
            return new GpuSample { Utilization = Math.Min(Math.Round(total, 1), 100) };
        }

        return null;
    }
}

public sealed class LinuxGpuProvider : IGpuProvider
{
    public GpuSpecs GetSpecs()
    {
        var s = new GpuSpecs();

        string nvidia = CommandRunner.Run("nvidia-smi", "--query-gpu=name,driver_version --format=csv,noheader");
        if (!string.IsNullOrWhiteSpace(nvidia) && nvidia.Contains(','))
        {
            var parts = nvidia.Split(',');
            s.Name = parts[0].Trim();
            s.DriverVersion = parts[1].Trim();
            s.GraphicsApi = "Vulkan / OpenGL";
            s.PhysicalLocation = "/dev/nvidia0";
            return s;
        }

        foreach (var line in CommandRunner.Run("lspci", "").Split('\n'))
        {
            if (line.Contains("VGA compatible controller") || line.Contains("3D controller"))
            {
                s.Name = line.Substring(line.IndexOf(':') + 1).Trim();
                s.GraphicsApi = "Vulkan / OpenGL";
                s.PhysicalLocation = line.Split(' ')[0];
                break;
            }
        }
        return s;
    }

    public GpuSample? Sample()
    {
        string smi = CommandRunner.Run("nvidia-smi",
            "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits");
        if (!string.IsNullOrWhiteSpace(smi) && smi.Contains(','))
        {
            var parts = smi.Split(',');
            if (double.TryParse(parts[0].Trim(), out double util) && double.TryParse(parts[1].Trim(), out double temp))
                return new GpuSample { Utilization = util, TemperatureC = temp };
        }

        double? gpuUtil = null;

        // Primary: standard amdgpu sysfs path
        try
        {
            const string path = "/sys/class/drm/card0/device/gpu_busy_percent";
            if (File.Exists(path) && double.TryParse(File.ReadAllText(path).Trim(), out double busy))
                gpuUtil = busy;
        }
        catch { }

        // Fallback: debugfs amdgpu_pm_info (newer kernels, sometimes readable when sysfs is not)
        if (gpuUtil is null)
        {
            try
            {
                const string debugPath = "/sys/kernel/debug/dri/0/amdgpu_pm_info";
                if (File.Exists(debugPath))
                {
                    foreach (var line in File.ReadAllLines(debugPath))
                    {
                        if (line.Contains("GPU Load", StringComparison.OrdinalIgnoreCase))
                        {
                            var tokens = line.Split(new[] { ':', '%', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var token in tokens)
                            {
                                if (double.TryParse(token.Trim(), out double v) && v >= 0 && v <= 100)
                                {
                                    gpuUtil = v;
                                    break;
                                }
                            }
                            if (gpuUtil is not null) break;
                        }
                    }
                }
            }
            catch { }  // debugfs is often root-only; ignore failures
        }

        return gpuUtil is double u ? new GpuSample { Utilization = u } : null;
    }
}
