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
///   2. A QUOTA on live meter reads — roughly one per day per meter, after which the
///      portal answers DAILY_LIMIT_REACHED. So background callers leave
///      userRequestedFresh false and read the portal's CACHED figure, which costs
///      nothing and is the same midnight-snapshot balance the old API returned. Only an
///      explicit pull-to-refresh spends an attempt. Syncing live on every app open —
///      what this app used to do — would exhaust the day's allowance before noon and
///      leave the user unable to refresh at the moment it mattered.
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
            // would otherwise be indistinguishable from the portal being down — which is
            // the one thing the user must be told apart, since one needs a tap and the
            // other needs patience.
            if (provider is DescoProvider auth && auth.LastState == SessionState.NeedsLogin)
            {
                Notifier.SessionExpired(meter);
                authNeeded = true;
                continue;
            }

            if (!result.Ok || result.Snapshot == null) continue;

            // The portal reports the connection's sanction load, and the demand charge it
            // predicts is worth real money in the recharge suggestion. Cache it on the
            // meter so a sync where the summary endpoint didn't answer doesn't silently
            // drop the suggestion back to its fallback path. Written only on change —
            // this runs on every background wake and meters.json is rewritten whole.
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
            // it hadn't: spent = prev + 0 - new goes NEGATIVE and Forecast drops the whole
            // day. So bank it again with the credit counted. Record is idempotent —
            // AddReading replaces the row for that date — so the second call is a
            // correction, not a duplicate, and it only happens on the one sync that
            // settles a verdict.
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
        // not report the whole sync as broken when a NESCO meter updated fine. The
        // expired session was already surfaced by its own notification.
        if (any) return SyncOutcome.Ok;
        return authNeeded ? SyncOutcome.AuthRequired : SyncOutcome.Failed;
    }

    /// <summary>
    /// Cache the snapshot and keep our own reading log. The log is what the forecast is
    /// built from whenever the utility publishes no daily figure — which is always for
    /// NESCO, and now also for DESCO, whose rebuilt portal exposes no daily endpoint we
    /// have located. A day the app never saw is a day that does not exist.
    /// </summary>
    public static void Record(Meter meter, Snapshot snap)
    {
        Store.SaveSnapshot(meter.Id, snap);
        if (snap.ReadingTime is { } rt) Store.RecordMeterUpdate(meter.Id, rt);

        // A sync with no reported balance must not log a phantom ৳0.00 reading —
        // one of those poisons the own-readings forecast for days.
        if (!snap.BalanceReported) return;

        // Date the reading by the meter's OWN read time, not our sync clock. The balance
        // we just stored is the meter's figure as of ReadingTime — DESCO's
        // lastRealtimeReceivedAt, NESCO's (সময়ঃ …) caption — so a reading whose
        // ReadingTime is "15 Jul 12:00 AM" belongs to the 15th no matter what hour we
        // happened to sync. Dating it by the sync clock instead put the same midnight
        // balance under two different dates when the user synced twice across a day the
        // meter never re-read, banking a phantom ৳0 day, and shifted the derived daily
        // bars off by a day. Fall back to the sync date only when the portal gives no
        // read time.
        var readingDate = (snap.ReadingTime ?? DateTime.Now).ToString("yyyy-MM-dd");
        var prior = Store.Readings(meter.Id)
            .LastOrDefault(r => string.Compare(r.Date, readingDate, StringComparison.Ordinal) < 0);

        // Money that actually reached the meter: the ones the portal called successful,
        // plus the ones it called FAILED that a later read proved landed anyway (see
        // Reconcile). A failed row is not evidence of anything on its own — but once a
        // reading has risen by the recharge's energy, refusing to count it here is
        // exactly what pushes `spent` negative and makes Forecast drop the day.
        //
        // MULTI-TOKEN rows are excluded even when the portal marks them successful. DESCO
        // emits a meter key-change as one transaction carrying a SET of tokens (sequence
        // "-3~9", twelve of them); its Amount is not a single credit against a single
        // balance rise, so adding it here would inflate `spent` by hundreds of taka and
        // then push the real day negative — the same failure as the two bugs above,
        // reached from a third direction.
        //
        // The window is bounded on BOTH sides, and that matters because balances are
        // midnight snapshots. A recharge dated D happens DURING day D — hours after that
        // day's midnight read — so it is NOT in the balance dated D; it first shows up in
        // the balance dated D+1. Attributing it to the interval that ENDS at D's midnight
        // (the old open-ended "> prior.Date" rule did exactly that for a same-day
        // recharge) adds the recharge to `spent` while the balance hasn't risen yet,
        // inflating that one bar by the whole recharge amount and then pushing the real
        // recharge-day usage negative so Forecast drops it. Counting only recharges in
        // [prior.Date, readingDate) attributes each recharge to the first interval whose
        // ending balance actually reflects it; consecutive intervals stay contiguous and
        // non-overlapping.
        double recharge = prior == null
            ? 0
            : snap.Recharges
                  .Where(r => !r.MultiToken
                           && (r.Success || Reconcile.IsCredited(meter.Id, r))
                           && string.Compare(r.Date, prior.Date, StringComparison.Ordinal) >= 0
                           && string.Compare(r.Date, readingDate, StringComparison.Ordinal) < 0)
                  .Sum(r => r.Amount);

        Store.AddReading(meter.Id, new Reading
        {
            Date = readingDate,
            Balance = snap.Balance,
            Recharge = recharge
        });
    }
}