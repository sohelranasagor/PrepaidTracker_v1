using System.Globalization;

namespace PrepaidTracker;

/// <summary>
/// The usage model. Daily spend comes from the utility when it publishes one,
/// otherwise it's derived from balance readings we take ourselves. On top of that: an
/// exponentially-weighted moving average, so recent days count more and a heat wave
/// shows up within a day or two instead of being diluted by last month's mild weather.
///
/// Note on the daily feed: DESCO's rebuilt portal (Nov 2026) exposes no daily endpoint
/// we have located, so BOTH providers now normally land on the derived path. Nothing
/// here changed for that — the fallback was always built to carry it — but it does mean
/// FromUtility is false far more often than the comments below were written to expect.
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
    public double? DailyAvg;          // ৳/day — the model's estimate (EWMA over active days)
    public double? SimpleAvg;         // ৳/day — flat 7-day mean (includes zeros; internal only)
    public double? ActiveAvg;         // ৳/day — flat mean over days that ACTUALLY consumed
    public double? WeeklyCost;
    public double? DaysLeft;
    public DateTime? RunsOut;
    public double? UnitsPerDay;
    public double? SuggestedRecharge; // covers Store.TargetDays, charges included, ৳50 steps

    /// <summary>
    /// Today's spend — NULL when nothing about today was actually measured. It used to be
    /// a plain double, which meant "unknown" and "৳0 spent" were the same value, and the
    /// monthly-fallback path filled it with the flat daily rate. The UI then labelled a
    /// month-average as "আজকের খরচ", and the Insights observation compared Today against
    /// an average that WAS Today — so "spending below average" was printed every day.
    /// </summary>
    public double? Today;

    /// <summary>
    /// The most recent COMPLETED day's spend, or null when no daily data exists at all.
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
    public List<MonthRow> Months = new();// monthly spend, for the chart

    const double Alpha = 0.35;
    const double Vat = 0.05;

    /// <summary>
    /// The fraction of a payment that reaches the meter as energy, once per-unit charges
    /// and VAT are taken out. Derived, not guessed: across 24 captured DESCO recharges
    ///
    ///     energyAmount = totalAmount × 0.95712 − demandCharge − meterRent
    ///
    /// holds exactly — ৳600 → ৳574.27, ৳500 → ৳478.56, ৳1000 → ৳957.12. Used only when
    /// the demand charge can be predicted; otherwise <see cref="CreditRatio"/>'s observed
    /// median still rules.
    /// </summary>
    const double EnergyFactor = 0.95712;

    /// <summary>
    /// Demand charge per kW of sanctioned load, levied on the FIRST recharge of each
    /// calendar month. Inferred from one 4 kW meter where ৳168 appeared on exactly the
    /// first recharge of Jun, Jul, Aug, Sep, Oct and Nov: 168 / 4 = 42.
    ///
    /// ONE meter is not a tariff schedule. If a 3 kW or 5 kW connection reports something
    /// other than load × 42, this constant is wrong and the fallback path below is the
    /// safer answer — which is why an unknown load deliberately does NOT get a guessed
    /// charge. Under-suggesting here means the user buys less energy than the app
    /// promised, which is the failure mode that actually costs them power.
    /// </summary>
    const double TakaPerKw = 42.0;

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
                // reading's date onward, not the later one.
                for (int d = 1; d <= days; d++)
                    f.Series.Add(new DayRow { Date = Date(sorted[i - 1].Date).AddDays(d - 1).ToString("yyyy-MM-dd"), Taka = spent / days });
            }

            // A derived series has to actually say something. A meter read once a day
            // means two syncs either side of that read see the SAME balance: spent = 0,
            // and we bank a single ৳0.00 day. That one row is not zero rows, so the
            // monthly fallback below was skipped, ewma came out 0, and every forecast
            // field went null — a meter with a year of monthly history rendered as ৳0
            // spent, ৳0 a day, no bill estimate and no days-left pill.
            if (f.Series.Count < 2 || f.Series.Sum(d => d.Taka) <= 0)
                f.Series.Clear();
        }

        // ---- no daily series? The monthly total still tells us something. ----
        if (f.Series.Count == 0)
        {
            f.Months = snap?.Months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList() ?? new();

            var thisMonth = DateTime.Now.ToString("yyyy-MM");
            var elapsed = DateTime.Now.Day;

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
                // working from a monthly total alone.
                f.ActiveAvg = rate;
                f.WeeklyCost = rate * 7;
                // Today stays NULL. A monthly total divided by elapsed days says nothing
                // about today in particular.
                f.Confidence = 0.35;
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

                f.MonthRecharged = MonthPaid(snap, thisMonth);
            }
            return f;
        }

        f.Months = snap?.Months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList() ?? new();

        // The rate must come from COMPLETE days only. Two kinds of incomplete day were
        // dragging it toward zero: today (a running, mid-day figure), and the newest day
        // a once-daily meter or a lagging feed hasn't posted yet, which sits at ৳0 but is
        // dated *yesterday*. Completed() drops today and, for utility-fed series only,
        // any trailing ৳0 run. For a readings-derived series a trailing ৳0 is a confirmed
        // empty-house day and stays in.
        var todayKey = DateTime.Now.ToString("yyyy-MM-dd");
        var rateDays = Completed(f.Series, dropTrailingZeros: f.FromUtility);
        if (rateDays.Count == 0) rateDays = f.Series;   // nothing complete yet: a partial rate beats none

        // ---- window stats (last 7 completed days) ----
        var week = rateDays.TakeLast(7).ToList();
        f.WindowTotal = week.Sum(d => d.Taka);
        f.SimpleAvg = week.Average(d => d.Taka);
        // Active-day average: same window, only days that actually consumed. This is the
        // "typical use" number people expect when they see "Daily average".
        var activeWeek = week.Where(d => d.Taka > 0).ToList();
        f.ActiveAvg = activeWeek.Count > 0 ? activeWeek.Average(d => d.Taka) : (double?)null;
        var heaviest = week.OrderByDescending(d => d.Taka).First();
        f.MaxDay = heaviest.Taka;
        f.MaxDayDate = heaviest.Date;

        // Only a row actually dated today counts.
        f.Today = f.Series.LastOrDefault(d => d.Date == todayKey)?.Taka;

        // The newest day we actually hold a complete figure for.
        f.LastFullDay = rateDays.Count > 0 ? rateDays[^1].Taka : null;

        // ---- EWMA over ACTIVE completed days, oldest → newest ----
        // A day the user genuinely consumed nothing is signal for the LiveEstimate
        // anomaly guard, but noise for the long-run rate: leaving it in drags the
        // forecast down so a returning user sees "5 days" when their pattern points to
        // 3 — and the days-left pill then contradicts the "Daily average" on the same
        // card. Both figures share one active-day world.
        var activeDays = rateDays.Where(d => d.Taka > 0).ToList();
        double ewma = activeDays.Count > 0 ? activeDays[0].Taka : 0;
        foreach (var d in activeDays.Skip(1)) ewma = Alpha * d.Taka + (1 - Alpha) * ewma;

        // Weekday shape: only with 3+ weeks of ACTIVE data, clamped so one freak day
        // can't skew it.
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
                f.DaysLeft = Math.Max(0, f.Balance / ewma);   // never negative
                f.RunsOut = DateTime.Now.AddDays(f.DaysLeft.Value);
            }
            var needed = ewma * Store.TargetDays - (balKnown ? f.Balance : 0);
            f.SuggestedRecharge = Suggest(needed, snap);
        }

        if (Store.Tariff is > 0) f.UnitsPerDay = ewma / Store.Tariff.Value;

        // ---- month ----
        var month = DateTime.Now.ToString("yyyy-MM");
        FillMonth(f, ewma, snap);
        f.MonthRecharged = snap is { Recharges.Count: > 0 }
            ? MonthPaid(snap, month)
            : readings.Where(r => r.Date.StartsWith(month)).Sum(r => r.Recharge);

        // ---- "you're spending X% less than usual" ----
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
    /// What the customer actually paid this month. Multi-token rows are excluded: a
    /// meter key-change arrives as one transaction carrying a set of tokens, and its
    /// amount is not a recharge the user chose to make — counting it would make
    /// "recharged this month" disagree with the user's own bank app.
    /// </summary>
    static double MonthPaid(Snapshot? snap, string month) =>
        snap?.Recharges
            .Where(r => r.Date.StartsWith(month) && r.Success && !r.MultiToken)
            .Sum(r => r.Amount) ?? 0;

    /// <summary>
    /// What fraction of a payment actually reaches the meter as energy.
    ///
    /// The old code assumed exactly (1 − VAT). Both portals report what each recharge
    /// really credited, and the observed figure is not 5%: a ৳990 recharge credits
    /// ৳947.55 and a ৳500 credits ৳478.56 — 4.288%, every time.
    ///
    /// MEDIAN of recent recharges, not the mean: a recharge that also carried a demand
    /// charge credits far less (৳990 → ৳781.35 in one observed case, a 21% cut) and
    /// would drag an average into suggesting more than the customer needs.
    ///
    /// Now that DESCO itemises demandCharge, those rows can be UNDISTORTED rather than
    /// merely outvoted: adding the charge back recovers the true per-taka energy rate,
    /// so a demand-charge month contributes a clean sample instead of a misleading one.
    /// That is why the ratio here is (energy + demand) / amount. Rows from NESCO, which
    /// itemises nothing, have DemandCharge == 0 and behave exactly as before.
    /// </summary>
    internal static double CreditRatio(Snapshot? snap)
    {
        var ratios = snap?.Recharges
            .Where(r => r.Success && !r.MultiToken && r.Amount > 0 && r.EnergyAmount > 0
                        && r.EnergyAmount + r.DemandCharge <= r.Amount)
            .OrderByDescending(r => r.Date, StringComparer.Ordinal)
            .Take(6)
            .Select(r => (r.EnergyAmount + r.DemandCharge) / r.Amount)
            .OrderBy(x => x)
            .ToList();

        if (ratios is not { Count: > 0 }) return 1 - Vat;

        var median = ratios[ratios.Count / 2];
        return median is > 0.5 and <= 1 ? median : 1 - Vat;
    }

    /// <summary>
    /// True when no successful recharge has been made yet this calendar month — i.e. the
    /// next one will carry the demand charge. Multi-token rows don't count: a key-change
    /// is not the month's first recharge in the billing sense.
    /// </summary>
    static bool FirstRechargeOfMonth(Snapshot? snap)
    {
        var month = DateTime.Now.ToString("yyyy-MM");
        return snap?.Recharges.Any(r => r.Date.StartsWith(month) && r.Success && !r.MultiToken) != true;
    }

    /// <summary>
    /// The demand charge the next recharge will carry, or 0 when it can't be predicted.
    ///
    /// Zero whenever the sanction load is unknown (always, for NESCO). That is
    /// deliberate: a guessed charge that is too high makes the suggestion overshoot,
    /// which merely costs the user some float — but a guessed charge where there is
    /// none makes it overshoot for twelve months of the year. The observed credit ratio
    /// already handles the unknown case tolerably, so there is nothing to gain by
    /// inventing a figure.
    /// </summary>
    static double DemandCharge(Snapshot? snap)
    {
        if (snap is not { SanctionLoadKw: > 0 }) return 0;
        return FirstRechargeOfMonth(snap) ? snap.SanctionLoadKw * TakaPerKw : 0;
    }

    /// <summary>
    /// What to pay to buy this much credit: grossed up for charges, rounded to a ৳50
    /// step, and never below the minimum the utility publishes — suggesting ৳150 where
    /// the utility will not accept less than ৳500 is advice the customer cannot act on.
    ///
    /// Two paths. When the sanction load is known, the arithmetic is exact and inverts
    /// the portal's own identity:
    ///
    ///     energy = amount × 0.95712 − demand    ⇒    amount = (energy + demand) / 0.95712
    ///
    /// which is what makes the first recharge of a month come out right instead of
    /// ৳168 short. When it isn't known, fall back to the observed median ratio, which
    /// is what this did before and still does for NESCO.
    /// </summary>
    static double? Suggest(double energyNeeded, Snapshot? snap)
    {
        if (energyNeeded <= 0) return null;

        var demand = DemandCharge(snap);
        var gross = snap is { SanctionLoadKw: > 0 }
            ? (energyNeeded + demand) / EnergyFactor
            : energyNeeded / CreditRatio(snap);

        gross = Math.Ceiling(gross / 50) * 50;

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

        // 1) authoritative month-to-date for THIS month, if the utility gives one.
        double? auth = null;
        if (snap?.CurrentMonthConsumption > 0)
            auth = snap.CurrentMonthConsumption;
        else
        {
            var row = snap?.Months.FirstOrDefault(m => m.Month == month);
            if (row is { Taka: > 0 }) auth = row.Taka;
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
            // Fill the elapsed time at the daily rate: the same walk-forward LiveEstimate
            // performs on the balance, with the same 6-hour freshness threshold (under
            // that, the measured figure IS the answer, and the guard doubles as
            // clock-skew protection since a future ReadingTime gives a negative elapsed).
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
            f.MonthSpent = coveredSum + rate * gap;
            f.MonthSpentEstimated = gap > 0 && rate > 0;

            // The PROJECTION is a different question, and today is a running day, not a
            // finished one. Counting it as covered left month-end short by up to a full
            // day's spend. Project every day that isn't complete — today included.
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
    /// removed.
    /// </summary>
    public List<DayRow> ChartDays() => Completed(Series, dropTrailingZeros: FromUtility);

    /// <summary>
    /// Months safe to chart and compare — everything EXCEPT the one still running.
    /// The current month is a month-to-date total, so it is not the same kind of number
    /// as the months beside it: as a bar it draws short and reads as a collapse in usage,
    /// as "latest" it reports half a month, and it drags the average down.
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
    /// caller says the trailing zeros might be "not reported yet" rather than "confirmed
    /// empty-house day" — drop any trailing ৳0 run. A ৳0 in the MIDDLE is always kept.
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
    /// A live estimate of the balance "right now", between once-a-day meter reads.
    /// Answers the question the big number can't when the portal is 12–36 hours behind
    /// wall-clock: "roughly, what have I got at this moment?"
    ///
    ///     est = snap.Balance
    ///         − rate × (hours since reading / 24)
    ///         + recharges dated AFTER the reading
    ///
    /// The RATE isn't just the EWMA — assuming today looks like your long-run average
    /// when yesterday was actually quiet will confidently deduct spending that never
    /// happened. Look at the last 3 completed days as a pattern:
    ///
    ///   • 2 or 3 quiet → vacation regime; return null and hide the chip.
    ///   • exactly 1 quiet → one-off outlier; use the plain EWMA.
    ///   • 0 quiet → normal life; blend 70% yesterday + 30% EWMA.
    ///
    /// The SIGN carries real meaning and is never filtered out. The old rule "return null
    /// when est &lt;= 0" hid the most important information exactly when the user most
    /// needed it: someone on emergency credit at −৳50 burning another ৳25 would see only
    /// the midnight −৳50, with no signal they were now at −৳75 and closer to cut-off.
    ///
    /// Two things that must NEVER happen and don't: it never touches
    /// <see cref="Store.AddReading"/>, and it never drives
    /// <see cref="Notifier.CheckLowBalance"/>. Alerts fire off measurements.
    ///
    /// This matters MORE under DESCO's new portal, not less: live reads are rationed to
    /// roughly one a day, so the measured balance is now stale by design for most of the
    /// day and this chip is the only thing standing between the user and a number from
    /// this morning.
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
        // want to see genuine zero days when they've happened.
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
            else rate = 0.7 * priorDays[^1].Taka + 0.3 * ewma;    // normal; blend toward yesterday
        }
        else if (priorDays.Count > 0)
        {
            var recent = priorDays[^1].Taka;
            if (recent < quietBar) return null;
            rate = 0.7 * recent + 0.3 * ewma;
        }

        // A recharge dated D happens DURING day D — hours after that day's midnight read
        // — so it is NOT in the balance dated D; it first appears in the balance dated
        // D+1. Without adding it back, a user who tops up in the morning watches the
        // estimate keep sliding as if the money never arrived. Same window as Sync.Record.
        //
        // Key-change sets are excluded: their amount is not a credit landing on the
        // meter, and adding it would make the chip hundreds of taka optimistic.
        var readDate = readAt.ToString("yyyy-MM-dd");
        var pending = snap.Recharges
            .Where(r => r.Success && !r.MultiToken
                     && string.Compare(r.Date, readDate, StringComparison.Ordinal) >= 0)
            .Sum(r => r.EnergyAmount > 0 ? r.EnergyAmount : r.Amount);

        // ...and the FAILED ones dated after the read, on trust. A portal "failed"
        // usually means the handshake timed out, not that the money stayed in the wallet.
        // The estimate is the one number allowed to be optimistic — it is explicitly a
        // projection, and the chip is marked provisional while it carries money we can't
        // yet stand behind.
        //
        // Self-expiring, which is the whole safety argument: a recharge dated D sits in
        // this window only until the read dated D+1 lands, at which point it falls BEHIND
        // readDate and drops out whether it landed or not.
        if (meterId.Length > 0) pending += Reconcile.UnverifiedCredit(meterId, snap);

        // Return regardless of sign — a negative estimate is meaningful information
        // (emergency credit deepening), not noise. The renderer colours the chip by sign.
        return snap.Balance - rate * (elapsedHours / 24.0) + pending;
    }

    public static DateTime Date(string ymd) =>
        DateTime.ParseExact(ymd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}