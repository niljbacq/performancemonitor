namespace TaskManager.Providers;

public sealed class NullCpuProvider : ICpuProvider
{
    public CpuSpecs GetSpecs() => new() { Name = "Not available" };
    public float GetUsagePercent() => 0;
    public ProcessStats GetProcessStats() => new(0, 0, "N/A");
}

public sealed class NullGpuProvider : IGpuProvider
{
    public GpuSpecs GetSpecs() => new() { Name = "Not available" };
    public GpuSample? Sample() => null;
}

public sealed class NullDiskProvider : IDiskProvider
{
    public DiskSpecs GetSpecs() => new() { ModelName = "Not available" };
    public DiskSample? Sample() => new() { NotSupported = true };
}

public sealed class NullWifiProvider : IWifiProvider
{
    public WifiDetails GetDetails(string interfaceName)
        => new("Disconnected", "Not connected", "-", "-");
}

public sealed class NullMemoryProvider : IMemoryProvider
{
    public MemoryUsage GetUsage() => new(0, 0, 0);
}