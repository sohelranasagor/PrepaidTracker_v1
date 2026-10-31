using Android.Content;
using AndroidX.Work;

namespace PrepaidTracker;

/// <summary>
/// The daily sync on Android. WorkManager persists its queue to a database inside the
/// app's data dir, so this survives a swipe from Recents, a low-memory kill, and a
/// reboot — the system starts the process to run it. That is the whole reason to use it
/// rather than a Handler, an AlarmManager, or a foreground Service.
///
/// What it is NOT: a clock. Doze batches work into maintenance windows, App Standby
/// demotes an app the user rarely opens, and MIUI / ColorOS / Funtouch will happily
/// treat a swipe-away as a force stop and never run it at all. So the queued alert in
/// Notifier.ScheduleLowBalance stays the load-bearing part; this just keeps the readings
/// (and therefore the forecast the alert is dated from) fresh.
///
/// A Force Stop from Settings puts the app in the stopped state and nothing here runs
/// until the user launches it again. There is no way around that, on any Android.
/// </summary>
public class SyncWorker : Worker
{
    public SyncWorker(Context ctx, WorkerParameters args) : base(ctx, args) { }

    public override Result DoWork()
    {
        try
        {
            // Worker.DoWork is already off the main thread; block here deliberately
            // rather than pull in ListenableWorker's future plumbing for one call.
            var ok = Sync.RunAllAsync().GetAwaiter().GetResult();
            return ok ? Result.InvokeSuccess() : Result.InvokeRetry();
        }
        catch
        {
            return Result.InvokeRetry();
        }
    }
}

/// <summary>
/// Delivers the notification that Notifier.ScheduleLowBalance dated in advance. No
/// network, no fetch — the decision was made at sync time; this only rings the bell.
///
/// The body is formatted HERE, not at schedule time, so a language switch between arm
/// and fire is honoured without needing a resync. The input carries the numeric
/// projection plus the identifying lead; the title and sentence template come from
/// L.S(...) evaluated with the language currently in effect.
/// </summary>
public class AlertWorker : Worker
{
    public const string KeyLead = "lead";
    public const string KeyBalance = "balance";
    public const string KeyDaysLeft = "daysLeft";
    public const string KeyHasNumbers = "hasNumbers";

    public AlertWorker(Context ctx, WorkerParameters args) : base(ctx, args) { }

    public override Result DoWork()
    {
        var lead = InputData.GetString(KeyLead) ?? "";
        var hasNumbers = InputData.GetBoolean(KeyHasNumbers, false);
        double? balance = hasNumbers ? InputData.GetDouble(KeyBalance, 0) : null;
        double? daysLeft = hasNumbers ? InputData.GetDouble(KeyDaysLeft, 0) : null;

        // Format now, using the current language. L.S honours Store.Language, which the
        // user may have changed since this reminder was armed.
        var title = L.S("alertTitle");
        var body = Notifier.FormatBody(lead, balance, daysLeft);

        if (title.Length > 0) Notifier.Show(title, body);
        return Result.InvokeSuccess();
    }
}
