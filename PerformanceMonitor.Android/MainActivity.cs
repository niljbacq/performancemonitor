using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Avalonia.Android;

namespace TaskManager.Droid;

[Activity(
    Label = "Performance Monitor",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int LocationRequestCode = 1001;
    private const int NotificationRequestCode = 1002;

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestLocationPermissionIfNeeded();
        RequestNotificationPermissionIfNeeded();
        StartMonitorService();
    }

    private void RequestLocationPermissionIfNeeded()
    {
        if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Permission.Granted) return;
        RequestPermissions(new[] { Android.Manifest.Permission.AccessFineLocation }, LocationRequestCode);
    }

    // Android 13+ requires a runtime grant before the foreground-service
    // notification can be shown.
    private void RequestNotificationPermissionIfNeeded()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) == Permission.Granted) return;
        RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, NotificationRequestCode);
    }

    // Starts the special-use foreground service so sampling (and CSV logging)
    // continues when the app is backgrounded.
    private void StartMonitorService()
    {
        try
        {
            var intent = new Intent(this, typeof(MonitorForegroundService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) StartForegroundService(intent);
            else StartService(intent);
        }
        catch { }
    }
}
