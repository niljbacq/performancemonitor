using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

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
    public string? FrequencyMinText { get; set; }
    public string? FrequencyMaxText { get; set; }
    public string? CpuGovernor { get; set; }
    public string? SupportedAbis { get; set; }
    public string? ClusterText { get; set; }
    public string? TemperatureText { get; set; }
    public string? PressureText { get; set; }
}

public sealed record ProcessStats(int? Processes, int? Threads, string? Handles);

public sealed class GpuSpecs
{
    public string? Name { get; set; }
    public string? DriverVersion { get; set; }
    public string? DriverDate { get; set; }
    public string? GraphicsApi { get; set; }
    public string? PhysicalLocation { get; set; }
    public string? Vendor { get; set; }
}

public sealed class GpuSample
{
    public double? Utilization { get; set; }
    public double? TemperatureC { get; set; }
    public string? MemoryUsage { get; set; }
    public string? UtilizationText { get; set; }
    public string? TemperatureText { get; set; }
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
    public string? UsedText { get; set; }
    public string? FreeText { get; set; }
    public string? AppsAndDataText { get; set; }
    public string? SystemReservedText { get; set; }
    public string? PressureText { get; set; }
}

public sealed class DiskSample
{
    public double? ReadBytesPerSec { get; set; }
    public double? WriteBytesPerSec { get; set; }
    public double? ActivePercent { get; set; }
    public double? ResponseMs { get; set; }
    public bool NotSupported { get; set; }
    public string? NotSupportedMessage { get; set; }
}

public sealed record WifiDetails(string Status, string Ssid, string ConnectionType, string SignalStrength);

public sealed record MemoryUsage(double TotalGB, double UsedGB, double Percent)
{
    public string? SwapText { get; init; }
    public string? ActiveText { get; init; }
    public string? InactiveText { get; init; }
    public string? SlabText { get; init; }
    public string? PressureText { get; init; }
}

// ---- Interfaces ----

public interface ICpuProvider
{
    CpuSpecs GetSpecs();
    float GetUsagePercent();
    ProcessStats GetProcessStats();

    long UptimeMilliseconds => Environment.TickCount64;
    string? UsageUnavailableMessage => null;
    string? ProcessStatsUnavailableMessage => null;

    string? PerCoreFrequencyText => null;
    string? CurrentSpeedText => null;
    string? FrequencyResidencyText => null;
    string? AppUsageText => null;
}

public interface IGpuProvider
{
    GpuSpecs GetSpecs();
    GpuSample? Sample();
}

public sealed class GpuEnvironment
{
    public string? DisplayResolution { get; set; }
    public string? RefreshRate { get; set; }
    public string? Hdr { get; set; }
    public string? WideColor { get; set; }
    public string? ThermalStatus { get; set; }
}

public interface IGpuEnvironmentProvider
{
    GpuEnvironment GetEnvironment();
}

public interface IGpuListProvider
{
    IReadOnlyList<GpuInfo> GetGpuList();
    IReadOnlyList<GpuInfo> SampleGpuList();
}

public sealed class GpuInfo
{
    public string? DeviceId { get; set; }
    public string? PciBus { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? DriverVersion { get; set; }
    public string? DriverDate { get; set; }
    public string? GraphicsApi { get; set; }
    public string? PhysicalLocation { get; set; }
    public string? Vendor { get; set; }
    public double? Utilization { get; set; }
    public double? TemperatureC { get; set; }
    public string? UtilizationText { get; set; }
    public string? TemperatureText { get; set; }
    public string? MemoryUsage { get; set; }
    public string? SharedMemoryUsage { get; set; }
    public long? TotalMemoryBytes { get; set; }
}

public interface IDiskProvider
{
    DiskSpecs GetSpecs();
    DiskSample? Sample();
}

public interface IDiskListProvider
{
    IReadOnlyList<DiskInfo> GetDiskList();
    IReadOnlyList<DiskInfo> SampleDiskList();
}

public sealed class StorageExtras
{
    public string? FileSystem { get; set; }
    public string? BlockSize { get; set; }
    // True only when Usage Access was actually granted, not merely when the breakdown loaded.
    public bool UsageAccessGranted { get; set; }
    // True only when every media permission is granted, so the hint tracks access, not empty collections.
    public bool MediaAccessGranted { get; set; }
    public IReadOnlyList<StorageVolumeInfo> Volumes { get; set; } = Array.Empty<StorageVolumeInfo>();
    public StorageBreakdown? Breakdown { get; set; }
}

public sealed class StorageVolumeInfo
{
    public string Name { get; set; } = "";
    public string UsageText { get; set; } = "";
    public double UsedPercent { get; set; }
}

public sealed class StorageBreakdown
{
    public string? Apps { get; set; }
    public string? AppData { get; set; }
    public string? AppCache { get; set; }
    public string? Photos { get; set; }
    public string? Videos { get; set; }
    public string? Audio { get; set; }
    public string? Other { get; set; }
}

public interface IStorageExtrasProvider
{
    StorageExtras GetExtras();
    void OpenUsageAccessSettings();
    void RequestMediaAccess();
}

public sealed class DiskInfo
{
    public string? DeviceId { get; set; }
    public string? DiskName { get; set; }
    public string? ModelName { get; set; }
    public string? Type { get; set; }
    public string? Capacity { get; set; }
    public string? Formatted { get; set; }
    public string? SystemDisk { get; set; }
    public string? PageFile { get; set; }
    public string? UsedText { get; set; }
    public string? FreeText { get; set; }
    public string? AppsAndDataText { get; set; }
    public string? SystemReservedText { get; set; }
    public double? FreeBytes { get; set; }
    public double? ReadBytesPerSec { get; set; }
    public double? WriteBytesPerSec { get; set; }
    public double? ActivePercent { get; set; }
    public double? ResponseMs { get; set; }
    public bool NotSupported { get; set; }
    public string? NotSupportedMessage { get; set; }
    public string? PressureText { get; set; }
}

public interface IWifiProvider
{
    WifiDetails GetDetails(string interfaceName);
    WifiSnapshot? GetSnapshot() => null;
}

public sealed record WifiSnapshot(
    string AdapterName, string Status, string Ssid, string ConnectionType, string SignalStrength,
    string Ipv4Address, string Ipv6Address, long? BytesSent, long? BytesReceived)
{
    public string? WifiStandard { get; init; }
    public string? SignalPercentText { get; init; }
    public string? Bssid { get; init; }
    public string? DhcpServer { get; init; }
    public string? Gateway { get; init; }
    public string? Dns1 { get; init; }
    public string? Dns2 { get; init; }
    public string? Netmask { get; init; }
    public string? NonMobileTrafficText { get; init; }
    public string? MobileTrafficText { get; init; }
    public string? DownloadText { get; init; }
    public string? UploadText { get; init; }
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
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Keep CLI output (lscpu, nmcli, iw, netsh) in a stable language.
            startInfo.Environment["LC_ALL"] = "C";
            startInfo.Environment["LANG"] = "C";

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return string.Empty;
            }

            return read.Wait(timeoutMs) ? read.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

// ---- Factory (Android-aware) ----

public static class ProviderFactory
{
    public static Func<ICpuProvider>? CpuFactory { get; set; }
    public static Func<IGpuProvider>? GpuFactory { get; set; }
    public static Func<IDiskProvider>? DiskFactory { get; set; }
    public static Func<IWifiProvider>? WifiFactory { get; set; }
    public static Func<IMemoryProvider>? MemoryFactory { get; set; }

    public static Func<string?>? LogDirectoryFactory { get; set; }

    public static ICpuProvider CreateCpu()
    {
        if (CpuFactory != null) return CpuFactory();
        if (OperatingSystem.IsAndroid()) return new NullCpuProvider();
        if (OperatingSystem.IsWindows()) return new WindowsCpuProvider();
        if (OperatingSystem.IsLinux()) return new LinuxCpuProvider();
        return new NullCpuProvider();
    }

    public static IGpuProvider CreateGpu()
    {
        if (GpuFactory != null) return GpuFactory();
        if (OperatingSystem.IsAndroid()) return new NullGpuProvider();
        if (OperatingSystem.IsWindows()) return new WindowsGpuProvider();
        if (OperatingSystem.IsLinux()) return new LinuxGpuProvider();
        return new NullGpuProvider();
    }

    public static IDiskProvider CreateDisk()
    {
        if (DiskFactory != null) return DiskFactory();
        if (OperatingSystem.IsAndroid()) return new NullDiskProvider();
        if (OperatingSystem.IsWindows()) return new WindowsDiskProvider();
        if (OperatingSystem.IsLinux()) return new LinuxDiskProvider();
        return new NullDiskProvider();
    }

    public static IWifiProvider CreateWifi()
    {
        if (WifiFactory != null) return WifiFactory();
        if (OperatingSystem.IsAndroid()) return new NullWifiProvider();
        if (OperatingSystem.IsWindows()) return new WindowsWifiProvider();
        if (OperatingSystem.IsLinux()) return new LinuxWifiProvider();
        return new NullWifiProvider();
    }

    public static IMemoryProvider CreateMemory()
    {
        if (MemoryFactory != null) return MemoryFactory();
        if (OperatingSystem.IsAndroid()) return new NullMemoryProvider();
        if (OperatingSystem.IsWindows()) return new HardwareInfoMemoryProvider();
        if (OperatingSystem.IsLinux()) return new LinuxMemoryProvider();
        return new NullMemoryProvider();
    }

    public static string LogDirectory()
    {
        if (LogDirectoryFactory != null)
        {
            try
            {
                string? custom = LogDirectoryFactory();
                if (!string.IsNullOrWhiteSpace(custom)) return custom;
            }
            catch { }
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerformanceMonitor");
    }
}
