using System;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using TaskManager;
using TaskManager.Providers;

namespace TaskManager.Droid;

[Android.App.Application]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
        ProviderFactory.CpuFactory = () => new AndroidCpuProvider();
        ProviderFactory.MemoryFactory = () => new AndroidMemoryProvider();
        ProviderFactory.DiskFactory = () => new AndroidDiskProvider();
        ProviderFactory.GpuFactory = () => new AndroidGpuProvider();
        ProviderFactory.WifiFactory = () => new AndroidWifiProvider();

        // Temporary: answers the IsLinux() question on your phone. Delete after reading the log.
        Android.Util.Log.Info("PerfMon", $"IsAndroid={OperatingSystem.IsAndroid()} IsLinux={OperatingSystem.IsLinux()}");
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).WithInterFont();
}