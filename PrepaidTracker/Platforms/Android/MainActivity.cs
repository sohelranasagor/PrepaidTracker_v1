using Android.App;
using Android.Content.PM;

namespace PrepaidTracker;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
                         | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Enqueue the daily sync. The policy is KEEP, so opening the app doesn't reset
        // the period — otherwise a user who opens it every morning would never actually
        // reach a scheduled run.
        BackgroundSync.Schedule();

        // The reliable background path: an exact daily alarm that wakes the device through
        // Doze — even with the app closed — to sync and notify if low. See Alarms.cs.
        Alarms.ScheduleDaily(this);

        // Ask for POST_NOTIFICATIONS here, where an Activity exists. The App() constructor
        // also calls this, but on Android that runs before any Activity is attached, so the
        // system dialog can silently no-op there. Permissions.RequestAsync only shows the
        // dialog once, so calling it in both places is harmless.
        Notifier.RequestPermission();
    }
}
