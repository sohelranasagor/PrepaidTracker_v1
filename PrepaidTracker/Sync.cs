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
/// </summary>
public static class Sync
{
    /// <summary>Fetch every meter, log a reading, alert if low, and arm the next alert.</summary>
    public static async Task<bool> RunAllAsync(CancellationToken ct = default)
    {
        bool any = false;

        foreach (var meter in Store.Meters())
        {
            if (ct.IsCancellationRequested) break;

            FetchResult result;
            try { result = await Providers.For(meter.Utility).FetchAsync(meter.Account, ct); }
            catch { continue; }                       // one dead meter must not kill the rest

            if (!result.Ok || result.Snapshot == null) continue;

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

        return any;
    }

    /// <summary>
    /// Cache the snapshot and keep our own reading log. The log is what NESCO's forecast
    /// is built from — the portal publishes no daily figure, so a day the app never saw
    /// is a day that does not exist.
    /// </summary>
    public static void Record(Meter meter, Snapshot snap)
    {
        Store.SaveSnapshot(meter.Id, snap);
        if (snap.ReadingTime is { } rt) Store.RecordMeterUpdate(meter.Id, rt);

        // A sync with no reported balance must not log a phantom ৳0.00 reading —
        // one of those poisons the own-readings forecast for days.
        if (!snap.BalanceReported) return;

        // Date the reading by the meter's OWN read time, not our sync clock. The balance we
        // just stored is the meter's figure as of ReadingTime — NESCO reads once, at midnight
        // — so a reading whose ReadingTime is "15 Jul 12:00 AM" belongs to the 15th no matter
        // what hour we happened to sync. Dating it by the sync clock instead put the same
        // midnight balance under two different dates when the user synced twice across a day
        // the meter never re-read, banking a phantom ৳0 day; and it shifted the derived daily
        // bars off by a day. Fall back to the sync date only when the portal gives no read time.
        var readingDate = (snap.ReadingTime ?? DateTime.Now).ToString("yyyy-MM-dd");
        var prior = Store.Readings(meter.Id)
            .LastOrDefault(r => string.Compare(r.Date, readingDate, StringComparison.Ordinal) < 0);

        // Money that actually reached the meter: the ones the portal called successful,
        // plus the ones it called FAILED that a later read proved landed anyway (see
        // Reconcile). A failed row is not evidence of anything on its own — but once a
        // reading has risen by the recharge's energy, refusing to count it here is
        // exactly what pushes `spent` negative and makes Forecast drop the day.
        //
        // The window is bounded on BOTH sides, and that matters because balances are
        // midnight snapshots. A recharge dated D happens DURING day D — hours after that
        // day's midnight read — so it is NOT in the balance dated D; it first shows up in
        // the balance dated D+1. Attributing it to the interval that ENDS at D's midnight
        // (the old open-ended "> prior.Date" rule did exactly that for a same-day recharge)
        // adds the recharge to `spent` while the balance hasn't risen yet, inflating that
        // one bar by the whole recharge amount — the ৳500+ spike — and then pushing the
        // real recharge-day usage negative so `Forecast` drops it. Counting only recharges
        // in [prior.Date, readingDate) attributes each recharge to the first interval whose
        // ending balance actually reflects it; consecutive intervals stay contiguous and
        // non-overlapping.
        double recharge = prior == null
            ? 0
            : snap.Recharges
                  .Where(r => (r.Success || Reconcile.IsCredited(meter.Id, r))
                           && string.Compare(r.Date, prior.Date, StringComparison.Ordinal) >= 0
                           && string.Compare(r.Date, readingDate, StringComparison.Ordinal) < 0)
                  .Sum(r => r.Amount);

        Store.AddReading(meter.Id, new Reading { Date = readingDate, Balance = snap.Balance, Recharge = recharge });
    }
}
