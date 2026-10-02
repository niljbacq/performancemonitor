using Hardware.Info;
using System;

namespace TaskManager.Providers;

public sealed class HardwareInfoMemoryProvider : IMemoryProvider
{
    private readonly HardwareInfo _hardwareInfo = new();

    public MemoryUsage GetUsage()
    {
        _hardwareInfo.RefreshMemoryStatus();

        ulong totalBytes = _hardwareInfo.MemoryStatus.TotalPhysical;
        ulong usedBytes = totalBytes - _hardwareInfo.MemoryStatus.AvailablePhysical;

        double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double usedGB = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double percent = Math.Round((usedGB / totalGB) * 100, 0);

        return new MemoryUsage(totalGB, usedGB, percent);
    }
}
