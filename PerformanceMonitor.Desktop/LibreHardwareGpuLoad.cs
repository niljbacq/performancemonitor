using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;
using TaskManager.Providers;

namespace TaskManager;

/// <summary>
/// Reads GPU load through LibreHardwareMonitorLib (AMD ADL / ADLX, NVML, Intel and
/// D3D counters) for adapters the Windows perf counters left empty. Only the GPU
/// group is enabled, so no kernel driver is loaded and no elevation is needed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LibreHardwareGpuLoad
{
    private static readonly object Lock = new();
    private static Computer? _computer;
    private static bool _failed;

    public static void Register() => ExternalGpuLoad.Fill = Fill;

    private static void Fill(IReadOnlyList<GpuInfo> gpus)
    {
        lock (Lock)
        {
            if (_failed) return;
            try
            {
                if (_computer == null)
                {
                    _computer = new Computer { IsGpuEnabled = true };
                    _computer.Open();
                }

                var readings = new List<(string Vendor, string Name, double Load)>();
                foreach (var hw in _computer.Hardware)
                {
                    string? vendor = hw.HardwareType switch
                    {
                        HardwareType.GpuAmd => "AMD",
                        HardwareType.GpuNvidia => "NVIDIA",
                        HardwareType.GpuIntel => "Intel",
                        _ => null
                    };
                    if (vendor == null) continue;

                    hw.Update();
                    double? load = ReadLoad(hw);
                    if (load != null) readings.Add((vendor, hw.Name, load.Value));
                }

                var used = new HashSet<int>();

                // Pass 1: same vendor and the model names agree
                // ("AMD Radeon(TM) 740M Graphics" vs "AMD Radeon 740M Graphics").
                foreach (var gpu in gpus)
                {
                    string key = Normalize(gpu.Name);
                    for (int i = 0; i < readings.Count; i++)
                    {
                        if (used.Contains(i)) continue;
                        var r = readings[i];
                        if (!SameVendor(gpu.Vendor, r.Vendor)) continue;
                        string other = Normalize(r.Name);
                        if (key.Length == 0 || other.Length == 0) continue;
                        if (key.Contains(other) || other.Contains(key))
                        {
                            gpu.Utilization = Math.Round(r.Load, 1);
                            gpu.UtilizationText = null;
                            used.Add(i);
                            break;
                        }
                    }
                }

                // Pass 2: one unmatched adapter and one unused reading of the same
                // vendor can only be each other.
                foreach (var gpu in gpus.Where(g => g.Utilization == null))
                {
                    var candidates = Enumerable.Range(0, readings.Count)
                        .Where(i => !used.Contains(i) && SameVendor(gpu.Vendor, readings[i].Vendor))
                        .ToList();
                    int sameVendorGpus = gpus.Count(g => g.Utilization == null && SameVendor(g.Vendor, gpu.Vendor));
                    if (candidates.Count == 1 && sameVendorGpus == 1)
                    {
                        gpu.Utilization = Math.Round(readings[candidates[0]].Load, 1);
                        gpu.UtilizationText = null;
                        used.Add(candidates[0]);
                    }
                }
            }
            catch
            {
                // Library or driver unavailable: stop retrying every sample.
                _failed = true;
            }
        }
    }

    // "GPU Core" is the vendor-reported busy percentage; "D3D 3D" (the Windows 3D
    // engine counter) is the fallback when the driver has no core load sensor.
    private static double? ReadLoad(IHardware hw)
    {
        var sensors = hw.Sensors.Concat(hw.SubHardware.SelectMany(s =>
        {
            s.Update();
            return s.Sensors;
        })).Where(s => s.SensorType == SensorType.Load && s.Value.HasValue).ToList();

        foreach (string wanted in new[] { "GPU Core", "D3D 3D" })
        {
            var hit = sensors.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (hit?.Value is float v) return Math.Clamp(v, 0, 100);
        }
        return null;
    }

    private static bool SameVendor(string? a, string? b)
        => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string s = name.ToLowerInvariant().Replace("(tm)", "").Replace("(r)", "");
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }
}
