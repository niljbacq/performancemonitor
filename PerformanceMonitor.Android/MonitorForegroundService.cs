using System;
using Android.App;
using Android.Content;
using Android.OS;
using TaskManager.Providers;

namespace TaskManager.Droid;

// Keeps the process alive so the SamplingService keeps writing the CSV log
// while the app is not in the foreground. Uses the "specialUse" foreground
// service type, which has no built-in timeout (unlike dataSync's 6 hours on
// Android 15). Google Play requires declaring a special-use subtype property
// for store submission; it is not needed for the service to run.
[Service(
    Exported = false,
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeSpecialUse)]
public class MonitorForegroundService : Service
{
    public const string ChannelId = "perfmon_monitor";
    private const int NotificationId = 4242;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateChannel();
        try { SamplingService.StartDefault(); } catch { }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification();
        if (notification != null)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotificationId, notification, Android.Content.PM.ForegroundService.TypeSpecialUse);
            else
                StartForeground(NotificationId, notification);
        }
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        try { StopForeground(StopForegroundFlags.Remove); } catch { }
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        try
        {
            if (GetSystemService(NotificationService) is not NotificationManager manager) return;
            var channel = new NotificationChannel(ChannelId, "Performance monitoring", NotificationImportance.Low)
            {
                Description = "Keeps hardware sampling alive in the background"
            };
            manager.CreateNotificationChannel(channel);
        }
        catch { }
    }

    private Notification? BuildNotification()
    {
        try
        {
            Notification.Builder builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? new Notification.Builder(this, ChannelId)
                : new Notification.Builder(this);

            builder.SetContentTitle("Performance Monitor")
                   .SetContentText("Sampling hardware in the background")
                   .SetSmallIcon(Resource.Drawable.icon)
                   .SetOngoing(true);

            var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
            if (launch != null)
            {
                var pending = PendingIntent.GetActivity(this, 0, launch,
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                if (pending != null) builder.SetContentIntent(pending);
            }

            return builder.Build();
        }
        catch { return null; }
    }
}
