using System;
using Hardware.Info;

namespace TaskManager.ViewModels;

public class MemoryMonitor
{
#if !ANDROID
    private readonly HardwareInfo _hardwareInfo = new();
#endif

    public (double TotalGB, double UsedGB, double Percent) GetLiveMemoryUsage()
    {
#if ANDROID
        var am = (Android.App.ActivityManager)Android.App.Application.Context
            .GetSystemService(Android.Content.Context.ActivityService)!;
        var info = new Android.App.ActivityManager.MemoryInfo();
        am.GetMemoryInfo(info);
        double totalGB = info.TotalMem / 1024.0 / 1024.0 / 1024.0;
        double usedGB = totalGB - info.AvailMem / 1024.0 / 1024.0 / 1024.0;
        return (Math.Round(totalGB, 1), Math.Round(usedGB, 1), Math.Round(usedGB / totalGB * 100, 0));
#else
        _hardwareInfo.RefreshMemoryStatus();

        ulong totalBytes = _hardwareInfo.MemoryStatus.TotalPhysical;
        ulong availableBytes = _hardwareInfo.MemoryStatus.AvailablePhysical;
        ulong usedBytes = totalBytes - availableBytes;

        double totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double usedGB = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double percent = Math.Round((usedGB / totalGB) * 100, 0);

        return (totalGB, usedGB, percent);
#endif
    }
}
