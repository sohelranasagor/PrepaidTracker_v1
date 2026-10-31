namespace PrepaidTracker;

/// <summary>
/// When to wake next.
///
/// Both utilities read the meter ONCE A DAY and publish that moment: NESCO in the
/// "(সময়ঃ 11 July 2026 12:00:00 AM)" caption, DESCO in getBalance's readingTime. Between
/// two reads there is no new information to fetch — the balance, the month-to-date and
/// the recharge list are all identical no matter how often we ask. So the useful question
/// is not "how often should we poll" but "when does the number actually change".
///
/// A fixed period answers neither. It fires 24h after whenever it was first enqueued,
/// which is whenever the user first opened the app — so a user who installed at 3pm gets
/// a 3pm sync forever, fetching a figure that last moved fifteen hours earlier, and the
/// low-balance alert is dated from data that stale.
///
/// This targets the read instead: last read + 24h + a small margin. Two consequences
/// worth stating, because they are the whole point:
///
///   • The target is ABSOLUTE, derived from the meter's own clock. Recomputing it when
///     the app launches cannot push it later, which is what made re-enqueueing safe here
///     when it wasn't for a fixed period.
///   • Catching each read promptly is what earns a real daily series. A day of NESCO
///     consumption only exists as the delta between two consecutive reads; miss one and
///     that day is gone for good, because the portal never publishes it.
/// </summary>
public static class SyncSchedule
{
    /// <summary>How often either utility re-reads the meter.</summary>
    public static readonly TimeSpan ReadPeriod = TimeSpan.FromHours(24);

    /// <summary>Grace after the expected read before we go looking for it.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(20);

    /// <summary>First retry when the new figure hasn't been posted yet.</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(45);

    /// <summary>Ceiling on the overdue backoff.</summary>
    public static readonly TimeSpan MaxRetry = TimeSpan.FromHours(6);

    /// <summary>Android's WorkManager will not schedule anything sooner than this.</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromMinutes(15);

    /// <summary>Never sleep longer than one read period.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromHours(24);

    /// <summary>No reading stored yet — check back soon enough to establish one.</summary>
    public static readonly TimeSpan NoReadingYet = TimeSpan.FromHours(6);

    /// <summary>
    /// Delay until the next worthwhile sync, given every meter's last known read time.
    /// The earliest meter wins: one wake syncs them all anyway.
    /// </summary>
    public static TimeSpan NextDelay(IEnumerable<DateTime?> lastReads, DateTime now)
    {
        TimeSpan? soonest = null;

        foreach (var read in lastReads)
        {
            if (read is not { } r) continue;

            var due = r + ReadPeriod + Margin;
            TimeSpan delay;

            if (due > now)
            {
                delay = due - now;
            }
            else
            {
                // Overdue: we expected a new figure and the portal hasn't posted one.
                // Back off in proportion to how late it is rather than retrying every
                // 45 minutes for the rest of the day — a meter that stopped reporting
                // must not turn into a polling loop against a government portal.
                var lateHours = Math.Floor((now - due).TotalHours);
                var scaled = FirstRetry.Ticks * (1 + lateHours);
                delay = TimeSpan.FromTicks((long)Math.Min(scaled, MaxRetry.Ticks));
            }

            if (soonest == null || delay < soonest) soonest = delay;
        }

        if (soonest is not { } chosen) return NoReadingYet;
        if (chosen < Floor) return Floor;
        if (chosen > Ceiling) return Ceiling;
        return chosen;
    }

    /// <summary>The same calculation, reading the times straight out of the store.</summary>
    public static TimeSpan NextDelay() =>
        NextDelay(Store.Meters().Select(m => Store.Snapshot(m.Id)?.ReadingTime), DateTime.Now);
}
