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
        ProviderFactory.LogDirectoryFactory = () =>
        {
            try { return Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath; }
            catch { return null; }
        };
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).WithInterFont();
}
