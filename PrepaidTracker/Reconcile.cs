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
    NotCredited,

    /// <summary>
    /// Not reconcilable, deliberately.
    ///
    /// DESCO emits a meter key-change as ONE transaction carrying a SET of tokens —
    /// twelve of them, under sequence "-3~9", in the captured data. Its Amount does not
    /// correspond to a single credit landing on a single balance, so every rule in
    /// <see cref="Reconcile.Run"/> would be arithmetic on a number that doesn't mean
    /// what it looks like: the balance could rise by the full amount, by part of it, or
    /// not at all, and none of those outcomes would tell us anything.
    ///
    /// So we record it, show every token (the user has to key all of them), and make no
    /// claim about whether it landed. Silence is the honest answer here — a confident
    /// wrong verdict costs either a wasted trip to the meter or a user who thinks they
    /// have power and doesn't.
    /// </summary>
    Informational
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
///
/// DESCO's rebuilt portal changed two inputs here, both for the better: tokens now
/// arrive as a first-class field instead of buried in detail keys, and energyAmount is
/// published on every row including failed ones. It also introduced the multi-token
/// transaction — see <see cref="CreditState.Informational"/>.
/// </summary>
public static class Reconcile
{
    /// <summary>
    /// Identity of a recharge across syncs. Not an index and not the object — the
    /// snapshot is rebuilt from scratch on every fetch, so the only stable handle is
    /// the content the portal reports for the row.
    ///
    /// DESCO's new feed carries a genuinely stable orderId, which would be a better key.
    /// It is deliberately NOT used: changing the key orphans every verdict already on
    /// disk, so every settled row would revert to Unverified, re-raise its banner and
    /// re-fire its notification days or weeks after the user dealt with it. The orderId
    /// is carried on the row (<see cref="RechargeRow.OrderId"/>) as the migration target
    /// if that is ever done deliberately, with a conversion pass over the stored map.
    /// </summary>
    public static string KeyOf(RechargeRow r) => $"{r.Date}|{r.Time}|{r.Amount:0.##}|{r.Via}";

    /// <summary>
    /// Electricity this recharge would put on the meter. The portal usually reports it
    /// outright (DESCO reports it on failed rows too — a would-have-been figure); when
    /// it doesn't, gross it down by the same observed credit ratio the suggestion engine
    /// uses, rather than the VAT constant.
    ///
    /// Nothing is subtracted for the demand charge: DESCO's energyAmount already excludes
    /// it. Verified across 24 captured recharges — ৳600 with no demand charge credits
    /// ৳574.27, ৳400 with ৳168 of demand charge credits ৳214.85, and both satisfy
    /// energyAmount = totalAmount × 0.95712 − demandCharge.
    /// </summary>
    public static double Energy(RechargeRow r, Snapshot? snap) =>
        r.EnergyAmount > 0 ? r.EnergyAmount : r.Amount * Forecast.CreditRatio(snap);

    public static CreditState StateOf(string meterId, RechargeRow r)
    {
        // Checked before Success: a key-change set can be reported "Execution
        // Successful", and calling it Credited would invite the caller to treat its
        // Amount as money that landed on the meter.
        if (r.MultiToken) return CreditState.Informational;
        if (r.Success) return CreditState.Credited;
        return Store.Verdicts(meterId).TryGetValue(KeyOf(r), out var v) ? v.State : CreditState.Unverified;
    }

    /// <summary>
    /// A recharge the portal called failed but the meter proved landed. <see cref="Sync.Record"/>
    /// has to count these as money credited or the day's usage comes out negative and
    /// <see cref="Forecast"/> silently drops the whole day.
    /// </summary>
    public static bool IsCredited(string meterId, RechargeRow r) =>
        !r.Success && !r.MultiToken && StateOf(meterId, r) == CreditState.Credited;

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
    ///
    /// Key-change sets are excluded: their Amount is not a credit, and adding it to the
    /// projected balance would make the chip hundreds of taka optimistic for a full day.
    /// </summary>
    public static double UnverifiedCredit(string meterId, Snapshot? snap)
    {
        if (snap?.ReadingTime is not { } readAt) return 0;

        var readDate = readAt.ToString("yyyy-MM-dd");
        var verdicts = Store.Verdicts(meterId);

        return snap.Recharges
            .Where(r => !r.Success
                     && !r.MultiToken
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
                     && !r.MultiToken
                     && verdicts.TryGetValue(KeyOf(r), out var v)
                     && v.State == CreditState.NotCredited
                     && !v.Dismissed)
            .OrderByDescending(r => r.Date, StringComparer.Ordinal)
            .ThenByDescending(r => r.Time, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The STS token for this recharge. "Enter the token manually" is useless advice on
    /// its own — the whole point is that the app already holds the twenty digits and can
    /// put them on screen.
    ///
    /// DESCO's new feed publishes the token as its own field, so that is tried first.
    /// The detail-key scrape stays as the fallback: NESCO's HTML table still buries it
    /// among labelled cells, and upgrading installs hold old snapshots that predate the
    /// Tokens array.
    /// </summary>
    public static string? TokenOf(RechargeRow r)
    {
        if (r.Tokens.Length > 0)
        {
            var d = new string(r.Tokens[0].Where(char.IsDigit).ToArray());
            if (d.Length is >= 16 and <= 24) return d;
        }

        foreach (var kv in r.Details)
        {
            if (kv.Key.IndexOf("token", StringComparison.OrdinalIgnoreCase) < 0 && !kv.Key.Contains("টোকেন"))
                continue;
            var digits = new string(kv.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 16 and <= 24) return digits;
        }
        return null;
    }

    /// <summary>
    /// Every token on the row, each grouped for keying. A key-change set carries twelve;
    /// rendering them as one 240-digit run is unusable, and showing only the first is
    /// worse — the meter needs all of them, in order, and the user has no other copy.
    /// </summary>
    public static List<string> AllTokens(RechargeRow r)
    {
        var all = r.Tokens
            .Select(t => new string(t.Where(char.IsDigit).ToArray()))
            .Where(t => t.Length is >= 16 and <= 24)
            .Select(Group)
            .ToList();

        // Pre-Tokens snapshots, and NESCO: fall back to the detail scrape.
        if (all.Count == 0 && TokenOf(r) is { } single) all.Add(Group(single));
        return all;
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

            // A row with no usable date cannot be windowed at all: LastOrDefault against
            // an empty string matches nothing and FirstOrDefault matches the very first
            // reading ever taken, so `burn` would become the entire log's consumption and
            // rule 1 would credit almost anything. Skip rather than guess.
            if (r.Date.Length < 10) continue;

            // Settled rows are never revisited. See the class comment: the evidence that
            // produced the verdict rolls out of the window long before the row does.
            if (verdicts.TryGetValue(key, out var existing) && existing.State != CreditState.Unverified)
                continue;

            // A key-change set is RECORDED, never decided — its amount isn't one credit.
            // Written once so the UI can list its tokens and so this loop skips it on
            // every later sync via the settled-row check above.
            if (r.MultiToken)
            {
                if (existing == null)
                {
                    verdicts[key] = new Verdict
                    {
                        State = CreditState.Informational,
                        DecidedOn = snap.ReadingTime?.ToString("yyyy-MM-dd") ?? "",
                        Energy = 0,
                        Observed = 0
                    };
                    changed = true;
                }
                continue;
            }

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
                //
                // Worth knowing that this state now lasts LONGER on DESCO than it used
                // to. The portal rations live reads to about one a day, so `after` can be
                // most of a day away even when the user is actively watching — which is
                // exactly the window this state was designed to describe honestly.
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
            // Key-change sets are excluded here too: subtracting their notional amount
            // would make a genuine credit in the same interval look unexplained.
            var others = snap.Recharges
                .Where(x => KeyOf(x) != key
                         && !x.MultiToken
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

            // Rule 2 — the naive comparison, fenced to the case where it is sound. When
            // the recharge is worth more than the entire interval's consumption, a credit
            // that landed CANNOT leave the balance at or below where it started; if it
            // did, rule 1 was fooled. This is the guard against a wild `burn`: a long gap
            // between readings, or an EWMA inflated by a heat wave, inflates `burn` until
            // rule 1 would say yes to almost anything. Where the recharge is genuinely
            // smaller than the interval's burn the raw comparison is meaningless and is
            // skipped — both halves are covered by test cases and dropping either one
            // breaks a real scenario.
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