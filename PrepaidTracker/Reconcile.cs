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
/// of the three it was.
/// </summary>
public enum CreditState
{
    /// <summary>
    /// No meter read taken after the recharge exists yet, so the question is not
    /// answerable. This is NOT "it failed" — showing the manual-token prompt here
    /// would nag every user on every failed row before we have any evidence at all.
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
    /// what it looks like.
    ///
    /// So we record it, show every token (the user has to key all of them), and make no
    /// claim about whether it landed.
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
///      the only number in the app allowed to be optimistic, and it is marked as
///      provisional while it carries unverified money.
///   2. It never touches <see cref="Store.AddReading"/>, the EWMA, or the alerts on the
///      way in. Those run on measured figures only.
///   3. The very next meter read settles it, and the verdict is written down.
///
/// The verdict has to be PERSISTED rather than recomputed, because both portals return
/// a rolling window of recharges — recomputing from whatever happens to be in the
/// current snapshot would let a settled row flip back to Unverified, re-fire its
/// notification, and re-raise its banner. Decided once, never re-litigated.
///
/// "Before" and "after" a read are decided by MOMENT where both the recharge time and
/// the read time are known (<see cref="Forecast.After"/>). DESCO's live read happens at
/// any hour, so a recharge at 08:00 is already inside a 09:05 read on the same day.
/// The old date-only rule treated that read as pre-recharge, saw no rise after it, and
/// raised "enter the token manually" for money that had already landed.
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
    /// disk. The orderId is carried on the row (<see cref="RechargeRow.OrderId"/>) as the
    /// migration target if that is ever done deliberately.
    /// </summary>
    public static string KeyOf(RechargeRow r) => $"{r.Date}|{r.Time}|{r.Amount:0.##}|{r.Via}";

    /// <summary>
    /// Electricity this recharge would put on the meter. The portal usually reports it
    /// outright (DESCO reports it on failed rows too); when it doesn't, gross it down by
    /// the same observed credit ratio the suggestion engine uses.
    ///
    /// Nothing is subtracted for the demand charge: DESCO's energyAmount already excludes
    /// it (energyAmount = totalAmount × 0.95712 − demandCharge, verified across 24 rows).
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
    /// the chip.
    ///
    /// The window is the same one the success path uses — recharges made AFTER the meter's
    /// last read, by moment where the recharge has a time (<see cref="Forecast.AfterRead"/>)
    /// — and that window is what makes the optimism self-expiring: once a read is taken
    /// after the recharge, it drops out of this sum whether it landed or not, and the
    /// measured balance is the answer either way.
    ///
    /// Key-change sets are excluded: their Amount is not a credit.
    /// </summary>
    public static double UnverifiedCredit(string meterId, Snapshot? snap)
    {
        if (snap?.ReadingTime is not { } readAt) return 0;

        var verdicts = Store.Verdicts(meterId);

        return snap.Recharges
            .Where(r => !r.Success
                     && !r.MultiToken
                     && Forecast.AfterRead(r, readAt)
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
    /// The STS token for this recharge. DESCO's new feed publishes the token as its own
    /// field, so that is tried first. The detail-key scrape stays as the fallback: NESCO's
    /// HTML table still buries it among labelled cells, and upgrading installs hold old
    /// snapshots that predate the Tokens array.
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
    /// the meter needs all of them, in order, and the user has no other copy.
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
    /// never redraws.
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

            // A row with no usable date cannot be windowed at all. Skip rather than guess.
            if (r.Date.Length < 10) continue;

            // Settled rows are never revisited. See the class comment.
            if (verdicts.TryGetValue(key, out var existing) && existing.State != CreditState.Unverified)
                continue;

            // A key-change set is RECORDED, never decided — its amount isn't one credit.
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

            // `prior` is the last read taken BEFORE the recharge (so its balance does not
            // contain it), `after` the first read taken AFTER it. Decided by moment where
            // both times are known, by date otherwise. Same rule as Sync.Record — the two
            // must agree about which interval owns the money.
            var prior = readings.LastOrDefault(x => Forecast.After(r, x));
            var after = readings.FirstOrDefault(x => !Forecast.After(r, x));

            if (prior == null || after == null)
            {
                // Not knowable yet — either the recharge predates our whole log, or the
                // meter hasn't been read since. Hold Unverified and try again next sync.
                // Emphatically NOT NotCredited: that would put "enter the token manually"
                // on screen before any evidence exists.
                //
                // On DESCO this state lasts until the next LIVE read — the cached figure
                // does not move on its own.
                if (!verdicts.ContainsKey(key))
                {
                    verdicts[key] = new Verdict { State = CreditState.Unverified, Energy = Energy(r, snap) };
                    changed = true;
                }
                continue;
            }

            var energy = Energy(r, snap);
            if (energy <= 0) continue;                    // nothing to look for

            // The interval's length, in hours where both reads carry a time. DESCO's live
            // reads land at arbitrary hours, so two reads on consecutive dates can be 2 h
            // apart or 46 h apart; counting whole dates over- or under-states burn.
            double days = prior.ReadAt is { } p && after.ReadAt is { } a
                ? Math.Max(0.25, (a - p).TotalDays)
                : Math.Max(1, (Forecast.Date(after.Date) - Forecast.Date(prior.Date)).Days);
            var burn = Math.Max(0, rate) * days;

            // Anything ELSE in the same interval that we know landed has to come out
            // first, or two recharges bracketed by one pair of readings each explain the
            // other's rise and both get marked Credited off a single credit.
            // Key-change sets are excluded here too.
            var others = snap.Recharges
                .Where(x => KeyOf(x) != key
                         && !x.MultiToken
                         && Forecast.After(x, prior)
                         && !Forecast.After(x, after)
                         && (x.Success
                             || (verdicts.TryGetValue(KeyOf(x), out var ov) && ov.State == CreditState.Credited)))
                .Sum(x => Energy(x, snap));

            // How much of the balance move is left unexplained once ordinary consumption
            // and known credits are subtracted. A landed recharge leaves ~`energy` here;
            // one that never arrived leaves ~0.
            var observed = after.Balance - prior.Balance + burn - others;

            // Rule 1 — the rate-aware test. Half the energy is the midpoint between the
            // two hypotheses, which is the right place to cut.
            bool landed = observed >= energy * 0.5;

            // Rule 2 — the naive comparison, fenced to the case where it is sound. When
            // the recharge is worth more than the entire interval's consumption, a credit
            // that landed CANNOT leave the balance at or below where it started; if it
            // did, rule 1 was fooled by an inflated `burn`.
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
