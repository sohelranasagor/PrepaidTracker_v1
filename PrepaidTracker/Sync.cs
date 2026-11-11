namespace PrepaidTracker;

/// <summary>
/// One sync, three callers: the foreground pull-to-refresh, the iOS background task,
/// and the Android WorkManager worker.
///
/// Record() used to exist twice — once in HomePage, once inside BackgroundSync's iOS
/// branch — and the two had already drifted apart: the background copy forgot the
/// r.Success filter, so a FAILED recharge counted as money credited. That inflates
/// spent = prevBalance + recharge - newBalance, which inflates the day's bar, the EWMA
/// rate, and days-left, and could fire a low-balance alert off a number that never
/// existed. Two copies of a rule is one copy too many.
///
/// DESCO's rebuilt portal (Nov 2026) added two constraints this class has to respect:
///
///   1. A SESSION. A sync can now fail because the login is gone, which is NOT the same
///      as a network failure: only the user can fix it. On that outcome we must not bank
///      a reading, not rebuild the forecast and not run Reconcile — there is no new
///      measurement, and inventing one would corrupt the interval arithmetic and leave
///      Unverified verdicts with no read dated after them.
///
///   2. A QUOTA on live meter reads — one successful read per day per meter, after which
///      the portal answers DAILY_LIMIT_REACHED. So background callers leave
///      userRequestedFresh false and read the portal's CACHED figure, which costs
///      nothing. Only an explicit pull-to-refresh spends an attempt.
/// </summary>
public static class Sync
{
    /// <summary>
    /// Fetch every meter, log a reading, alert if low, and arm the next alert.
    /// </summary>
    /// <param name="userRequestedFresh">
    /// True ONLY for an explicit user refresh. Lets DESCO spend one of the day's
    /// rationed live meter reads. Every background path must leave this false.
    /// </param>
    /// <returns>
    /// Ok when at least one meter synced; AuthRequired when nothing synced and at least
    /// one DESCO session needs the user; Failed otherwise.
    /// </returns>
    public static async Task<SyncOutcome> RunAllAsync(
        bool userRequestedFresh = false, CancellationToken ct = default)
    {
        bool any = false;
        bool authNeeded = false;

        foreach (var meter in Store.Meters())
        {
            if (ct.IsCancellationRequested) break;

            var provider = Providers.For(meter.Utility);

            // IProvider.FetchAsync takes an account string and is shared with
            // NescoProvider, so the fresh-read request rides on the provider INSTANCE
            // rather than the call signature. Providers.For hands back a new instance
            // per call, so there is no leakage between meters or between syncs.
            if (provider is DescoProvider dp) dp.UserRequestedFresh = userRequestedFresh;

            FetchResult result;
            try { result = await provider.FetchAsync(meter.Account, ct); }
            catch { continue; }                       // one dead meter must not kill the rest

            // The session is gone and only the user can restore it. Checked BEFORE the
            // result.Ok test, because an auth failure arrives as a plain failed fetch and
            // would otherwise be indistinguishable from the portal being down.
            if (provider is DescoProvider auth && auth.LastState == SessionState.NeedsLogin)
            {
                Notifier.SessionExpired(meter);
                authNeeded = true;
                continue;
            }

            if (!result.Ok || result.Snapshot == null) continue;

            // Cache the sanction load on the meter so a sync where the summary endpoint
            // didn't answer doesn't silently drop the suggestion back to its fallback
            // path. Written only on change — meters.json is rewritten whole.
            if (result.Snapshot.SanctionLoadKw > 0
                && Math.Abs(meter.SanctionLoadKw - result.Snapshot.SanctionLoadKw) > 0.01)
            {
                meter.SanctionLoadKw = result.Snapshot.SanctionLoadKw;
                Store.SaveMeter(meter);
            }

            Record(meter, result.Snapshot);

            var f = Forecast.Build(Store.Readings(meter.Id), result.Snapshot);

            // Settle every failed recharge this read has now overtaken. When one turns
            // out to have landed after all, the reading we just banked was computed as if
            // it hadn't, so bank it again with the credit counted. Record is idempotent —
            // AddReading replaces the row for that date.
            if (Reconcile.Run(meter, result.Snapshot, f?.DailyAvg ?? 0))
            {
                Record(meter, result.Snapshot);
                f = Forecast.Build(Store.Readings(meter.Id), result.Snapshot);
            }

            // ORDER MATTERS. Schedule first: it is what decides whether the OS is holding
            // a 9am reminder for this meter today, and CheckLowBalance has to know that to
            // avoid ringing a second time over the top of it.
            Notifier.ScheduleLowBalance(meter, f, result.Snapshot.CustomerName);    // low later → put it in the OS queue
            Notifier.CheckLowBalance(meter, f, result.Snapshot.CustomerName);       // low right now → tell them now
            any = true;
        }

        // A partial success is still a success: one DESCO account needing a login must
        // not report the whole sync as broken when a NESCO meter updated fine.
        if (any) return SyncOutcome.Ok;
        return authNeeded ? SyncOutcome.AuthRequired : SyncOutcome.Failed;
    }

    /// <summary>
    /// Cache the snapshot and keep our own reading log. The log is what the forecast is
    /// built from whenever the utility publishes no daily figure — which is always for
    /// NESCO, and now also for DESCO. A day the app never saw is a day that does not exist.
    /// </summary>
    public static void Record(Meter meter, Snapshot snap)
    {
        Store.SaveSnapshot(meter.Id, snap);
        if (snap.ReadingTime is { } rt) Store.RecordMeterUpdate(meter.Id, rt);

        // A sync with no reported balance must not log a phantom ৳0.00 reading —
        // one of those poisons the own-readings forecast for days.
        if (!snap.BalanceReported) return;

        // Date the reading by the meter's OWN read time, not our sync clock. Dating it by
        // the sync clock put the same balance under two different dates when the user
        // synced twice across a day the meter never re-read, banking a phantom ৳0 day.
        // Fall back to the sync date only when the portal gives no read time.
        var readingDate = (snap.ReadingTime ?? DateTime.Now).ToString("yyyy-MM-dd");

        // ReadAt is stored too. NESCO reads at midnight, so the date alone was enough to
        // say whether a recharge was inside a balance. DESCO's live read happens at any
        // hour: a recharge at 08:00 is already inside a 09:05 read on the same day, and
        // the date-only rule counted it a second time in the NEXT interval.
        var current = new Reading { Date = readingDate, ReadAt = snap.ReadingTime, Balance = snap.Balance };

        var prior = Store.Readings(meter.Id)
            .LastOrDefault(r => string.Compare(r.Date, readingDate, StringComparison.Ordinal) < 0);

        // Money that actually reached the meter between the previous read and this one:
        // the rows the portal called successful, plus the ones it called FAILED that a
        // later read proved landed anyway (see Reconcile).
        //
        // MULTI-TOKEN rows are excluded even when the portal marks them successful: a
        // meter key-change's Amount is not a single credit against a single balance rise.
        //
        // The window is "after the previous read, not after this one", decided by MOMENT
        // where both the recharge time and the read time are known (Forecast.After), and
        // by date otherwise — the original midnight rule, so readings saved by older builds
        // behave exactly as before.
        //
        // ENERGY credited, not the amount paid. The balance rises by energy — ৳1,000 paid
        // credits ৳957.12, or ৳789.12 on the month's first recharge — so counting the gross
        // amount overstated `spent` for every interval with a recharge in it. That inflated
        // the daily rate, shortened days-left, and the recharge suggestion then grossed the
        // inflated rate up a second time.
        current.Recharge = prior == null
            ? 0
            : snap.Recharges
                  .Where(r => !r.MultiToken
                           && (r.Success || Reconcile.IsCredited(meter.Id, r))
                           && Forecast.After(r, prior)
                           && !Forecast.After(r, current))
                  .Sum(r => Reconcile.Energy(r, snap));

        Store.AddReading(meter.Id, current);
    }
}
