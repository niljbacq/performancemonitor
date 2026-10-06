using Android.App;
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

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestLocationPermissionIfNeeded();
    }

    private void RequestLocationPermissionIfNeeded()
    {
        if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Permission.Granted) return;
        RequestPermissions(new[] { Android.Manifest.Permission.AccessFineLocation }, LocationRequestCode);
    }
}
