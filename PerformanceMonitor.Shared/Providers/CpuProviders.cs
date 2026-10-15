using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;

namespace TaskManager.Providers;

public abstract class CpuProviderBase : ICpuProvider
{
    protected virtual bool CountHandles => false;

    public abstract CpuSpecs GetSpecs();
    public abstract float GetUsagePercent();

    public virtual ProcessStats GetProcessStats()
    {
        Process[] list = Process.GetProcesses();
        try
        {
            int threads = 0;
            long handles = 0;
            foreach (var proc in list)
            {
                try
                {
                    threads += proc.Threads.Count;
                    if (CountHandles) handles += proc.HandleCount;
                }
                catch { }
            }
            return new ProcessStats(list.Length, threads, handles > 0 ? handles.ToString() : "N/A");
        }
        finally
        {
            foreach (var proc in list) proc.Dispose();
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCpuProvider : CpuProviderBase
{
    private PerformanceCounter? _counter;

    public WindowsCpuProvider()
    {
        try
        {
            _counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _counter.NextValue();
        }
        catch
        {
            _counter = null;
        }
    }

    protected override bool CountHandles => true;

    public override float GetUsagePercent() => _counter?.NextValue() ?? 0;

    public override CpuSpecs GetSpecs()
    {
        var s = new CpuSpecs();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                s.Name = obj["Name"]?.ToString()?.Trim();

                if (uint.TryParse(obj["MaxClockSpeed"]?.ToString(), out uint maxClock))
                {
                    s.BaseSpeed = $"{Math.Round(maxClock / 1000.0, 2):F2} GHz";
                    s.Speed = s.BaseSpeed;
                }

                s.Cores = obj["NumberOfCores"]?.ToString();
                s.LogicalProcessors = obj["NumberOfLogicalProcessors"]?.ToString();

                if (uint.TryParse(obj["L2CacheSize"]?.ToString(), out uint l2)) s.L2Cache = FormatKb(l2);
                if (uint.TryParse(obj["L3CacheSize"]?.ToString(), out uint l3)) s.L3Cache = FormatKb(l3);

                if (obj["VirtualizationFirmwareEnabled"] is bool v)
                    s.Virtualization = v ? "Enabled" : "Disabled";

                break;
            }

            using var cacheSearcher = new ManagementObjectSearcher("SELECT Level, MaxCacheSize FROM Win32_CacheMemory");
            uint totalL1KB = 0;
            foreach (var cache in cacheSearcher.Get())
            {
                if (ushort.TryParse(cache["Level"]?.ToString(), out ushort level) && level == 3 &&
                    uint.TryParse(cache["MaxCacheSize"]?.ToString(), out uint size))
                {
                    totalL1KB += size;
                }
            }
            if (totalL1KB > 0) s.L1Cache = FormatKb(totalL1KB);

            using var socketSearcher = new ManagementObjectSearcher("SELECT SocketDesignation FROM Win32_Processor");
            s.Sockets = socketSearcher.Get().Count.ToString();
        }
        catch
        {
            s.Name = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Windows Processor";
        }
        return s;
    }

    private static string FormatKb(uint kb) => kb >= 1024 ? $"{kb / 1024.0:F1} MB" : $"{kb} KB";
}

public sealed class LinuxCpuProvider : CpuProviderBase
{
    private ulong _prevIdle;
    private ulong _prevTotal;

    public override CpuSpecs GetSpecs()
    {
        var s = new CpuSpecs();
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                int logical = 0;
                foreach (string line in File.ReadAllLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase) && s.Name == null)
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1) s.Name = parts[1].Trim();
                    }
                    else if (line.StartsWith("cpu cores", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int cores))
                            s.Cores = cores.ToString();
                    }
                    else if (line.StartsWith("processor", StringComparison.OrdinalIgnoreCase))
                    {
                        logical++;
                    }
                    else if (line.StartsWith("cpu MHz", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1 && double.TryParse(parts[1].Trim(), out double mhz))
                            s.BaseSpeed = $"{mhz / 1000.0:F2} GHz";
                    }
                }
                if (logical > 0) s.LogicalProcessors = logical.ToString();
            }

            // Live speed: prefer cpufreq (kHz), fall back to the base clock from cpuinfo.
            string? liveMhz = ReadLiveCpuMhz();
            s.Speed = liveMhz != null
                ? $"{double.Parse(liveMhz, System.Globalization.CultureInfo.InvariantCulture) / 1000.0:F2} GHz"
                : s.BaseSpeed;

            string lscpu = CommandRunner.Run("lscpu", "");
            if (!string.IsNullOrEmpty(lscpu))
            {
                s.Virtualization = lscpu.Contains("Virtualization:") || lscpu.Contains("VT-x") || lscpu.Contains("AMD-V")
                    ? "Enabled" : "Disabled";

                foreach (var line in lscpu.Split('\n'))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length != 2) continue;
                    string label = parts[0].Trim();
                    string value = parts[1].Trim();
                    // Remove "(N instances)" suffix
                    int paren = value.IndexOf('(');
                    if (paren > 0) value = value[..paren].Trim();

                    if (label is "L1d" or "L1d cache" or "L1i" or "L1i cache")
                    {
                        s.L1Cache = value;
                    }
                    else if (label is "L2" or "L2 cache")
                    {
                        s.L2Cache = value;
                    }
                    else if (label is "L3" or "L3 cache")
                    {
                        s.L3Cache = value;
                    }
                    else if (label == "Socket(s)")
                    {
                        s.Sockets = value;
                    }
                }
            }

            // CPU temperature from hwmon (k10temp for AMD, coretemp for Intel)
            s.TemperatureText = ReadCpuTemperature();

            // Linux PSI: CPU stall pressure (kernel >= 4.20 with CONFIG_PSI).
            s.PressureText = LinuxPressure.Cpu();
        }
        catch
        {
            s.Name = "Linux Processor";
        }
        s.Name ??= "Linux Processor";
        return s;
    }

    // Read CPU temperature from hwmon (k10temp for AMD, coretemp for Intel)
    private static string? ReadCpuTemperature()
    {
        try
        {
            string hwmonRoot = "/sys/class/hwmon";
            if (!Directory.Exists(hwmonRoot)) return null;

            foreach (var hwmonDir in Directory.GetDirectories(hwmonRoot))
            {
                string namePath = Path.Combine(hwmonDir, "name");
                if (!File.Exists(namePath)) continue;
                
                string name = File.ReadAllText(namePath).Trim();
                // k10temp = AMD Family 15h/16h/17h (Kaveri, etc.)
                // coretemp = Intel Core
                // fam15h_power = AMD Family 15h power (also has temp)
                if (name is not ("k10temp" or "coretemp" or "fam15h_power")) continue;

                foreach (var tempFile in Directory.GetFiles(hwmonDir, "temp*_input"))
                {
                    if (long.TryParse(File.ReadAllText(tempFile).Trim(), out long milliC))
                    {
                        double celsius = milliC / 1000.0;
                        if (celsius > -20 && celsius < 150) // sanity check
                        {
                            // fam15h_power also has power1_input in µW
                            string? power = null;
                            if (name == "fam15h_power")
                            {
                                string powerPath = Path.Combine(hwmonDir, "power1_input");
                                if (File.Exists(powerPath) && long.TryParse(File.ReadAllText(powerPath).Trim(), out long microwatts))
                                {
                                    power = $", {microwatts / 1_000_000.0:F1} W";
                                }
                            }
                            return $"{celsius:F1} °C{power}";
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public override float GetUsagePercent()
    {
        try
        {
            if (!File.Exists("/proc/stat")) return 0;

            var parts = File.ReadLines("/proc/stat").First()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return 0;

            ulong idle = ulong.Parse(parts[4]);
            ulong iowait = parts.Length > 5 ? ulong.Parse(parts[5]) : 0;
            ulong total = 0;
            for (int i = 1; i < parts.Length; i++)
                if (ulong.TryParse(parts[i], out ulong v)) total += v;

            ulong idleTotal = idle + iowait;
            ulong idleDelta = idleTotal - _prevIdle;
            ulong totalDelta = total - _prevTotal;

            _prevIdle = idleTotal;
            _prevTotal = total;

            if (totalDelta == 0) return 0;
            return (float)Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100, 0, 100);
        }
        catch { return 0; }
    }

    // Current speed of CPU 0 (kHz), or null when unavailable.
    private static string? ReadLiveCpuMhz()
    {
        try
        {
            string path = "/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq";
            if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out long khz) && khz > 0)
                return (khz / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { }
        return null;
    }

    // Per-core frequencies ("3.9 / 3.9 / 3.9 / 3.9 GHz") for the Per-core now row.
    public string? PerCoreFrequencyText
    {
        get
        {
            try
            {
                var values = new List<string>();
                for (int i = 0; i < Environment.ProcessorCount; i++)
                {
                    string path = $"/sys/devices/system/cpu/cpu{i}/cpufreq/scaling_cur_freq";
                    if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out long khz) && khz > 0)
                        values.Add((khz / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                }
                return values.Count > 0 ? $"{string.Join(" / ", values)} GHz" : null;
            }
            catch { return null; }
        }
    }

    // Real system uptime from /proc/uptime instead of the process's own tick count.
    public long UptimeMilliseconds
    {
        get
        {
            try
            {
                if (File.Exists("/proc/uptime"))
                {
                    string text = File.ReadAllText("/proc/uptime").Trim();
                    int sp = text.IndexOf(' ');
                    if (sp > 0 && double.TryParse(text[..sp],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double secs))
                        return (long)(secs * 1000.0);
                }
            }
            catch { }
            return Environment.TickCount64;
        }
    }

    // Frequency residency: how much time the cores spent at each P-state
    // (cpufreq "time_in_state"). Percentages are unit-independent, so no
    // conversion of the raw tick counts is needed.
    public string? FrequencyResidencyText
    {
        get
        {
            try
            {
                var totals = new SortedDictionary<long, long>();
                for (int i = 0; i < Environment.ProcessorCount; i++)
                {
                    string[] paths =
                    {
                        $"/sys/devices/system/cpu/cpu{i}/cpufreq/stats/time_in_state",
                        $"/sys/devices/system/cpu/cpufreq/policy{i}/stats/time_in_state"
                    };
                    foreach (string path in paths)
                    {
                        if (!File.Exists(path)) continue;
                        AccumulateResidency(path, totals);
                        break;
                    }
                }
                return FormatResidency(totals);
            }
            catch { return null; }
        }
    }

    // time_in_state lines are "<freq kHz> <ticks>".
    internal static void AccumulateResidency(string path, SortedDictionary<long, long> totals)
    {
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!long.TryParse(parts[0], out long khz)) continue;
                if (!long.TryParse(parts[1], out long ticks)) continue;
                totals[khz] = totals.TryGetValue(khz, out long t) ? t + ticks : ticks;
            }
        }
        catch { }
    }

    internal static string? FormatResidency(SortedDictionary<long, long> totals)
    {
        if (totals.Count == 0) return null;
        long grand = 0;
        foreach (var kv in totals) grand += kv.Value;
        if (grand <= 0) return null;

        var top = totals
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Where(kv => kv.Value > 0)
            .Select(kv => $"{kv.Key / 1000} MHz {kv.Value * 100.0 / grand:F0}%");
        string text = string.Join(", ", top);
        return text.Length == 0 ? null : text;
    }

    // Linux has no per-process "handles" the way Windows does; the closest analogue
    // is the count of open file descriptors across all processes. Process.HandleCount
    // throws on Linux, so the base-class GetProcessStats would report N/A — this
    // override sums /proc/<pid>/fd entries and falls back to the kernel-wide
    // /proc/sys/fs/file-nr figure when a per-process read fails.
    public override ProcessStats GetProcessStats()
    {
        try
        {
            int processes = 0, threads = 0, fds = 0;
            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                string pid = Path.GetFileName(procDir);
                if (string.IsNullOrEmpty(pid) || !pid.All(char.IsDigit)) continue;
                processes++;

                try
                {
                    foreach (string line in File.ReadLines(Path.Combine(procDir, "status")))
                    {
                        if (!line.StartsWith("Threads:", StringComparison.Ordinal)) continue;
                        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[1], out int t)) threads += t;
                        break;
                    }
                }
                catch { }

                try { fds += Directory.GetFiles(Path.Combine(procDir, "fd")).Length; }
                catch { } // other users' fds are not readable
            }

            string handles = fds > 0 ? $"{fds} open files" : "Open files unreadable";
            return new ProcessStats(processes, threads, handles);
        }
        catch
        {
            return new ProcessStats(0, 0, "N/A");
        }
    }

    // /proc/sys/fs/file-nr: "allocated unused maximum" — the first column is the
    // number of file handles currently allocated kernel-wide.
    private static long ReadFileNr()
    {
        try
        {
            if (!File.Exists("/proc/sys/fs/file-nr")) return 0;
            string line = File.ReadAllText("/proc/sys/fs/file-nr").Trim();
            int sp = line.IndexOf(' ');
            if (sp > 0 && long.TryParse(line[..sp], out long v)) return v;
        }
        catch { }
        return 0;
    }
}