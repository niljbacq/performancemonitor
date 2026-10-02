using System;
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

    public ProcessStats GetProcessStats()
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

            string lscpu = CommandRunner.Run("lscpu", "");
            if (!string.IsNullOrEmpty(lscpu))
            {
                s.Virtualization = lscpu.Contains("Virtualization:") || lscpu.Contains("VT-x") || lscpu.Contains("AMD-V")
                    ? "Enabled" : "Disabled";

                foreach (var line in lscpu.Split('\n'))
                {
                    if (line.StartsWith("L1d cache:") || line.StartsWith("L1i cache:"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1) s.L1Cache = parts[1].Trim();
                    }
                }
            }
        }
        catch
        {
            s.Name = "Linux Processor";
        }
        s.Name ??= "Linux Processor";
        return s;
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
}