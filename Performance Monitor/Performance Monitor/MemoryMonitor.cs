using System;
using Hardware.Info;

namespace TaskManager.ViewModels;

public class MemoryMonitor
{
    private readonly HardwareInfo _hardwareInfo = new();

    public (double TotalGB, double UsedGB, double Percent) GetLiveMemoryUsage()
    {
        _hardwareInfo.RefreshMemoryStatus();

        ulong totalBytes = _hardwareInfo.MemoryStatus.TotalPhysical;
        ulong availableBytes = _hardwareInfo.MemoryStatus.AvailablePhysical;
        ulong usedBytes = totalBytes - availableBytes;

        double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double usedGB = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double percent = Math.Round((usedGB / totalGB) * 100, 0);

        return (totalGB, usedGB, percent);
    }
}