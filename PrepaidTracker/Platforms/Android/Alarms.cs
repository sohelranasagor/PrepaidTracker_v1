using Android.App;
using Android.Content;
using AndroidX.Work;

namespace PrepaidTracker;

/// <summary>
/// Reliable background notifications on Android, independent of whether the app is open,
/// backgrounded, or fully closed.
///
/// WorkManager alone is best-effort — Doze batches it and several OEMs demote or kill it.
/// So instead of trusting it to fire on time, we use it only as the *execution* environment
/// and let an EXACT alarm be the clock. AlarmManager.SetExactAndAllowWhileIdle fires through
/// Doze, waking the device (and starting the app process if it was killed). On each fire we
/// run a real sync — which checks the live balance and posts a low-balance notification if
/// warranted — then re-arm for the next day. A boot receiver re-arms after a reboot.
///
/// Honest ceiling: a Force Stop from Settings, or an aggressive OEM battery manager
/// (MIUI / ColorOS / Funtouch) that treats the app as frozen, can still suppress this. No
/// mechanism on Android is exempt from that. Exempting the app from battery optimisation in
/// system settings is what makes it stick on those devices.
/// </summary>
public static class Alarms
{
    const int RequestCode = 4711;
    const int HourOfDay = 8;          // daily check at ~8am local

    public static void ScheduleDaily(Context ctx)
    {
        try
        {
            var am = (AlarmManager?)ctx.GetSystemService(Context.AlarmService);
            if (am == null) return;

            var intent = new Intent(ctx, typeof(AlarmReceiver));
            var pi = PendingIntent.GetBroadcast(
                ctx, RequestCode, intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

            var now = DateTime.Now;
            var next = now.Date.AddHours(HourOfDay);
            if (next <= now) next = next.AddDays(1);
            long triggerAt = new DateTimeOffset(next).ToUnixTimeMilliseconds();

            // Exact where we're allowed to be, inexact-but-Doze-piercing otherwise. On
            // Android 12+ exact alarms can be withheld; SetAndAllowWhileIdle still fires in
            // Doze, just not to the minute — fine for a once-a-day balance check.
            bool canExact = !OperatingSystem.IsAndroidVersionAtLeast(31) || am.CanScheduleExactAlarms();
            if (canExact)
                am.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAt, pi);
            else
                am.SetAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAt, pi);
        }
        catch { /* never let scheduling crash a launch */ }
    }
}

/// <summary>
/// Fires on the daily alarm. Runs the sync inside an expedited WorkManager job — a proper
/// worker lifecycle with time to do network — rather than blocking the short-lived
/// broadcast, then re-arms tomorrow's alarm. Sync.RunAllAsync is the same path the
/// foreground uses, and it posts the low-balance notification itself when the meter is low.
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public class AlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null) return;
        try
        {
            var req = (OneTimeWorkRequest)new OneTimeWorkRequest.Builder(
                    Java.Lang.Class.FromType(typeof(SyncWorker))!)
                .SetExpedited(OutOfQuotaPolicy.RunAsNonExpeditedWorkRequest!)!
                .Build()!;

            WorkManager.GetInstance(context)
                       .EnqueueUniqueWork("alarm-sync", ExistingWorkPolicy.Replace!, req);
        }
        catch { }
        finally
        {
            // Always line up the next day, even if this run failed to enqueue.
            Alarms.ScheduleDaily(context);
        }
    }
}

/// <summary>Re-arm the daily alarm after a reboot — AlarmManager forgets its queue on power-off.</summary>
[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context != null) Alarms.ScheduleDaily(context);
    }
}
