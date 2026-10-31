using System.Globalization;

namespace PrepaidTracker;

/// <summary>
/// The usage model. Daily spend comes from the utility when it publishes one
/// (DESCO does), otherwise it's derived from balance readings we take ourselves
/// (NESCO). On top of that: an exponentially-weighted moving average, so recent
/// days count more and a heat wave shows up within a day or two instead of being
/// diluted by last month's mild weather.
/// </summary>
public class Forecast
{
    public double Balance;

    /// <summary>
    /// Whether <see cref="Balance"/> means anything. False when the utility answered but
    /// published no balance figure (DESCO's unified accounts), where Balance is a
    /// placeholder 0 that must not be read as "empty meter".
    /// </summary>
    public bool BalanceKnown = true;
    public double? DailyAvg;          // ৳/day — the model's estimate (EWMA, includes zero days;
                                      // used internally for days-left, month-end, LiveEstimate)
    public double? SimpleAvg;         // ৳/day — flat 7-day mean (includes zeros; internal only)
    public double? ActiveAvg;         // ৳/day — flat mean over days that ACTUALLY consumed
                                      // (excludes zeros). This is the number displayed as
                                      // "দৈনিক গড় / Daily average" to the user, because a
                                      // vacation day shouldn't drag your typical-day figure down.
    public double? WeeklyCost;
    public double? DaysLeft;
    public DateTime? RunsOut;
    public double? UnitsPerDay;
    public double? SuggestedRecharge; // covers Store.TargetDays, VAT included, ৳100 steps

    /// <summary>
    /// Today's spend — NULL when nothing about today was actually measured. It used to be
    /// a plain double, which meant "unknown" and "৳0 spent" were the same value, and the
    /// monthly-fallback path filled it with the flat daily rate. The UI then labelled a
    /// month-average as "আজকের খরচ", and the Insights observation compared Today against
    /// an average that WAS Today — so "spending below average" was printed every day, for
    /// every meter with no daily feed, and could never be false.
    /// </summary>
    public double? Today;

    /// <summary>
    /// The most recent COMPLETED day's spend, or null when no daily data exists at all.
    /// Today is frequently unknowable — NESCO publishes no daily figure ever, and a DESCO
    /// feed posts a day only once it closes — which left the "Today" cell showing a dash
    /// on every account, every day. This is the honest thing to put there instead: a real
    /// measured day under its own label, rather than a permanently empty column or a
    /// month-average wearing today's name.
    /// </summary>
    public double? LastFullDay;
    public double MaxDay;             // heaviest day in the window
    public string MaxDayDate = "";
    public double WindowTotal;        // total over the chart window
    public double MonthSpent;
    public bool MonthSpentEstimated;  // true when unobserved elapsed days were filled at the daily rate
    public double MonthRecharged;
    public double? MonthEndEstimate;  // projected total for this calendar month
    public double? SavingsPct;        // vs your own longer-run average; +ve = saving

    public double Confidence;         // 0–1, how much history backs the estimate
    public List<DayRow> Series = new();  // daily spend, oldest → newest
    public bool FromUtility;             // utility gave us a real daily feed
    public bool FromMonthly;             // no daily feed: rate inferred from a monthly total
    public bool MonthIsPrevious;         // that total was last month's, not this month's
    public string MonthLabel = "";       // yyyy-MM the rate came from
    public List<MonthRow> Months = new();// monthly spend, for the NESCO chart

    const double Alpha = 0.35;
    const double Vat = 0.05;

    public static Forecast? Build(List<Reading> readings, Snapshot? snap)
    {
        if (snap == null && readings.Count == 0) return null;

        var f = new Forecast { Balance = snap?.Balance ?? readings.LastOrDefault()?.Balance ?? 0 };

        // DESCO's unified meters can sync with NO balance figure. A missing
        // balance is not ৳0.00: computing days-left from it yields 0, which
        // fired a false "balance low" notification every day and put
        // "0 days left" on the Insights hero. Rate estimates stay valid;
        // anything derived from the balance itself must stay null.
        bool balKnown = snap?.BalanceReported ?? true;
        f.BalanceKnown = balKnown;

        // ---- daily series ----
        if (snap is { Days.Count: > 0 })
        {
            f.Series = snap.Days.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
            f.FromUtility = true;
        }
        else
        {
            var sorted = readings.OrderBy(r => r.Date, StringComparer.Ordinal).ToList();
            for (int i = 1; i < sorted.Count; i++)
            {
                var days = (Date(sorted[i].Date) - Date(sorted[i - 1].Date)).Days;
                if (days <= 0) continue;
                var spent = sorted[i - 1].Balance + sorted[i].Recharge - sorted[i].Balance;
                if (spent < 0) continue;                  // topped up off-book; skip
                // The drop between a reading at day D's midnight and the next at day D+1's
                // midnight is what was used DURING day D — so the bar belongs to the earlier
                // reading's date onward, not the later one. AddDays(d) stamped it a day late
                // (a delta ending at the 15 Jul read is the 14th's usage, not the 15th's).
                for (int d = 1; d <= days; d++)
                    f.Series.Add(new DayRow { Date = Date(sorted[i - 1].Date).AddDays(d - 1).ToString("yyyy-MM-dd"), Taka = spent / days });
            }

            // A derived series has to actually say something. NESCO reads the meter once a
            // day, so two syncs either side of that read see the SAME balance: spent = 0,
            // and we bank a single ৳0.00 day. That one row is not zero rows, so the monthly
            // fallback below was skipped, ewma came out 0, and every forecast field went
            // null — a meter with a year of monthly history rendered as ৳0 spent, ৳0 a day,
            // no bill estimate and no days-left pill. Throw a series like that away and let
            // the monthly total do its job.
            if (f.Series.Count < 2 || f.Series.Sum(d => d.Taka) <= 0)
                f.Series.Clear();
        }

        // ---- no daily series? NESCO still tells us this month's total. ----
        // Spread it across the days elapsed so the month cards and the forecast
        // have something honest to show on the very first sync.
        if (f.Series.Count == 0)
        {
            f.Months = snap?.Months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList() ?? new();

            var thisMonth = DateTime.Now.ToString("yyyy-MM");
            var elapsed = DateTime.Now.Day;

            // Prefer this month's running total. NESCO usually hasn't posted it yet,
            // so fall back to the last complete month and divide by *its* length.
            double monthTaka = snap?.Months.FirstOrDefault(m => m.Month == thisMonth)?.Taka
                               ?? snap?.CurrentMonthConsumption ?? 0;
            int divisor = elapsed;
            f.MonthLabel = thisMonth;

            if (monthTaka <= 0)
            {
                var prev = f.Months.LastOrDefault(m => string.Compare(m.Month, thisMonth, StringComparison.Ordinal) < 0 && m.Taka > 0);
                if (prev != null)
                {
                    monthTaka = prev.Taka;
                    var firstOfPrev = Date(prev.Month + "-01");
                    divisor = DateTime.DaysInMonth(firstOfPrev.Year, firstOfPrev.Month);
                    f.MonthIsPrevious = true;
                    f.MonthLabel = prev.Month;
                }
            }

            if (monthTaka > 0 && divisor >= 1)
            {
                var rate = monthTaka / divisor;
                f.FromMonthly = true;
                f.DailyAvg = rate;
                f.SimpleAvg = rate;
                // No daily-level shape to separate quiet from active days when we're
                // working from a monthly total alone. The monthly-derived rate is our
                // best "typical daily use" figure.
                f.ActiveAvg = rate;
                f.WeeklyCost = rate * 7;
                // Today stays NULL. A monthly total divided by elapsed days says nothing
                // about today in particular; assigning it here is what made the "today vs
                // average" comparison tautological.
                f.Confidence = 0.35;                        // one monthly figure, no daily shape
                // Same coverage-aware month math as the daily path. With no daily rows,
                // month-to-date becomes rate x elapsed days and the estimate rate x full
                // month — except when this month's own total is known, which wins.
                FillMonth(f, rate, snap);

                if (rate > 0)
                {
                    if (balKnown)
                    {
                        // Never below zero. A meter on emergency credit divides a
                        // NEGATIVE balance by the daily rate and used to report
                        // "-1 days left", which is not a thing: you have no days.
                        f.DaysLeft = Math.Max(0, f.Balance / rate);
                        f.RunsOut = DateTime.Now.AddDays(f.DaysLeft.Value);
                    }
                    var need = rate * Store.TargetDays - (balKnown ? f.Balance : 0);
                    f.SuggestedRecharge = Suggest(need, snap);
                }
                if (Store.Tariff is > 0) f.UnitsPerDay = rate / Store.Tariff.Value;

                f.MonthRecharged = snap is { Recharges.Count: > 0 }
                    ? snap.Recharges.Where(r => r.Date.StartsWith(thisMonth) && r.Success).Sum(r => r.Amount)
                    : 0;
            }
            return f;                                        // balance + rate; the chart is monthly
        }

        f.Months = snap?.Months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList() ?? new();

        // The rate must come from COMPLETE days only. Two kinds of incomplete day were
        // dragging it toward zero: today (a running, mid-day figure), and — the one the
        // date check missed — the newest day a once-daily meter or a lagging feed hasn't
        // posted yet, which sits at ৳0 but is dated *yesterday*, not today. Completed()
        // drops today and, for utility-fed series only, any trailing ৳0 run. For NESCO
        // (readings-derived), a trailing ৳0 is a confirmed empty-house day and stays in;
        // the EWMA correctly drifts down to reflect a genuinely quieter recent pattern.
        var todayKey = DateTime.Now.ToString("yyyy-MM-dd");
        var rateDays = Completed(f.Series, dropTrailingZeros: f.FromUtility);
        if (rateDays.Count == 0) rateDays = f.Series;   // nothing complete yet: a partial rate beats none

        // ---- window stats (last 7 completed days) ----
        var week = rateDays.TakeLast(7).ToList();
        f.WindowTotal = week.Sum(d => d.Taka);
        f.SimpleAvg = week.Average(d => d.Taka);
        // Active-day average: same window, but only days that actually consumed. This is
        // the "typical use" number people expect when they see "Daily average" — a
        // vacation day at ৳0 should not drag the typical figure down. Null when the whole
        // window was quiet; the display then shows "—" rather than a phantom zero.
        var activeWeek = week.Where(d => d.Taka > 0).ToList();
        f.ActiveAvg = activeWeek.Count > 0 ? activeWeek.Average(d => d.Taka) : (double?)null;
        var heaviest = week.OrderByDescending(d => d.Taka).First();
        f.MaxDay = heaviest.Taka;
        f.MaxDayDate = heaviest.Date;

        // Only a row actually dated today counts. The old `?? rateDays[^1].Taka` fallback
        // showed YESTERDAY's spend under a "Today" label whenever the feed lagged — which
        // for a once-daily DESCO feed is most of the day, every day.
        f.Today = f.Series.LastOrDefault(d => d.Date == todayKey)?.Taka;

        // The newest day we actually hold a complete figure for. rateDays has today and
        // any unposted trailing days already stripped, so its last row is exactly that.
        f.LastFullDay = rateDays.Count > 0 ? rateDays[^1].Taka : null;

        // ---- EWMA over ACTIVE completed days, oldest → newest ----
        // A day the user genuinely consumed nothing (empty house, day trip) is signal
        // for the LiveEstimate anomaly guard, but noise for the long-run rate: leaving
        // it in drags the forecast down so a returning user sees "5 days" when their
        // actual usage pattern points to 3 — and worse, the days-left pill then contradicts
        // the "Daily average" number on the same card, which excludes those zero days
        // (see ActiveAvg above). Both figures now share one active-day world.
        //
        // If EVERY completed day was quiet, activeDays is empty; ewma falls back to 0
        // and the downstream `if (ewma > 0)` block correctly suppresses days-left and
        // recharge suggestions. Nothing to project from.
        var activeDays = rateDays.Where(d => d.Taka > 0).ToList();
        double ewma = activeDays.Count > 0 ? activeDays[0].Taka : 0;
        foreach (var d in activeDays.Skip(1)) ewma = Alpha * d.Taka + (1 - Alpha) * ewma;

        // Weekday shape: only with 3+ weeks of ACTIVE data, clamped so one freak day
        // can't skew it. Using active days here too keeps the ratio slot.Mean / overall
        // meaningful — a Saturday that's sometimes an empty-house day shouldn't average
        // to half its actual-use figure.
        var byDay = activeDays.GroupBy(d => Date(d.Date).DayOfWeek)
                              .ToDictionary(g => g.Key, g => (Count: g.Count(), Mean: g.Average(v => v.Taka)));
        var tomorrow = DateTime.Now.AddDays(1).DayOfWeek;
        var overall = activeDays.Count > 0 ? activeDays.Average(d => d.Taka) : 0;
        if (activeDays.Count >= 21 && overall > 0 && byDay.TryGetValue(tomorrow, out var slot) && slot.Count >= 3)
            ewma *= Math.Clamp(slot.Mean / overall, 0.75, 1.25);

        f.DailyAvg = ewma;
        f.WeeklyCost = ewma * 7;
        f.Confidence = Math.Clamp(f.Series.Count / 21.0, 0.1, 1.0);

        if (ewma > 0)
        {
            if (balKnown)
            {
                f.DaysLeft = Math.Max(0, f.Balance / ewma);   // see note above: never negative
                f.RunsOut = DateTime.Now.AddDays(f.DaysLeft.Value);
            }
            var needed = ewma * Store.TargetDays - (balKnown ? f.Balance : 0);
            f.SuggestedRecharge = Suggest(needed, snap);
        }

        if (Store.Tariff is > 0) f.UnitsPerDay = ewma / Store.Tariff.Value;

        // ---- month ----
        var month = DateTime.Now.ToString("yyyy-MM");
        // Month-to-date and the month-end projection, coverage-aware: a partial daily
        // feed no longer masquerades as a whole month (see FillMonth).
        FillMonth(f, ewma, snap);
        f.MonthRecharged = snap is { Recharges.Count: > 0 }
            ? snap.Recharges.Where(r => r.Date.StartsWith(month) && r.Success).Sum(r => r.Amount)
            : readings.Where(r => r.Date.StartsWith(month)).Sum(r => r.Recharge);

        // ---- "you're spending X% less than usual" ----
        // Compare this month's daily rate against everything before this month.
        var before = f.Series.Where(d => !d.Date.StartsWith(month)).ToList();
        var currentMonthDays = f.Series.Where(d => d.Date.StartsWith(month)).ToList();
        if (before.Count >= 7 && currentMonthDays.Count >= 3)
        {
            var baseline = before.Average(d => d.Taka);
            if (baseline > 0)
                f.SavingsPct = (baseline - currentMonthDays.Average(d => d.Taka)) / baseline * 100;
        }

        return f;
    }

    /// <summary>
    /// Month-to-date spend and the projected month-end bill, computed the same way for
    /// every provider. The rule a short DESCO feed used to break: a partial daily feed
    /// is NOT the whole month. Month-to-date is taken, in order of trust:
    ///   1. the utility's own month-to-date, when it publishes one for THIS month
    ///      (DESCO's currentMonthConsumption, or a current-month monthly row);
    ///   2. else the daily rows we actually hold, with any *unobserved* elapsed days
    ///      filled at the current daily rate — so two days of feed on the 15th no longer
    ///      report the month as having cost only two days.
    /// The month-end estimate projects every not-yet-covered day (unobserved elapsed +
    /// future) at the same rate, so it degrades gracefully from a full feed down to none.
    /// </summary>
    /// <summary>
    /// What fraction of a payment actually reaches the meter as energy.
    ///
    /// The old code assumed exactly (1 − VAT). Both portals report what each recharge
    /// really credited, and the observed figure is not 5%: across DESCO and NESCO alike a
    /// ৳990 recharge credits ৳947.55 and a ৳500 credits ৳478.56 — 4.288%, every time.
    /// Guessing when the answer is already in the data is the same mistake as the
    /// fabricated "today", just smaller.
    ///
    /// MEDIAN of recent recharges, not the mean: a recharge that also cleared arrears or a
    /// demand charge credits far less (one observed case went ৳990 → ৳781.35, a 21% cut)
    /// and would drag an average into suggesting more than the customer needs. The median
    /// of the last handful tracks the ordinary case and shrugs off the exceptional one.
    /// Falls back to the VAT constant when no recharge reports an energy figure.
    /// </summary>
    internal static double CreditRatio(Snapshot? snap)
    {
        var ratios = snap?.Recharges
            .Where(r => r.Success && r.Amount > 0 && r.EnergyAmount > 0
                        && r.EnergyAmount <= r.Amount)
            .OrderByDescending(r => r.Date, StringComparer.Ordinal)
            .Take(6)
            .Select(r => r.EnergyAmount / r.Amount)
            .OrderBy(x => x)
            .ToList();

        if (ratios is not { Count: > 0 }) return 1 - Vat;

        var median = ratios[ratios.Count / 2];
        return median is > 0.5 and <= 1 ? median : 1 - Vat;
    }

    /// <summary>
    /// What to pay to buy this much credit: grossed up by the observed credit ratio,
    /// rounded to a ৳50 step, and never below the minimum the utility publishes —
    /// suggesting ৳150 where NESCO will not accept less than ৳500 is advice the customer
    /// cannot act on. One implementation, called from both the daily and monthly paths.
    /// </summary>
    static double? Suggest(double energyNeeded, Snapshot? snap)
    {
        if (energyNeeded <= 0) return null;

        var gross = Math.Ceiling(energyNeeded / CreditRatio(snap) / 50) * 50;

        if (snap?.MinRecharge is { } min && min > 0 && gross < min)
            gross = Math.Ceiling(min / 50) * 50;

        return gross;
    }

    static void FillMonth(Forecast f, double rate, Snapshot? snap)
    {
        var now = DateTime.Now;
        var month = now.ToString("yyyy-MM");
        int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        int elapsed = now.Day;                       // the 1st through today, inclusive
        int daysLeft = daysInMonth - elapsed;

        // 1) authoritative month-to-date for THIS month, if the utility gives one.
        double? auth = null;
        if (snap?.CurrentMonthConsumption > 0)
            auth = snap.CurrentMonthConsumption;                 // DESCO getBalance
        else
        {
            var row = snap?.Months.FirstOrDefault(m => m.Month == month);
            if (row is { Taka: > 0 }) auth = row.Taka;           // current-month monthly total
        }

        // 2) what the daily rows actually cover this month.
        var monthRows = f.Series.Where(d => d.Date.StartsWith(month)).ToList();
        int coveredDays = monthRows.Select(d => d.Date).Distinct().Count();
        double coveredSum = monthRows.Sum(d => d.Taka);

        if (auth is > 0)
        {
            // The utility's own month-to-date — but measured AS OF ITS LAST READ, not as
            // of now. Both portals read once, around midnight, so by evening this figure
            // is most of a day behind while the card presents it as exact and current.
            // Coverage is the test everywhere else in this method; apply it here too and
            // fill the elapsed time at the daily rate, exactly the walk-forward
            // LiveEstimate already performs on the balance, using the same 6-hour
            // freshness threshold (under that, the measured figure IS the answer, and the
            // guard doubles as clock-skew protection since a future ReadingTime gives a
            // negative elapsed).
            var sinceRead = snap?.ReadingTime is { } readAt ? (now - readAt).TotalHours : 0;
            var fill = sinceRead >= 6 && rate > 0 ? rate * (sinceRead / 24.0) : 0;

            f.MonthSpent = auth.Value + fill;
            f.MonthSpentEstimated = fill > 0;

            // Project from the READ, not from midnight tonight: the read time is the
            // authoritative figure's actual cutoff, so this is the one branch where the
            // remaining span can be measured rather than guessed.
            if (rate > 0)
            {
                var monthEnd = new DateTime(now.Year, now.Month, 1).AddMonths(1);
                var from = snap?.ReadingTime is { } r2 && r2 < monthEnd ? r2 : now;
                var remainingDays = Math.Max(0, (monthEnd - from).TotalDays);
                f.MonthEndEstimate = auth.Value + rate * remainingDays;
            }
        }
        else if (rate > 0 || coveredSum > 0)
        {
            int gap = Math.Max(0, elapsed - coveredDays);        // elapsed days with no data
            f.MonthSpent = coveredSum + rate * gap;              // today counts at its partial actual — correct for "so far"
            f.MonthSpentEstimated = gap > 0 && rate > 0;

            // The PROJECTION is a different question, and today is a running day, not a
            // finished one. Counting it as covered meant the rest of today was never
            // projected, leaving month-end short by up to a full day's spend. Project
            // every day that isn't complete — today included — at the daily rate.
            if (rate > 0)
            {
                var todayKey = now.ToString("yyyy-MM-dd");
                bool todayCovered = monthRows.Any(d => d.Date == todayKey);
                double todaySum = monthRows.Where(d => d.Date == todayKey).Sum(d => d.Taka);
                int doneDays = coveredDays - (todayCovered ? 1 : 0);
                f.MonthEndEstimate = (coveredSum - todaySum) + rate * (daysInMonth - doneDays);
            }
        }
    }

    /// <summary>
    /// The daily series for charting: <see cref="Series"/> with the incomplete newest days
    /// removed — today (a running figure) and, for utility-fed feeds only, any run of
    /// trailing ৳0 days a once-daily or lagging feed hasn't posted yet (whose date is often
    /// "yesterday", which is why the old today-only check left a stunted ৳0 stub on the
    /// right). For NESCO — where the series is built from readings, so a DayRow only exists
    /// when two readings bracketed the interval — a trailing ৳0 is a CONFIRMED empty-house
    /// day and stays; the bar chart then honestly shows a day the user didn't consume, at
    /// the same MinBarH as any other near-zero real day.
    /// </summary>
    public List<DayRow> ChartDays() => Completed(Series, dropTrailingZeros: FromUtility);

    /// <summary>
    /// Months safe to chart and compare — everything EXCEPT the one still running.
    /// The current month is a month-to-date total, so it is not the same kind of number
    /// as the months beside it: as a bar it draws short and reads as a collapse in usage,
    /// as "latest" it reports half a month, and it drags the average down and can even win
    /// a "highest" comparison it never entered fairly. Month-to-date already has its own
    /// card (Spent this month / Est. spent this month); it does not belong in the history.
    /// </summary>
    public List<MonthRow> CompleteMonths()
    {
        var thisMonth = DateTime.Now.ToString("yyyy-MM");
        return Months
            .Where(m => !m.Estimated && string.CompareOrdinal(m.Month, thisMonth) < 0)
            .ToList();
    }

    /// <summary>
    /// The days we can treat as COMPLETE: drop today (always mid-day), then — when the
    /// caller says the trailing zeros might be "not reported yet" (DESCO) rather than
    /// "confirmed empty-house day" (NESCO) — drop any trailing ৳0 run.
    ///
    /// A ৳0 in the MIDDLE is always kept: for utility feeds it's a real quiet day the feed
    /// did post, and for readings-derived series it's likewise bracketed by observations.
    /// </summary>
    static List<DayRow> Completed(List<DayRow> series, bool dropTrailingZeros = true)
    {
        var todayKey = DateTime.Now.ToString("yyyy-MM-dd");
        var list = series.Where(d => d.Date != todayKey).ToList();
        if (dropTrailingZeros)
            while (list.Count > 0 && list[^1].Taka <= 0) list.RemoveAt(list.Count - 1);
        return list;
    }

    /// <summary>Balance is only as fresh as the utility's last meter read.</summary>
    public static string Freshness(Snapshot? snap)
    {
        var t = snap?.ReadingTime ?? snap?.SyncedAt;
        if (t == null) return "";
        var mins = (DateTime.Now - t.Value).TotalMinutes;
        if (mins < 2) return L.S("justNow");
        if (mins < 60) return L.S("minAgo", L.N($"{mins:0}"));
        if (mins < 60 * 48) return L.S("hrAgo", L.N($"{mins / 60:0}"));
        return t.Value.ToString("d MMM");
    }

    /// <summary>
    /// A live estimate of the balance "right now", between NESCO's once-a-day meter
    /// reads. Answers the question the big number can't when the portal is 12–36 hours
    /// behind wall-clock: "roughly, what have I got at this moment?"
    ///
    /// The walk-forward is deliberately simple and shows its work:
    ///     est = snap.Balance
    ///         − rate × (hours since reading / 24)
    ///         + successful recharges dated AFTER the reading
    ///
    /// The RATE isn't just the EWMA — assuming today looks like your long-run
    /// average when yesterday was actually quiet will confidently deduct spending
    /// that never happened. Look at the last 3 completed days as a pattern:
    ///
    ///   • 2 or 3 quiet → vacation regime; return null and hide the chip. The
    ///     measured figure IS the answer.
    ///   • exactly 1 quiet → one-off outlier; use the plain EWMA.
    ///   • 0 quiet → normal life; blend 70% yesterday + 30% EWMA.
    ///
    /// Return value semantics — the sign of the estimate carries real meaning and
    /// is NEVER filtered out:
    ///   • positive → normal or recovered balance ("you've got ~৳X right now")
    ///   • negative → you're either on emergency credit already, or your day's
    ///     consumption has pushed you over the edge since the midnight read.
    ///     Callers colour the chip red to warn (BalanceLine).
    ///
    /// The old rule "return null when est &lt;= 0" hid the most important information
    /// exactly when the user most needed it: an emergency-credit user with balance
    /// −৳50 burning another ৳25 over the day would see only the midnight −৳50, with
    /// no signal that they were now at −৳75. Now the chip shows −৳75 in red so
    /// the user can tell they're getting closer to the meter's cut-off.
    ///
    /// Two things that must NEVER happen and don't:
    ///   1. The estimate never touches <see cref="Store.AddReading"/>. It exists only
    ///      in the render pass, so it can't poison the EWMA the next day.
    ///   2. It never drives <see cref="Notifier.CheckLowBalance"/>. Alerts fire off
    ///      real numbers, not our guess.
    ///
    /// Returns null (→ hide the chip) only when we can't stand behind a projection:
    ///   • no balance was reported by the portal (DESCO unified meters);
    ///   • no daily rate yet (first sync, monthly-only fallback with 0 rate);
    ///   • no ReadingTime (utility didn't publish one);
    ///   • the reading is under ~6 hours old — projecting less than that is inside
    ///     the daily noise and the measured figure is already the answer;
    ///   • the 3-day pattern says we're in a vacation regime.
    ///
    /// Recharges after the reading date are added back because a recharge dated D
    /// happens DURING day D — hours after that day's midnight read — so it is NOT
    /// in the balance dated D; it first shows up in the balance dated D+1. See the
    /// matching window in <see cref="Sync.Record"/>. Without this, a user who tops
    /// up in the morning sees the estimate keep sliding as if the money never arrived.
    /// </summary>
    public static double? LiveEstimate(Snapshot? snap, Forecast? f, string meterId = "")
    {
        if (snap is null or { BalanceReported: false }) return null;
        if (f?.DailyAvg is not (> 0)) return null;
        if (snap.ReadingTime is not { } readAt) return null;

        var elapsedHours = (DateTime.Now - readAt).TotalHours;
        // Under ~6 hours the measured figure IS the fresh answer. Also catches clock
        // skew (future ReadingTime → negative elapsed → still < 6).
        if (elapsedHours < 6) return null;

        var ewma = f.DailyAvg.Value;
        var rate = ewma;
        var quietBar = ewma * 0.2;

        // Prior completed days in the raw Series — NOT the Completed() view, because we
        // want to see genuine zero days when they've happened. A day counts as "quiet"
        // when its spend is below 20% of the long-run EWMA.
        var todayKey = DateTime.Now.ToString("yyyy-MM-dd");
        var priorDays = f.Series
            .Where(d => string.Compare(d.Date, todayKey, StringComparison.Ordinal) < 0)
            .ToList();

        if (priorDays.Count >= 3)
        {
            var lastThree = priorDays.TakeLast(3).ToList();
            var quietCount = lastThree.Count(d => d.Taka < quietBar);

            if (quietCount >= 2) return null;                     // vacation regime
            if (quietCount == 1) rate = ewma;                     // one-off; long-run rate
            else rate = 0.7 * priorDays[^1].Taka + 0.3 * ewma;    // fully normal; blend toward yesterday
        }
        else if (priorDays.Count > 0)
        {
            var recent = priorDays[^1].Taka;
            if (recent < quietBar) return null;
            rate = 0.7 * recent + 0.3 * ewma;
        }

        var readDate = readAt.ToString("yyyy-MM-dd");
        var pending = snap.Recharges
            .Where(r => r.Success && string.Compare(r.Date, readDate, StringComparison.Ordinal) >= 0)
            .Sum(r => r.EnergyAmount > 0 ? r.EnergyAmount : r.Amount);

        // ...and the FAILED ones dated after the read, on trust. A portal "failed" usually
        // means the handshake timed out, not that the money stayed in the wallet: both
        // utilities mark a transaction failed when their own side times out, and a token
        // has generally already been minted. The estimate is the one number in this app
        // allowed to be optimistic — it is explicitly a projection, and the chip is
        // marked provisional while it carries money we can't yet stand behind.
        //
        // Self-expiring, and that is the whole safety argument: a recharge dated D sits
        // in this window only until the read dated D+1 lands, at which point it falls
        // BEHIND readDate and drops out whether it landed or not. The measured balance is
        // the answer from then on. See Reconcile.UnverifiedCredit, which computes the
        // same set for the UI, and Reconcile.Run, which settles it.
        if (meterId.Length > 0) pending += Reconcile.UnverifiedCredit(meterId, snap);

        // Return regardless of sign — a negative estimate is meaningful information
        // (emergency credit deepening), not noise to be filtered. The renderer
        // colours the chip by sign.
        return snap.Balance - rate * (elapsedHours / 24.0) + pending;
    }

    public static DateTime Date(string ymd) =>
        DateTime.ParseExact(ymd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
