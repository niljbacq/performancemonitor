using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TaskManager.Providers;

/// <summary>
/// Cross-platform "who am I running on" data. Every field is nullable; the
/// ViewModel hides rows whose value is null, matching the app-wide rule.
/// </summary>
public sealed class SystemInfo
{
    public string? OperatingSystem { get; set; }
    public string? Architecture { get; set; }
    public string? ProcessArchitecture { get; set; }
    public string? Runtime { get; set; }
    public string? Avalonia { get; set; }
    public string? Kernel { get; set; }
    public string? Distro { get; set; }
    public string? MachineName { get; set; }
    public string? ProcessorCount { get; set; }

    // Android-only extras (Build.* / SocModel). Null elsewhere.
    public string? Manufacturer { get; set; }
    public string? DeviceModel { get; set; }
    public string? DeviceHardware { get; set; }
    public string? SocModel { get; set; }
    public string? AndroidRelease { get; set; }
    public string? AndroidSdk { get; set; }
    public string? AndroidBoard { get; set; }
}

public interface ISystemInfoProvider
{
    SystemInfo GetInfo();
}

/// <summary>
/// Default provider: everything available from the BCL plus /proc and
/// /etc/os-release on Linux. Android adds Build.* values via its own subclass.
/// </summary>
public class CrossPlatformSystemInfoProvider : ISystemInfoProvider
{
    public virtual SystemInfo GetInfo()
    {
        return new SystemInfo
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            Avalonia = AvaloniaVersion(),
            Kernel = ReadKernel(),
            Distro = ReadDistro(),
            MachineName = SafeMachineName(),
            ProcessorCount = Environment.ProcessorCount.ToString()
        };
    }

    private static string? AvaloniaVersion()
    {
        try { return typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(); }
        catch { return null; }
    }

    private static string? ReadKernel()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (File.Exists("/proc/sys/kernel/osrelease"))
                    return File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
            }
            catch { }
        }
        try { return Environment.OSVersion.VersionString; }
        catch { return null; }
    }

    private static string? ReadDistro()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            if (!File.Exists("/etc/os-release")) return null;
            foreach (string line in File.ReadLines("/etc/os-release"))
            {
                if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) continue;
                string v = line["PRETTY_NAME=".Length..].Trim().Trim('"');
                return v.Length > 0 ? v : null;
            }
        }
        catch { }
        return null;
    }

    private static string? SafeMachineName()
    {
        try
        {
            string n = Environment.MachineName;
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }
        catch { return null; }
    }
}
