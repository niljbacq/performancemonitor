using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Provider;
using Android.Widget;
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
    private const int PermissionRequestCode = 1001;

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestPermissionsIfNeeded();
        StartMonitorService();
        OverlayHost.Toggle = _ => ToggleAndroidOverlay();
    }

    protected override void OnPause()
    {
        base.OnPause();
        (Avalonia.Application.Current as TaskManager.App)?.MainViewModel?.Pause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        (Avalonia.Application.Current as TaskManager.App)?.MainViewModel?.Resume();
    }

    private void ToggleAndroidOverlay()
    {
        if (!Settings.CanDrawOverlays(this))
        {
            Toast.MakeText(this, "Allow display over other apps to use the overlay", ToastLength.Long)?.Show();
            try
            {
                StartActivity(new Intent(Settings.ActionManageOverlayPermission,
                    Android.Net.Uri.Parse($"package:{PackageName}")));
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("PerfMon", $"Could not open overlay permission: {ex.Message}");
            }
            return;
        }

        try
        {
            var intent = new Intent(this, typeof(MonitorForegroundService));
            intent.SetAction(MonitorForegroundService.ActionToggleOverlay);
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) StartForegroundService(intent);
            else StartService(intent);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("PerfMon", $"Could not toggle overlay: {ex.Message}");
        }
    }

    // One request for everything still missing: Android shows a single
    // permission flow instead of two overlapping dialogs.
    // Location is needed to read the Wi-Fi name; notifications (Android 13+)
    // are needed to show the foreground-service notification.
    private void RequestPermissionsIfNeeded()
    {
        var needed = new List<string>();

        if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Permission.Granted)
            needed.Add(Android.Manifest.Permission.AccessFineLocation);

        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            needed.Add(Android.Manifest.Permission.PostNotifications);

        if (needed.Count > 0)
            RequestPermissions(needed.ToArray(), PermissionRequestCode);
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
        catch (Exception ex)
        {
            Android.Util.Log.Warn("PerfMon", $"Could not start monitor service: {ex.Message}");
        }
    }
}
