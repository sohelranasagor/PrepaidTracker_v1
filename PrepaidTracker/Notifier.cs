#if IOS
using Foundation;
using UserNotifications;
#endif
#if ANDROID
using AndroidX.Work;
using System.Security.Permissions;
using static System.Runtime.InteropServices.JavaScript.JSType;
#endif

namespace PrepaidTracker;

/// <summary>
/// Local alerts. Two kinds, and the difference between them is the whole point:
///
///   CheckLowBalance    — the balance is low RIGHT NOW, at sync time. Fires immediately.
///                        Needs the app running or woken, so it only ever helps someone
///                        who was already looking.
///
///   ScheduleLowBalance — the forecast says the meter runs dry on the 18th. Hand the OS a
///                        notification dated the 16th and walk away. iOS delivers a
///                        scheduled UNCalendarNotificationTrigger even if the app has been
///                        force-quit; Android's WorkManager survives a swipe from Recents
///                        and a reboot. No background wake needed, no network needed —
///                        the date was computed while we still had both.
///
/// The second one is what makes a week away survivable. Background refresh is
/// opportunistic on both platforms and never guaranteed; a queued notification is.
/// The cost is being honest about what it is: a forecast from average usage, so an
/// unusual week makes it early or late. A warning that always arrives beats a precise
/// one that never does.
///
/// Not push: a sideloaded app has no aps-environment entitlement, and no server here
/// holds your meter number.
/// </summary>
public static class Notifier
{
    // ---------------- permission ----------------

#if IOS
    /// <summary>Prompt once and report whether alerts are allowed. iOS remembers the
    /// answer; a later call after the user has decided returns it without re-prompting.</summary>
    public static Task<bool> RequestPermissionAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        UNUserNotificationCenter.Current.RequestAuthorization(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge,
            (granted, _) => tcs.TrySetResult(granted));
        return tcs.Task;
    }
#elif ANDROID
    public static async Task<bool> RequestPermissionAsync()
    {
        // POST_NOTIFICATIONS is a runtime permission from Android 13 (API 33). Below that
        // it is granted at install time, so there is nothing to ask for.
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return true;
        try
        {
            // Shows a system dialog, so it must run on the UI thread.
            var status = await MainThread.InvokeOnMainThreadAsync(
                async () => await Permissions.RequestAsync<Permissions.PostNotifications>());
            return status == PermissionStatus.Granted;
        }
        catch { return false; }
    }
#else
    public static Task<bool> RequestPermissionAsync() => Task.FromResult(true);
#endif

    /// <summary>Fire-and-forget prompt for the startup path, where nothing awaits the result.</summary>
    public static void RequestPermission() => _ = RequestPermissionAsync();

    // ---------------- low right now ----------------

    public static void CheckLowBalance(Meter meter, Forecast? f, string? accountName = null)
    {
        if (!Store.AlertsEnabled || !meter.NotificationsEnabled || f == null) return;

        // Two triggers: a money floor and a days-left floor.
        // The money test used to require Balance > 0, which excluded the single most
        // urgent state a prepaid meter has: a NEGATIVE balance, i.e. already eating the
        // emergency credit. That normally still alerted through lowDays, because DaysLeft
        // clamps to 0 — but DaysLeft is null until a usage rate exists, so a meter with no
        // history yet went completely silent at exactly the wrong moment. Gate on whether
        // the balance is KNOWN, not on whether it is positive.
        bool lowMoney = f.BalanceKnown && f.Balance < meter.EffectiveLowTaka;
        bool lowDays = f.DaysLeft is { } d && d < meter.EffectiveLowDays;
        if (!lowMoney && !lowDays) return;

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (Store.LastAlert(meter.Id) == today) return;   // nag at most once a day

        // The OS may ALREADY be holding a 9am reminder for this meter today. That was the
        // second way one account produced two banners a few minutes apart: the queued alert
        // rang at 9:00, a background sync landed at 9:02, and the live alert rang too —
        // LastAlert never saw the queued one, because the queued one fires with no app code
        // running. ScheduleLowBalance now runs BEFORE this method and writes down what it
        // armed, so the answer is knowable.
        if (QueuedIndexFor(meter.Id, today) is { } queued)
        {
            if (DateTime.Now >= DateTime.Today.AddHours(9))
            {
                // It has already rung this morning. Burn today's slot and stay quiet.
                Store.SetLastAlert(meter.Id, today);
                return;
            }

            // It has not rung yet, and we are about to say the same thing with a LIVE
            // number instead of a projected one. Take the morning; drop the queued one.
            CancelOne(meter.Id, queued);
            ForgetQueued(meter.Id, today);
        }

        Show(L.S("alertTitle"),
            L.S("alertBody", Lead(meter, accountName),
                // Balance and days-left read as words in the sentence, so use L.NBn —
                // it converts to Bengali digits in Bn mode, stays Latin otherwise.
                // Lead is NOT wrapped in NBn: the account number in there is a token
                // the user compares to what's printed on the meter, which is Latin.
                L.NBn($"{f.Balance:0}"),
                L.NBn($"{f.DaysLeft:0.#}")));

        // Only after the alert has been posted. Marking it first meant a suppressed or
        // OS-denied notification still burned today's slot and the meter stayed silent
        // for the rest of the day even once permission was granted.
        Store.SetLastAlert(meter.Id, today);
    }

    // ---------------- a recharge that never reached the meter ----------------

    /// <summary>
    /// Fires once, the moment a failed recharge is DISPROVED by a meter read — not when
    /// the portal reports the failure. The difference is the whole point: at report time
    /// the money has usually arrived and a banner would be a false alarm; by the time
    /// this fires we have watched a full read cycle go by without the balance moving.
    ///
    /// Deliberately NOT gated by LastAlert. That guard exists to stop the low-balance nag
    /// repeating daily; this is a one-off about one transaction, and Reconcile's
    /// Verdict.Notified flag already makes it fire exactly once per recharge. Suppressing
    /// it because a low-balance alert happened to go out the same morning would drop the
    /// more actionable of the two — this one the user can fix in a minute with a keypad.
    /// </summary>
    public static void TokenNotApplied(Meter meter, RechargeRow r, string? accountName = null)
    {
        if (!Store.AlertsEnabled || !meter.NotificationsEnabled) return;

        Show(L.S("tokenAlertTitle"),
             L.S("tokenAlertBody", Lead(meter, accountName), L.N($"{r.Amount:#,0.##}")));
    }

    // ---------------- the session died ----------------

    /// <summary>
    /// DESCO's rebuilt portal (Nov 2026) requires a login, and a session can expire while
    /// the app is closed. When that happens there is nothing the app can do alone: the
    /// stored credential was rejected, so only the user can restore it. Until they do,
    /// every sync returns nothing and the balance on screen silently ages — which is the
    /// worst failure this app has, because it looks exactly like everything working.
    ///
    /// ONCE A DAY, not once a sync. Android's exact alarm plus WorkManager plus every app
    /// open can run this many times an hour, and a "sign in again" that repeats is one
    /// people learn to swipe away without reading.
    ///
    /// Deliberately NOT gated by Store.LastAlert. That slot belongs to the low-balance
    /// nag; burning it here would silence a genuinely urgent alert for the rest of the
    /// day, and these two say different things. The guard is its own Preferences key.
    ///
    /// Not scheduled ahead either, unlike ScheduleLowBalance: a dated reminder would keep
    /// firing after the user had already signed in, because nothing cancels it from the
    /// sign-in path. The next failed sync re-raises this one instead, which self-corrects.
    /// </summary>
    public static void SessionExpired(Meter meter)
    {
        if (!Store.AlertsEnabled || !meter.NotificationsEnabled) return;

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var key = "auth-notified-" + meter.Id;
        if (Preferences.Get(key, "") == today) return;

        Show(L.S("authExpiredTitle"),
             L.S("authExpiredBody2", Lead(meter, null)));

        // Marked after posting, for the same reason CheckLowBalance does: a suppressed or
        // OS-denied notification must not burn the day's slot.
        Preferences.Set(key, today);
    }

    /// <summary>
    /// Clear the once-a-day guard so the next failure can speak again. Called from the
    /// sign-in flow: a user who has just re-authenticated and then hits a second expiry
    /// the same day should be told, not left wondering why the balance stopped moving.
    /// </summary>
    public static void ClearSessionWarning(string meterId) =>
        Preferences.Remove("auth-notified-" + meterId);

    // ---------------- low later: the queued alert ----------------

    /// <summary>
    /// Arm (or re-arm) a notification for the day the balance is predicted to fall under
    /// the user's threshold. Called on every sync, so a recharge re-dates it and an
    /// alerts-off setting clears it. Replaces the meter's previous alert rather than
    /// stacking — a month of syncs must not queue a month of alarms.
    /// </summary>
    public static void ScheduleLowBalance(Meter meter, Forecast? f, string? accountName = null)
    {
        // Remember the id BEFORE doing anything else. Even the "nothing to arm" branch
        // below writes ScheduledAlerts, so the sweeper must be able to find this meter
        // later — otherwise an alerts-off setting followed by a delete leaves a ghost.
        Store.RememberMeterId(meter.Id);

        // Per-account switch is off — either globally or for this one meter. Tear down
        // anything the OS is already holding for it (a user who just flipped the switch
        // off expects the next queued morning banner to NOT fire), record an empty
        // scheduled set so CheckLowBalance can't see a stale entry, and return.
        if (!Store.AlertsEnabled || !meter.NotificationsEnabled)
        {
            CancelScheduled(meter.Id);
            ClearDeliveredFor(meter.Id);
            Store.SetScheduledAlerts(meter.Id, "");
            return;
        }

        var now = DateTime.Now;
        var todayKey = now.ToString("yyyy-MM-dd");

        // A reminder armed for TODAY has, by the time this runs, either already been
        // delivered by the OS or is still a few minutes away. Either way CheckLowBalance
        // needs to know it exists — and rebuilding the list from a clean slate ERASES it,
        // because the loop below skips today once 9am has gone past. That is what let the
        // live alert ring on top of a queued one that had fired seconds earlier: the sync
        // that was supposed to notice the collision destroyed the evidence first. Carry it.
        var carried = QueuedIndexFor(meter.Id, todayKey);

        // Clear the whole set up front; we re-arm below from a clean slate on every sync.
        CancelScheduled(meter.Id);
        // Spelled out rather than `f?.RunsOut is not { }` so that `f` narrows to non-null
        // for the Body() calls below.
        if (!Store.AlertsEnabled || f == null || f.RunsOut is not { } runsOut)
        {
            // Nothing to arm, but today's entry still has to survive: the balance can be
            // low on money with no usage rate to date it from, and CheckLowBalance would
            // otherwise duplicate this morning's queued alert.
            Store.SetScheduledAlerts(meter.Id, carried is { } keep ? $"{todayKey}:{keep}" : "");
            return;
        }

        var lead = Lead(meter, accountName);
        // The predicted "getting low" morning: LowDays before empty, at 9am — not at
        // whatever minute the sync happened to land on, and never at 3am.
        var predicted = runsOut.AddDays(-meter.EffectiveLowDays).Date.AddHours(9);

        // What was armed, so CheckLowBalance can avoid speaking over it. Written once at
        // the end rather than per-ScheduleAt: a half-written list is worse than none.
        var armed = new List<string>();

        if (predicted > now && predicted <= now.AddDays(60))
        {
            // Healthy meter: one heads-up, dated in advance. The OS fires it on the day
            // with no app wake — the whole reason this beats a background refresh.
            var (bal, days) = Project(f, predicted, now);
            ScheduleAt(meter.Id, 0, lead, bal, days, predicted);
            armed.Add($"{predicted:yyyy-MM-dd}:0");
        }
        else if (predicted <= now)
        {
            // Already inside the low window. The immediate alert (CheckLowBalance) only
            // shows if the app is open, so ALSO pre-schedule the next few 9am reminders.
            // Those are held by the OS and fire in the background with no launch needed —
            // which is exactly the "notify me without opening the app" case, and the only
            // way a sideloaded iOS build can do it (it cannot wake to check a live balance).
            int i = 0;
            // Starts at day 0, not day 1. The old loop began TOMORROW, so any sync landing
            // before 9am cancelled the whole set and re-armed it from the following day —
            // silently deleting the reminder that was minutes away, then doing it again the
            // next morning. `when <= now` already skips today once 9am has gone past, which
            // is the only case day 0 needs skipping for.
            for (int day = 0; day <= 7 && i < MaxReminders; day++)
            {
                var when = now.Date.AddDays(day).AddHours(9);
                if (when <= now) continue;
                var (bal, days) = Project(f, when, now);
                ScheduleAt(meter.Id, i, lead, bal, days, when);
                armed.Add($"{when:yyyy-MM-dd}:{i}");
                i++;
            }
        }
        // predicted more than 60 days out: nothing worth queuing; already cleared above.

        // Only if the re-arm didn't already produce an entry for today (a sync before 9am
        // re-arms today's, and that fresh entry is the accurate one).
        if (carried is { } c && !armed.Any(a => a.StartsWith(todayKey + ":", StringComparison.Ordinal)))
            armed.Add($"{todayKey}:{c}");

        Store.SetScheduledAlerts(meter.Id, string.Join(",", armed));
    }

    /// <summary>
    /// Language-free projection of the balance and days-left for a future fire date.
    /// Returns (null, null) when the forecast lacks a rate — the caller then knows to
    /// show the vague "likely low now" wording instead of a made-up number.
    /// </summary>
    static (double? balance, double? daysLeft) Project(Forecast f, DateTime when, DateTime now)
    {
        if (!f.BalanceKnown || f.DailyAvg is not { } rate || rate <= 0)
            return (null, null);
        var ahead = (when.Date - now.Date).TotalDays;
        var balance = f.Balance - rate * ahead;
        // Same formula Forecast uses for DaysLeft, applied to the projected balance, so the
        // two numbers in one sentence can never contradict each other.
        var daysLeft = Math.Max(0, balance / rate);
        return (balance, daysLeft);
    }

    /// <summary>
    /// Format the queued-alert body in the CURRENT language. On iOS this is called at
    /// schedule time (the OS holds an opaque string); on Android it's called by
    /// AlertWorker at fire time, so a language switch between arm and fire is honoured
    /// without needing a resync.
    /// </summary>
    internal static string FormatBody(string lead, double? projectedBalance, double? projectedDaysLeft)
    {
        // No balance figure or no usage rate: fall back to the vague wording rather
        // than print an invented number. Whichever language is current wins.
        if (projectedBalance is not { } bal || projectedDaysLeft is not { } days)
            return L.S("alertPredicted", lead);

        return L.S("alertBody", lead, L.NBn($"{bal:0}"), L.NBn($"{days:0.#}"));
    }

    // ---------------- bookkeeping for the armed set ----------------

    /// <summary>The idx of the reminder armed for <paramref name="date"/>, or null.</summary>
    static int? QueuedIndexFor(string meterId, string date)
    {
        foreach (var part in Store.ScheduledAlerts(meterId).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = part.LastIndexOf(':');
            if (cut > 0 && part[..cut] == date && int.TryParse(part[(cut + 1)..], out var idx))
                return idx;
        }
        return null;
    }

    static void ForgetQueued(string meterId, string date)
    {
        var kept = Store.ScheduledAlerts(meterId)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith(date + ":", StringComparison.Ordinal));
        Store.SetScheduledAlerts(meterId, string.Join(",", kept));
    }

    /// <summary>Drop every pending alert for a meter. Called when the meter is deleted —
    /// otherwise its reminders keep firing for days, naming an account that is gone.</summary>
    public static void CancelFor(string meterId)
    {
        CancelScheduled(meterId);
        // Also clear any banner ALREADY sitting on the lock screen for this meter, so
        // deleting the account visibly quiets it instead of leaving stale reminders
        // that name an account the app no longer holds.
        ClearDeliveredFor(meterId);
        Store.SetScheduledAlerts(meterId, "");
        // The session warning is keyed per meter too; a deleted account must not have a
        // stale "sign in again" guard left behind for an id that will never return.
        ClearSessionWarning(meterId);
    }

    /// <summary>
    /// Cancel every queued reminder for every meter currently in the store. Called on
    /// language change: the strings already handed to the OS are frozen in the OLD
    /// language, and the next sync re-arms the whole set in the current one.
    /// </summary>
    public static void CancelAllScheduled()
    {
        foreach (var m in Store.Meters())
        {
            CancelScheduled(m.Id);
            Store.SetScheduledAlerts(m.Id, "");
        }
        // Delivered banners are cleared too — after a language switch, leaving Bengali
        // banners on the lock screen for an English user (or vice versa) looks broken.
        ClearAllDeliveredLowBalance();
    }

    /// <summary>
    /// Walk the OS's pending and delivered notifications and drop any low-balance alert
    /// whose meter is no longer in the store. This is what stops a renamed or deleted
    /// account from continuing to fire under its old name for days. Called at startup,
    /// so a ghost queue clears itself on next launch.
    /// </summary>
    public static void SweepOrphans()
    {
        var alive = new HashSet<string>(Store.Meters().Select(m => m.Id), StringComparer.Ordinal);
        SweepOrphansPlatform(alive);
    }

    /// <summary>
    /// The identifying lead line for an alert: the account number first, then the account
    /// holder's name when we have it (the utility's CustomerName, falling back to the user's
    /// own label). Produces "A/C 14003679 · Md. Motaleb Mia", or just "A/C 14003679" when no
    /// name is known.
    /// </summary>
    static string Lead(Meter meter, string? accountName)
    {
        var name = !string.IsNullOrWhiteSpace(accountName) ? accountName!.Trim()
                 : (!string.IsNullOrWhiteSpace(meter.Label) ? meter.Label.Trim() : "");
        return name.Length > 0 ? $"A/C {meter.Account} · {name}" : $"A/C {meter.Account}";
    }

    // How many morning reminders to pre-schedule when the meter is ALREADY low. Each is a
    // separate OS-held notification, so a few days of background nudges survive even if the
    // app is never opened again (every sync re-arms the full set).
    const int MaxReminders = 3;
    static string Key(string meterId, int idx) => $"low-{meterId}-{idx}";

    // ---------------- platform: fire now ----------------

#if IOS
    static void Show(string title, string body)
    {
        var content = new UNMutableNotificationContent
        {
            Title = title, Body = body, Sound = UNNotificationSound.Default
        };
        var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(2, false);
        var req = UNNotificationRequest.FromIdentifier("now-" + Guid.NewGuid().ToString("N"), content, trigger);
        UNUserNotificationCenter.Current.AddNotificationRequest(req, _ => { });
    }
#elif ANDROID
    internal static void Show(string title, string body)
    {
        try
        {
            var ctx = Android.App.Application.Context;
            var mgr = (Android.App.NotificationManager?)ctx.GetSystemService(Android.Content.Context.NotificationService);
            if (mgr == null) return;

            // A NEW channel id, deliberately. Android freezes a channel's importance at
            // creation and ignores later changes from the app, so any device that already
            // created "low-balance" at Default would keep getting silent, shade-only
            // alerts forever. A fresh id lands at High: a heads-up banner with sound.
            const string channel = "low-balance-v2";
            mgr.CreateNotificationChannel(new Android.App.NotificationChannel(
                channel, "Balance alerts", Android.App.NotificationImportance.High));

            var n = new Android.App.Notification.Builder(ctx, channel)
                .SetContentTitle(title)
                .SetContentText(body)
                .SetStyle(new Android.App.Notification.BigTextStyle().BigText(body))
                .SetSmallIcon(ctx.ApplicationInfo!.Icon)
                .SetAutoCancel(true)
                .Build();

            // Stable per-content id, NOT Environment.TickCount. Two meters going low in the
            // same sync post within the same millisecond, and TickCount would hand them the
            // SAME id — the second notification would replace the first and one account's
            // warning would just vanish. The body carries the A/C number, so hashing the text
            // gives each meter its own notification slot.
            mgr.Notify(StableId(title + "\u0001" + body), n);
        }
        catch { /* a failed toast must never crash a sync */ }
    }

    /// <summary>Deterministic FNV-1a hash — stable across restarts, unlike string.GetHashCode.</summary>
    static int StableId(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }
#else
    static void Show(string title, string body) { }
#endif

    // ---------------- platform: fire on a future date ----------------

#if IOS
    static void ScheduleAt(string meterId, int idx, string lead,
                           double? projectedBalance, double? projectedDaysLeft, DateTime when)
    {
        // iOS freezes the notification body inside UNMutableNotificationContent; the OS
        // does not re-consult the app before firing. Format now in whatever language is
        // current — a later language switch will call CancelAllScheduled, and the next
        // sync will re-arm with the new language.
        var content = new UNMutableNotificationContent
        {
            Title = L.S("alertTitle"),
            Body = FormatBody(lead, projectedBalance, projectedDaysLeft),
            Sound = UNNotificationSound.Default
        };

        // The system holds this, not us. It survives backgrounding, termination, and a
        // force-quit from the switcher — none of which a BGAppRefreshTask survives.
        var parts = new NSDateComponents
        {
            Year = when.Year, Month = when.Month, Day = when.Day,
            Hour = when.Hour, Minute = when.Minute
        };
        var trigger = UNCalendarNotificationTrigger.CreateTrigger(parts, false);
        var req = UNNotificationRequest.FromIdentifier(Key(meterId, idx), content, trigger);
        UNUserNotificationCenter.Current.AddNotificationRequest(req, _ => { });
    }

    static void CancelScheduled(string meterId)
    {
        var ids = new List<string> { "low-" + meterId };   // legacy single-id, for upgrades
        for (int i = 0; i < MaxReminders; i++) ids.Add(Key(meterId, i));
        UNUserNotificationCenter.Current.RemovePendingNotificationRequests(ids.ToArray());
    }

    static void CancelOne(string meterId, int idx) =>
        UNUserNotificationCenter.Current.RemovePendingNotificationRequests(new[] { Key(meterId, idx) });

    // ---------- iOS orphan sweep ----------

    // Any pending "low-{id}-{idx}" whose id isn't in `alive` gets dropped. Same for the
    // legacy "low-{id}" identifier. Delivered banners for the same meter are cleared too
    // so the lock screen visibly quiets.
    static void SweepOrphansPlatform(HashSet<string> alive)
    {
        UNUserNotificationCenter.Current.GetPendingNotificationRequests(reqs =>
        {
            var kill = new List<string>();
            foreach (var r in reqs)
            {
                var id = r.Identifier;
                if (MeterIdFromLowKey(id) is { } mid && !alive.Contains(mid))
                    kill.Add(id);
            }
            if (kill.Count > 0)
                UNUserNotificationCenter.Current.RemovePendingNotificationRequests(kill.ToArray());
        });

        UNUserNotificationCenter.Current.GetDeliveredNotifications(notes =>
        {
            var kill = new List<string>();
            foreach (var n in notes)
            {
                var id = n.Request.Identifier;
                if (MeterIdFromLowKey(id) is { } mid && !alive.Contains(mid))
                    kill.Add(id);
            }
            if (kill.Count > 0)
                UNUserNotificationCenter.Current.RemoveDeliveredNotifications(kill.ToArray());
        });
    }

    static void ClearDeliveredFor(string meterId)
    {
        UNUserNotificationCenter.Current.GetDeliveredNotifications(notes =>
        {
            var kill = new List<string>();
            foreach (var n in notes)
            {
                var id = n.Request.Identifier;
                if (MeterIdFromLowKey(id) is { } mid && mid == meterId)
                    kill.Add(id);
            }
            if (kill.Count > 0)
                UNUserNotificationCenter.Current.RemoveDeliveredNotifications(kill.ToArray());
        });
    }

    static void ClearAllDeliveredLowBalance()
    {
        UNUserNotificationCenter.Current.GetDeliveredNotifications(notes =>
        {
            var kill = new List<string>();
            foreach (var n in notes)
            {
                var id = n.Request.Identifier;
                if (id.StartsWith("low-", StringComparison.Ordinal) ||
                    id.StartsWith("now-", StringComparison.Ordinal))
                    kill.Add(id);
            }
            if (kill.Count > 0)
                UNUserNotificationCenter.Current.RemoveDeliveredNotifications(kill.ToArray());
        });
    }

    // "low-{meterId}-{idx}" or the legacy "low-{meterId}". Returns null for anything else.
    static string? MeterIdFromLowKey(string id)
    {
        if (!id.StartsWith("low-", StringComparison.Ordinal)) return null;
        var rest = id.Substring(4);
        var dash = rest.LastIndexOf('-');
        if (dash <= 0) return rest;                          // legacy single-id form
        // Trailing segment must be the idx integer for the "-{idx}" form.
        return int.TryParse(rest[(dash + 1)..], out _) ? rest[..dash] : rest;
    }
#elif ANDROID
    static void ScheduleAt(string meterId, int idx, string lead,
                           double? projectedBalance, double? projectedDaysLeft, DateTime when)
    {
        try
        {
            var delay = when - DateTime.Now;
            if (delay <= TimeSpan.Zero) return;

            // Store the INGREDIENTS, not the finished sentence. AlertWorker formats them
            // at fire time using the language then in effect, so a Bengali user who
            // switches to English between arm and fire still gets an English banner
            // without needing a resync to re-arm the queue.
            var builder = new Data.Builder()
                .PutString(AlertWorker.KeyLead, lead);
            if (projectedBalance is { } bal && projectedDaysLeft is { } days)
            {
                builder.PutDouble(AlertWorker.KeyBalance, bal);
                builder.PutDouble(AlertWorker.KeyDaysLeft, days);
                builder.PutBoolean(AlertWorker.KeyHasNumbers, true);
            }
            else
            {
                builder.PutBoolean(AlertWorker.KeyHasNumbers, false);
            }
            var data = builder.Build()!;

            // Builder.Build() is typed as the base WorkRequest in the AndroidX binding,
            // while EnqueueUniqueWork wants the concrete OneTimeWorkRequest. Cast.
            var request = (OneTimeWorkRequest)new OneTimeWorkRequest.Builder(
                    Java.Lang.Class.FromType(typeof(AlertWorker))!)
                .SetInputData(data)!
                .SetInitialDelay((long)Math.Max(1, delay.TotalMinutes), Java.Util.Concurrent.TimeUnit.Minutes!)!
                .Build()!;

            WorkManager.GetInstance(Android.App.Application.Context)
                       .EnqueueUniqueWork(Key(meterId, idx), ExistingWorkPolicy.Replace!, request);
        }
        catch { /* WorkManager unavailable — the foreground alert still works */ }
    }

    static void CancelScheduled(string meterId)
    {
        try
        {
            var wm = WorkManager.GetInstance(Android.App.Application.Context);
            wm.CancelUniqueWork("low-" + meterId);                  // legacy single-id
            for (int i = 0; i < MaxReminders; i++) wm.CancelUniqueWork(Key(meterId, i));
        }
        catch { }
    }

    static void CancelOne(string meterId, int idx)
    {
        try { WorkManager.GetInstance(Android.App.Application.Context).CancelUniqueWork(Key(meterId, idx)); }
        catch { }
    }

    // ---------- Android orphan sweep ----------

    // Android's WorkManager can't be enumerated by prefix cheaply, so we sweep against a
    // shadow list of meter ids we've ever scheduled for (Store.KnownMeterIds). Any id in
    // that set but not in `alive` is a deleted meter — cancel its work, forget it.
    static void SweepOrphansPlatform(HashSet<string> alive)
    {
        var known = Store.KnownMeterIds();
        var orphans = known.Where(id => !alive.Contains(id)).ToList();
        foreach (var id in orphans)
        {
            CancelScheduled(id);
            Store.SetScheduledAlerts(id, "");
        }
        Store.SetKnownMeterIds(alive);
    }

    // Android delivered banners use a content-hash id we don't track per-meter, so we
    // can't cheaply target one meter. Cancel the whole low-balance channel: on a wipe
    // that's what the user wants anyway.
    static void ClearDeliveredFor(string meterId) => ClearAllDeliveredLowBalance();

    static void ClearAllDeliveredLowBalance()
    {
        try
        {
            var ctx = Android.App.Application.Context;
            var mgr = (Android.App.NotificationManager?)ctx.GetSystemService(Android.Content.Context.NotificationService);
            mgr?.CancelAll();
        }
        catch { }
    }
#else
    static void ScheduleAt(string meterId, int idx, string lead,
                           double? projectedBalance, double? projectedDaysLeft, DateTime when) { }
    static void CancelScheduled(string meterId) { }
    static void CancelOne(string meterId, int idx) { }
    static void SweepOrphansPlatform(HashSet<string> alive) { }
    static void ClearDeliveredFor(string meterId) { }
    static void ClearAllDeliveredLowBalance() { }
#endif
}