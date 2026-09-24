using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManager.ViewModels;

public partial class CpuViewModel : ViewModelBase
{
    private PerformanceCounter? _cpuCounter;

    private string _cpuName = "Detecting CPU...";
    public string CpuName
    {
        get => _cpuName;
        set => SetProperty(ref _cpuName, value);
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

    private string _speed = "0.00 GHz";
    public string Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    private string _processes = "0";
    public string Processes
    {
        get => _processes;
        set => SetProperty(ref _processes, value);
    }

    private string _threads = "0";
    public string Threads
    {
        get => _threads;
        set => SetProperty(ref _threads, value);
    }

    private string _handles = "0";
    public string Handles
    {
        get => _handles;
        set => SetProperty(ref _handles, value);
    }

    private string _uptime = "0:00:00:00";
    public string Uptime
    {
        get => _uptime;
        set => SetProperty(ref _uptime, value);
    }

    // Dynamic Hardware Specs
    private string _baseSpeed = "N/A";
    public string BaseSpeed
    {
        get => _baseSpeed;
        set => SetProperty(ref _baseSpeed, value);
    }

    private string _sockets = "1";
    public string Sockets
    {
        get => _sockets;
        set => SetProperty(ref _sockets, value);
    }

    private string _cores = Environment.ProcessorCount.ToString();
    public string Cores
    {
        get => _cores;
        set => SetProperty(ref _cores, value);
    }

    private string _logicalProcessors = Environment.ProcessorCount.ToString();
    public string LogicalProcessors
    {
        get => _logicalProcessors;
        set => SetProperty(ref _logicalProcessors, value);
    }

    private string _virtualization = "Unknown";
    public string Virtualization
    {
        get => _virtualization;
        set => SetProperty(ref _virtualization, value);
    }

    private string _l1Cache = "N/A";
    public string L1Cache
    {
        get => _l1Cache;
        set => SetProperty(ref _l1Cache, value);
    }

    private string _l2Cache = "N/A";
    public string L2Cache
    {
        get => _l2Cache;
        set => SetProperty(ref _l2Cache, value);
    }

    private string _l3Cache = "N/A";
    public string L3Cache
    {
        get => _l3Cache;
        set => SetProperty(ref _l3Cache, value);
    }

    public CpuViewModel()
    {
        InitCounters();
        _ = LoadCpuSpecsAsync();
        _ = StartMonitoringAsync();
    }

    private void InitCounters()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();
            }
            catch
            {
                _cpuCounter = null;
            }
        }
    }

    private async Task LoadCpuSpecsAsync()
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
        });
    }

    private void LoadWindowsSpecs()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                CpuName = obj["Name"]?.ToString()?.Trim() ?? CpuName;

                if (uint.TryParse(obj["MaxClockSpeed"]?.ToString(), out uint maxClock))
                {
                    BaseSpeed = $"{Math.Round(maxClock / 1000.0, 2):F2} GHz";
                    Speed = BaseSpeed;
                }

                if (obj["NumberOfCores"] != null)
                    Cores = obj["NumberOfCores"].ToString()!;

                if (obj["NumberOfLogicalProcessors"] != null)
                    LogicalProcessors = obj["NumberOfLogicalProcessors"].ToString()!;

                if (obj["L2CacheSize"] != null && uint.TryParse(obj["L2CacheSize"].ToString(), out uint l2))
                    L2Cache = l2 >= 1024 ? $"{l2 / 1024.0:F1} MB" : $"{l2} KB";

                if (obj["L3CacheSize"] != null && uint.TryParse(obj["L3CacheSize"].ToString(), out uint l3))
                    L3Cache = l3 >= 1024 ? $"{l3 / 1024.0:F1} MB" : $"{l3} KB";

                if (obj["VirtualizationFirmwareEnabled"] != null)
                    Virtualization = (bool)obj["VirtualizationFirmwareEnabled"] ? "Enabled" : "Disabled";

                break;
            }

            // Detect L1 Cache via Win32_CacheMemory
            using var cacheSearcher = new ManagementObjectSearcher("SELECT Level, MaxCacheSize FROM Win32_CacheMemory");
            uint totalL1KB = 0;

            foreach (var cache in cacheSearcher.Get())
            {
                // Level 3 enum in Win32_CacheMemory corresponds to L1 Cache
                if (ushort.TryParse(cache["Level"]?.ToString(), out ushort level) && level == 3)
                {
                    if (uint.TryParse(cache["MaxCacheSize"]?.ToString(), out uint size))
                    {
                        totalL1KB += size;
                    }
                }
            }

            if (totalL1KB > 0)
            {
                L1Cache = totalL1KB >= 1024 ? $"{totalL1KB / 1024.0:F1} MB" : $"{totalL1KB} KB";
            }

            using var socketSearcher = new ManagementObjectSearcher("SELECT SocketDesignation FROM Win32_Processor");
            Sockets = socketSearcher.Get().Count.ToString();
        }
        catch
        {
            CpuName = System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Windows Processor";
        }
    }

    private void LoadLinuxSpecs()
    {
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                string[] lines = File.ReadAllLines("/proc/cpuinfo");
                int coreCount = 0;

                foreach (string line in lines)
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase) && CpuName.Contains("Detecting"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1) CpuName = parts[1].Trim();
                    }
                    else if (line.StartsWith("cpu cores", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int cores))
                            Cores = cores.ToString();
                    }
                    else if (line.StartsWith("processor", StringComparison.OrdinalIgnoreCase))
                    {
                        coreCount++;
                    }
                    else if (line.StartsWith("cpu MHz", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1 && double.TryParse(parts[1].Trim(), out double mhz))
                            BaseSpeed = $"{mhz / 1000.0:F2} GHz";
                    }
                }

                if (coreCount > 0)
                    LogicalProcessors = coreCount.ToString();
            }

            string lscpu = ExecuteCommand("lscpu", "");
            if (!string.IsNullOrEmpty(lscpu))
            {
                if (lscpu.Contains("Virtualization:") || lscpu.Contains("VT-x") || lscpu.Contains("AMD-V"))
                    Virtualization = "Enabled";
                else
                    Virtualization = "Disabled";

                // Extract L1d/L1i cache if available from lscpu output
                foreach (var line in lscpu.Split('\n'))
                {
                    if (line.StartsWith("L1d cache:") || line.StartsWith("L1i cache:"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1)
                            L1Cache = parts[1].Trim();
                    }
                }
            }
        }
        catch
        {
            CpuName = "Linux Processor";
        }
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int tick = 0;

        // Resume ticks on a threadpool thread instead of the UI thread, so the
        // (potentially expensive) work below never runs on - and therefore
        // never blocks - the UI thread. This is what was causing the app to
        // stutter whenever a tick fired while the window was being dragged.
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            tick++;
            try
            {
                // 1. Live CPU Utilization % (cheap - safe to sample every tick)
                float cpuUsage = 0;
                if (_cpuCounter != null)
                {
                    cpuUsage = _cpuCounter.NextValue();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    cpuUsage = GetLinuxCpuUsage();
                }

                int cpuPercent = (int)Math.Round(cpuUsage);

                // 2. System Uptime (cheap)
                TimeSpan uptimeSpan = TimeSpan.FromMilliseconds(Environment.TickCount64);
                string uptimeText = $"{uptimeSpan.Days}:{uptimeSpan.Hours:D2}:{uptimeSpan.Minutes:D2}:{uptimeSpan.Seconds:D2}";

                // 3. Processes, Threads & Handles - this enumerates every process on the
                // system and, on Windows, opens each one to read its handle count. That's
                // genuinely expensive, so it only needs to run every few seconds rather
                // than every tick; the counts don't meaningfully change second-to-second.
                string? processesText = null;
                string? threadsText = null;
                string? handlesText = null;

                if (tick % 3 == 1)
                {
                    Process[] processList = Process.GetProcesses();
                    try
                    {
                        processesText = processList.Length.ToString();

                        int threadCount = 0;
                        long handleCount = 0;
                        foreach (var proc in processList)
                        {
                            try
                            {
                                threadCount += proc.Threads.Count;
                                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                    handleCount += proc.HandleCount;
                            }
                            catch { }
                        }

                        threadsText = threadCount.ToString();
                        handlesText = handleCount > 0 ? handleCount.ToString() : "N/A";
                    }
                    finally
                    {
                        // Process.GetProcesses() hands back live handles to every process
                        // on the system - previously these were never disposed, leaking a
                        // handle (and a small amount of memory) every single second.
                        foreach (var proc in processList)
                            proc.Dispose();
                    }
                }

                // Only the final, cheap property assignments touch the UI thread.
                Dispatcher.UIThread.Post(() =>
                {
                    UtilizationValue = cpuPercent;
                    Utilization = $"{cpuPercent}%";
                    Uptime = uptimeText;

                    if (processesText != null) Processes = processesText;
                    if (threadsText != null) Threads = threadsText;
                    if (handlesText != null) Handles = handlesText;
                });
            }
            catch { }
        }
    }

    private float GetLinuxCpuUsage()
    {
        try
        {
            if (File.Exists("/proc/stat"))
            {
                string firstLine = File.ReadLines("/proc/stat").First();
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    ulong idle = ulong.Parse(parts[4]);
                    ulong total = 0;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        if (ulong.TryParse(parts[i], out ulong val)) total += val;
                    }

                    return (float)Math.Clamp((1.0 - ((double)idle / Math.Max(total, 1))) * 100, 0, 100);
                }
            }
        }
        catch { }
        return 0;
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