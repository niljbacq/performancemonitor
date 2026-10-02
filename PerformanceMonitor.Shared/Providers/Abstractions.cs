using System;
using System.Diagnostics;

namespace TaskManager.Providers;

// ---- Data models (null = "leave the ViewModel default") ----

public sealed class CpuSpecs
{
    public string? Name { get; set; }
    public string? BaseSpeed { get; set; }
    public string? Speed { get; set; }
    public string? Cores { get; set; }
    public string? LogicalProcessors { get; set; }
    public string? Sockets { get; set; }
    public string? Virtualization { get; set; }
    public string? L1Cache { get; set; }
    public string? L2Cache { get; set; }
    public string? L3Cache { get; set; }
}

public sealed record ProcessStats(int Processes, int Threads, string Handles);

public sealed class GpuSpecs
{
    public string? Name { get; set; }
    public string? DriverVersion { get; set; }
    public string? DriverDate { get; set; }
    public string? GraphicsApi { get; set; }
    public string? PhysicalLocation { get; set; }
}

public sealed class GpuSample
{
    public double? Utilization { get; set; }
    public double? TemperatureC { get; set; }
    public string? MemoryUsage { get; set; }
    public string? UtilizationText { get; set; }
}

public sealed class DiskSpecs
{
    public string? DiskName { get; set; }
    public string? ModelName { get; set; }
    public string? Capacity { get; set; }
    public string? Formatted { get; set; }
    public string? SystemDisk { get; set; }
    public string? PageFile { get; set; }
    public string? Type { get; set; }
}

public sealed class DiskSample
{
    public double? ReadBytesPerSec { get; set; }
    public double? WriteBytesPerSec { get; set; }
    public double? ActivePercent { get; set; }
    public double? ResponseMs { get; set; }
    public bool NotSupported { get; set; }
}

public sealed record WifiDetails(string Status, string Ssid, string ConnectionType, string SignalStrength);

public sealed record MemoryUsage(double TotalGB, double UsedGB, double Percent);

// ---- Interfaces ----

public interface ICpuProvider
{
    CpuSpecs GetSpecs();
    float GetUsagePercent();
    ProcessStats GetProcessStats();
}

public interface IGpuProvider
{
    GpuSpecs GetSpecs();
    GpuSample? Sample();
}

public interface IDiskProvider
{
    DiskSpecs GetSpecs();
    DiskSample? Sample();
}

public interface IWifiProvider
{
    WifiDetails GetDetails(string interfaceName);
}

public interface IMemoryProvider
{
    MemoryUsage GetUsage();
}

// ---- Shared helper ----

internal static class CommandRunner
{
    public static string Run(string command, string arguments, int timeoutMs = 500)
    {
        try
        {
            using var process = new Process
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
            process.WaitForExit(timeoutMs);
            return result;
        }
        catch
        {
            return string.Empty;
        }
    }
}

// ---- Factory (macOS branches removed) ----

public static class ProviderFactory
{
    public static ICpuProvider CreateCpu()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCpuProvider();
        if (OperatingSystem.IsLinux()) return new LinuxCpuProvider();
        throw Unsupported("CPU");
    }

    public static IGpuProvider CreateGpu()
    {
        if (OperatingSystem.IsWindows()) return new WindowsGpuProvider();
        if (OperatingSystem.IsLinux()) return new LinuxGpuProvider();
        throw Unsupported("GPU");
    }

    public static IDiskProvider CreateDisk()
    {
        if (OperatingSystem.IsWindows()) return new WindowsDiskProvider();
        if (OperatingSystem.IsLinux()) return new LinuxDiskProvider();
        throw Unsupported("Disk");
    }

    public static IWifiProvider CreateWifi()
    {
        if (OperatingSystem.IsWindows()) return new WindowsWifiProvider();
        if (OperatingSystem.IsLinux()) return new LinuxWifiProvider();
        throw Unsupported("WiFi");
    }

    public static IMemoryProvider CreateMemory() => new HardwareInfoMemoryProvider();

    private static PlatformNotSupportedException Unsupported(string what)
        => new($"{what} monitoring is not supported on this operating system.");
}