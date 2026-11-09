#if IOS
using BackgroundTasks;
using Foundation;
using UIKit;
#endif
#if ANDROID
using AndroidX.Work;
#endif

namespace PrepaidTracker;

/// <summary>
/// Wakes the app periodically to sync each meter and re-date the low-balance alert.
///
/// Neither platform gives a sideloaded app a real scheduler:
///
///   iOS      — BGAppRefreshTask is granted opportunistically, tuned to how often you
///              open the app, and NOT granted at all once you force-quit from the
///              switcher. A BGProcessingTask is registered alongside it because those
///              are scheduled for idle/charging windows (usually overnight), which suits
///              a meter that is read once a day far better than app-refresh does.
///   Android  — WorkManager persists to disk and survives a swipe from Recents and a
///              reboot, but Doze batches it, App Standby demotes a rarely-opened app,
///              and several large OEMs kill background work outright.
///
/// So this is best-effort on both. The alert a user actually depends on is the one
/// Notifier.ScheduleLowBalance hands to the OS in advance, which fires on its date with
/// no wake at all. This class exists to keep the readings fresh so that date is right.
/// </summary>
public static class BackgroundSync
{
    public const string TaskId = "com.example.prepaidtracker.refresh";
    public const string ProcessId = "com.example.prepaidtracker.process";

#if ANDROID
    const string WorkName = "daily-sync";
    const string NextReadWork = "next-read";

    public static void Register() { }   // WorkManager needs no pre-launch registration

    public static void Schedule()
    {
        try
        {
            // THE NET. A fixed 24h period, KEEP: re-enqueueing on every launch must not
            // reset the clock, or an app opened daily would never actually reach a
            // scheduled run. It drifts to whatever hour it was first enqueued at, which
            // is why it can't be the only wake — but it survives reboots and never needs
            // the app to be opened, so it's what keeps a neglected install roughly current.
            // Build() returns the base WorkRequest in the binding; the enqueue overload
            // wants the concrete type. Same cast as the one-shot alert in Notifier.
            var work = (PeriodicWorkRequest)new PeriodicWorkRequest.Builder(
                    Java.Lang.Class.FromType(typeof(SyncWorker)),
                    24, Java.Util.Concurrent.TimeUnit.Hours!)
                .SetConstraints(new Constraints.Builder()
                    .SetRequiredNetworkType(NetworkType.Connected!)
                    .Build()!)
                .Build()!;

            WorkManager.GetInstance(Android.App.Application.Context)
                       .EnqueueUniquePeriodicWork(WorkName, ExistingPeriodicWorkPolicy.Keep!, work);
        }
        catch { /* no WorkManager — foreground sync still works */ }

        try
        {
            // THE PRECISION WAKE. One-shot, aimed at the meter's next read rather than at
            // a fixed offset from install time. REPLACE is safe here in a way it is not
            // for the periodic net above: this target is ABSOLUTE (last read + 24h + 20m),
            // so recomputing it on launch re-aims at the same moment instead of pushing it
            // a day out.
            //
            // Deliberately NOT re-enqueued from inside SyncWorker. Enqueuing unique work
            // with REPLACE cancels the worker currently running under that name, and
            // self-cancelling mid-run is a documented WorkManager footgun. This is armed
            // on launch instead; the periodic net covers a user who never opens the app.
            var delay = SyncSchedule.NextDelay();

            var next = (OneTimeWorkRequest)new OneTimeWorkRequest.Builder(
                    Java.Lang.Class.FromType(typeof(SyncWorker)))
                .SetInitialDelay((long)delay.TotalMinutes, Java.Util.Concurrent.TimeUnit.Minutes!)
                .SetConstraints(new Constraints.Builder()
                    .SetRequiredNetworkType(NetworkType.Connected!)
                    .Build()!)
                .Build()!;

            WorkManager.GetInstance(Android.App.Application.Context)
                       .EnqueueUniqueWork(NextReadWork, ExistingWorkPolicy.Replace!, next);
        }
        catch { }
    }
#elif IOS

    /// <summary>Must be called in FinishedLaunching, before the app finishes launching.</summary>
    public static void Register()
    {
        if (!UIDevice.CurrentDevice.CheckSystemVersion(13, 0)) return;

        BGTaskScheduler.Shared.Register(TaskId, null, task =>
        {
            Schedule();                    // always queue the next one first
            _ = Run(task, refresh: true);
        });

        BGTaskScheduler.Shared.Register(ProcessId, null, task =>
        {
            Schedule();
            _ = Run(task, refresh: false);
        });
    }

    /// <summary>Ask iOS for another wake. Call on launch and whenever we go to background.</summary>
    public static void Schedule()
    {
        if (!UIDevice.CurrentDevice.CheckSystemVersion(13, 0)) return;

        // Aim both requests at the meter's next read. EarliestBeginDate is a floor, not a
        // target, and the old fixed 2h floor was chosen so as not to decline wakes iOS
        // was willing to grant sooner — but an earlier wake has nothing to fetch. Neither
        // portal moves between reads, so a wake at hour 2 of a 24-hour cycle spends the
        // app's background budget re-downloading a figure it already has. Spend it on the
        // one moment the number changes instead.
        var delay = SyncSchedule.NextDelay();

        try
        {
            BGTaskScheduler.Shared.Cancel(TaskId);
            var refresh = new BGAppRefreshTaskRequest(TaskId)
            {
                EarliestBeginDate = (NSDate)DateTime.Now.Add(delay).ToUniversalTime()
            };
            BGTaskScheduler.Shared.Submit(refresh, out _);
        }
        catch { /* background refresh disabled, or no entitlement — not fatal */ }

        try
        {
            // The processing task is the complement, not a duplicate: iOS runs these in
            // idle/charging windows, which for most phones is overnight — the same hours
            // both portals post the new read. If app-refresh isn't granted, this is the
            // one that catches the day.
            BGTaskScheduler.Shared.Cancel(ProcessId);
            var processing = new BGProcessingTaskRequest(ProcessId)
            {
                EarliestBeginDate = (NSDate)DateTime.Now.Add(delay).ToUniversalTime(),
                RequiresNetworkConnectivity = true,
                RequiresExternalPower = false   // true would be more reliable and less useful
            };
            BGTaskScheduler.Shared.Submit(processing, out _);
        }
        catch { }
    }

    static async Task Run(BGTask task, bool refresh)
    {
        var cts = new CancellationTokenSource();
        task.ExpirationHandler = () => cts.Cancel();

        // App-refresh gets ~30 seconds; a processing task gets minutes. Same work either
        // way — Sync.RunAllAsync is the one path the foreground uses too.
        var outcome = await Sync.RunAllAsync(false, cts.Token);

        // SetTaskCompleted(true) tells iOS the wake was productive, which is one of the
        // signals it uses to decide how often to wake us again. AuthRequired is NOT
        // productive — nothing was fetched — and reporting it as success would train iOS
        // to keep waking an app that can't do anything until the user signs in.
        task.SetTaskCompleted(outcome == SyncOutcome.Ok);
    }
#else
    public static void Register() { }
    public static void Schedule() { }
#endif
}
