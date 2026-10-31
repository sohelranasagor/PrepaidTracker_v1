namespace PrepaidTracker;

/// <summary>
/// What we believe happened to a recharge the portal called FAILED.
///
/// The portal's own word is not the last word. Both NESCO and DESCO mark a
/// transaction failed when their side of the handshake times out — but the money has
/// usually already left the wallet and a token has usually already been minted. Three
/// things can be true after a "failed" row appears:
///
///   • the token was generated AND auto-applied — the portal simply lied;
///   • the token was generated but never reached the meter — it is sitting in the
///     transaction details waiting to be typed into the keypad;
///   • nothing happened at all and the money will be reversed.
///
/// From the app's side the second and third look identical, and both are answered the
/// same way: tell the user to enter the token by hand. Only the meter can settle which
/// of the three it was, and it only speaks once a day.
/// </summary>
public enum CreditState
{
    /// <summary>
    /// No meter read dated after the recharge exists yet, so the question is not
    /// answerable. This is NOT "it failed" — showing the manual-token prompt here
    /// would nag every user on every failed row for up to 24 hours before we have
    /// any evidence at all.
    /// </summary>
    Unverified,

    /// <summary>The next read rose the way a landed recharge makes it rise.</summary>
    Credited,

    /// <summary>The next read did not. The token never reached the meter.</summary>
    NotCredited
}

public class Verdict
{
    public CreditState State { get; set; }

    /// <summary>The reading date that decided it. Empty while Unverified.</summary>
    public string DecidedOn { get; set; } = "";

    /// <summary>Energy the recharge would have credited — kept so the UI can name a figure.</summary>
    public double Energy { get; set; }

    /// <summary>The balance move actually observed, once burn was accounted for.</summary>
    public double Observed { get; set; }

    /// <summary>The user pressed "I've entered it" — stop showing the banner.</summary>
    public bool Dismissed { get; set; }

    /// <summary>A NotCredited notification has already fired for this row.</summary>
    public bool Notified { get; set; }
}

/// <summary>
/// Reconciles failed recharges against the next meter read.
///
/// The rule this whole file exists to implement:
///
///   1. A failed recharge is credited to the LIVE ESTIMATE immediately, exactly like a
///      successful one, because the odds are good the money arrived. The estimate is
///      the only number in the app allowed to be optimistic — it is explicitly a
///      projection, and it is marked as provisional while it carries unverified money.
///   2. It never touches <see cref="Store.AddReading"/>, the EWMA, or the alerts on the
///      way in. Those run on measured figures only.
///   3. The very next meter read settles it, and the verdict is written down.
///
/// The verdict has to be PERSISTED rather than recomputed, because both portals return
/// a rolling window of recharges. The failed row that is visible today can be gone
/// tomorrow, and the reading it was compared against will eventually roll out of the
/// log too — recomputing from whatever happens to be in the current snapshot would let
/// a settled row flip back to Unverified, re-fire its notification, and re-raise its
/// banner. Decided once, never re-litigated.
/// </summary>
public static class Reconcile
{
    /// <summary>
    /// Identity of a recharge across syncs. Not an index and not the object — the
    /// snapshot is rebuilt from scratch on every fetch, so the only stable handle is
    /// the content the portal reports for the row.
    /// </summary>
    public static string KeyOf(RechargeRow r) => $"{r.Date}|{r.Time}|{r.Amount:0.##}|{r.Via}";

    /// <summary>
    /// Electricity this recharge would put on the meter. The portal usually reports it
    /// outright (DESCO reports it on failed rows too — a would-have-been figure); when
    /// it doesn't, gross it down by the same observed credit ratio the suggestion engine
    /// uses, rather than the VAT constant.
    /// </summary>
    public static double Energy(RechargeRow r, Snapshot? snap) =>
        r.EnergyAmount > 0 ? r.EnergyAmount : r.Amount * Forecast.CreditRatio(snap);

    public static CreditState StateOf(string meterId, RechargeRow r)
    {
        if (r.Success) return CreditState.Credited;
        return Store.Verdicts(meterId).TryGetValue(KeyOf(r), out var v) ? v.State : CreditState.Unverified;
    }

    /// <summary>
    /// A recharge the portal called failed but the meter proved landed. <see cref="Sync.Record"/>
    /// has to count these as money credited or the day's usage comes out negative and
    /// <see cref="Forecast"/> silently drops the whole day.
    /// </summary>
    public static bool IsCredited(string meterId, RechargeRow r) =>
        !r.Success && StateOf(meterId, r) == CreditState.Credited;

    // ---------------------------------------------------------------- the estimate

    /// <summary>
    /// Energy from failed recharges that is currently riding on the live estimate on
    /// trust. Added by <see cref="Forecast.LiveEstimate"/>; also drives the amber dot on
    /// the chip, because a number that might be ৳478 wrong should not look as settled as
    /// one that can't be.
    ///
    /// The window is the same one the success path uses — recharges dated at or after the
    /// meter's last read — and that window is what makes the optimism self-expiring. A
    /// recharge dated D sits in the estimate until the read dated D+1 lands; from that
    /// moment the recharge is BEHIND the read date, so it drops out of this sum whether
    /// it landed or not, and the measured balance is the answer either way. The verdict
    /// check below is belt-and-braces for a portal that republishes an older read time.
    /// </summary>
    public static double UnverifiedCredit(string meterId, Snapshot? snap)
    {
        if (snap?.ReadingTime is not { } readAt) return 0;

        var readDate = readAt.ToString("yyyy-MM-dd");
        var verdicts = Store.Verdicts(meterId);

        return snap.Recharges
            .Where(r => !r.Success
                     && string.CompareOrdinal(r.Date, readDate) >= 0
                     && (!verdicts.TryGetValue(KeyOf(r), out var v) || v.State != CreditState.NotCredited))
            .Sum(r => Energy(r, snap));
    }

    // ---------------------------------------------------------------- the banner

    /// <summary>Rows the meter has disproved and the user hasn't dealt with yet, newest first.</summary>
    public static List<RechargeRow> NeedsToken(string meterId, Snapshot? snap)
    {
        if (snap == null) return new();
        var verdicts = Store.Verdicts(meterId);

        return snap.Recharges
            .Where(r => !r.Success
                     && verdicts.TryGetValue(KeyOf(r), out var v)
                     && v.State == CreditState.NotCredited
                     && !v.Dismissed)
            .OrderByDescending(r => r.Date, StringComparer.Ordinal)
            .ThenByDescending(r => r.Time, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The STS token out of the portal's own detail fields. "Enter the token manually" is
    /// useless advice on its own — the whole point is that the app already holds the
    /// twenty digits and can put them on screen.
    /// </summary>
    public static string? TokenOf(RechargeRow r)
    {
        foreach (var kv in r.Details)
        {
            if (kv.Key.IndexOf("token", StringComparison.OrdinalIgnoreCase) < 0 && !kv.Key.Contains("টোকেন"))
                continue;
            var digits = new string(kv.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 16 and <= 24) return digits;
        }
        return null;
    }

    /// <summary>Twenty digits in groups of four — nobody can key a 20-digit run without it.</summary>
    public static string Group(string digits) =>
        string.Join("-", Enumerable.Range(0, (digits.Length + 3) / 4)
            .Select(i => digits.Substring(i * 4, Math.Min(4, digits.Length - i * 4))));

    public static void Dismiss(string meterId, RechargeRow r)
    {
        var verdicts = Store.Verdicts(meterId);
        if (!verdicts.TryGetValue(KeyOf(r), out var v)) return;
        v.Dismissed = true;
        Store.SaveVerdicts(meterId, verdicts);
    }

    /// <summary>
    /// Cheap fingerprint of this meter's verdicts, for the pages' StateToken. Without it
    /// a sync that flips a row to NotCredited writes the banner to disk and the open page
    /// never redraws — the user sees it only after switching tabs.
    /// </summary>
    public static string Stamp(string meterId)
    {
        var v = Store.Verdicts(meterId).Values;
        return $"{v.Count(x => x.State == CreditState.NotCredited && !x.Dismissed)}/{v.Count(x => x.State == CreditState.Unverified)}";
    }

    // ---------------------------------------------------------------- the decision

    /// <summary>
    /// Settle every failed recharge that a meter read has now overtaken.
    ///
    /// Call AFTER <see cref="Sync.Record"/> has banked the new reading and after the
    /// forecast is built (the burn rate is an input). Returns true when at least one row
    /// flipped to Credited, which means the caller must re-run Record: that recharge is
    /// now money the meter received, and the reading's `spent` figure was computed
    /// without it.
    /// </summary>
    public static bool Run(Meter meter, Snapshot snap, double rate)
    {
        if (snap.Recharges.Count == 0) return false;

        var readings = Store.Readings(meter.Id);
        if (readings.Count == 0) return false;

        var verdicts = Store.Verdicts(meter.Id);
        bool anyCredited = false, changed = false;

        foreach (var r in snap.Recharges.Where(x => !x.Success)
                                        .OrderBy(x => x.Date, StringComparer.Ordinal))
        {
            var key = KeyOf(r);

            // Settled rows are never revisited. See the class comment: the evidence that
            // produced the verdict rolls out of the window long before the row does.
            if (verdicts.TryGetValue(key, out var existing) && existing.State != CreditState.Unverified)
                continue;

            // Balances are midnight snapshots, so a recharge dated D happens DURING day D —
            // hours after that day's read — and cannot appear before the read dated D+1.
            // `prior` is therefore the last read at or before D (pre-recharge by
            // construction) and `after` the first read strictly past it. Same window rule
            // as Sync.Record, and it has to stay the same or the two disagree about which
            // interval owns the money.
            var prior = readings.LastOrDefault(x => string.CompareOrdinal(x.Date, r.Date) <= 0);
            var after = readings.FirstOrDefault(x => string.CompareOrdinal(x.Date, r.Date) > 0);

            if (prior == null || after == null)
            {
                // Not knowable yet — either the recharge predates our whole log, or the
                // meter hasn't spoken since. Hold Unverified and try again next sync.
                // Emphatically NOT NotCredited: that would put "enter the token manually"
                // on screen minutes after a recharge, before any evidence exists.
                if (!verdicts.ContainsKey(key))
                {
                    verdicts[key] = new Verdict { State = CreditState.Unverified, Energy = Energy(r, snap) };
                    changed = true;
                }
                continue;
            }

            var energy = Energy(r, snap);
            if (energy <= 0) continue;                    // nothing to look for

            var days = Math.Max(1, (Forecast.Date(after.Date) - Forecast.Date(prior.Date)).Days);
            var burn = Math.Max(0, rate) * days;

            // Anything ELSE in the same interval that we know landed has to come out
            // first, or two recharges bracketed by one pair of readings each explain the
            // other's rise and both get marked Credited off a single credit.
            var others = snap.Recharges
                .Where(x => KeyOf(x) != key
                         && string.CompareOrdinal(x.Date, prior.Date) >= 0
                         && string.CompareOrdinal(x.Date, after.Date) < 0
                         && (x.Success
                             || (verdicts.TryGetValue(KeyOf(x), out var ov) && ov.State == CreditState.Credited)))
                .Sum(x => Energy(x, snap));

            // How much of the balance move is left unexplained once ordinary consumption
            // and known credits are subtracted. A landed recharge leaves ~`energy` here;
            // one that never arrived leaves ~0.
            var observed = after.Balance - prior.Balance + burn - others;

            // Rule 1 — the rate-aware test. Half the energy is the midpoint between the
            // two hypotheses, which is the right place to cut: it tolerates a day that
            // burned twice the average without ever mistaking burn for a ৳478 credit.
            bool landed = observed >= energy * 0.5;

            // Rule 2 — your rule, applied only where it is sound. When the recharge is
            // worth more than the entire interval's consumption, a credit that landed
            // CANNOT leave the balance at or below where it started; if it did, rule 1
            // was fooled. This is the guard against a wild `burn`: a long gap between
            // readings, or an EWMA inflated by a heat wave, inflates `burn` until rule 1
            // would say yes to almost anything. Where the recharge is genuinely smaller
            // than the interval's burn the raw comparison is meaningless and is skipped.
            if (energy > burn && after.Balance <= prior.Balance) landed = false;

            var state = landed ? CreditState.Credited : CreditState.NotCredited;
            var verdict = new Verdict
            {
                State = state,
                DecidedOn = after.Date,
                Energy = energy,
                Observed = observed,
                Dismissed = false,
                Notified = existing?.Notified ?? false
            };

            if (state == CreditState.NotCredited && !verdict.Notified)
            {
                Notifier.TokenNotApplied(meter, r, snap.CustomerName);
                verdict.Notified = true;
            }

            verdicts[key] = verdict;
            changed = true;
            if (state == CreditState.Credited) anyCredited = true;
        }

        if (changed) Store.SaveVerdicts(meter.Id, verdicts);
        return anyCredited;
    }
}
