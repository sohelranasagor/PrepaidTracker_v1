using System.Text.Json.Serialization;

namespace PrepaidTracker;

public enum Utility { Nesco, Desco }

/// <summary>
/// How a sync ended. Was a bool; a third case appeared when DESCO added a login:
/// a sync can now fail because the SESSION is gone, which is not a network failure
/// and not a bad account. Only the user can fix it, so the caller has to be able to
/// tell the difference and prompt rather than silently retry forever.
/// </summary>
public enum SyncOutcome
{
    /// <summary>At least one meter fetched and banked a reading.</summary>
    Ok,

    /// <summary>Nothing came back — network, portal down, or no meters configured.</summary>
    Failed,

    /// <summary>
    /// A DESCO session expired and could not be renewed from stored credentials.
    /// No reading was banked and no verdict was run; the user must sign in again.
    /// </summary>
    AuthRequired
}

public class Meter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";
    public string Account { get; set; } = "";
    public Utility Utility { get; set; }

    /// <summary>
    /// Per-account notification switch. Defaults to true so existing meters keep
    /// alerting after the upgrade; turned off by the user from the notification
    /// settings modal. When false, the meter is skipped by both the live check
    /// and the queued reminder, and any reminders the OS is already holding for
    /// it are cancelled at flip time — otherwise a "turn off" wouldn't take
    /// effect until the next fire.
    /// </summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Per-meter balance threshold, in taka. 0 (the JSON default for a missing
    /// field) means "inherit from <see cref="Store.LowTaka"/>". Users only get
    /// a concrete value here when they override it in the settings sheet — so
    /// existing meters after an upgrade keep alerting at exactly the same taka
    /// figure they did yesterday, and changing the app-wide default still
    /// propagates to every meter that hasn't been customised.
    /// </summary>
    public int LowTaka { get; set; } = 0;

    /// <summary>
    /// Per-meter days-left threshold. Same 0-means-inherit convention as
    /// <see cref="LowTaka"/>; see that comment for the reasoning.
    /// </summary>
    public int LowDays { get; set; } = 0;

    /// <summary>
    /// Sanction load in kW, as DESCO's dashboard reports it ("SANCTION LOAD · 4 kW").
    /// 0 means unknown, which is always the case for NESCO.
    ///
    /// This exists because DESCO's demand charge is PREDICTABLE: it appears on the
    /// first recharge of each calendar month at a flat per-kW rate, and it is NOT
    /// energy. Across 24 recharges on one 4 kW meter the identity
    ///
    ///     energyAmount = totalAmount × 0.95712 − demandCharge − meterRent
    ///
    /// held exactly, with demandCharge = ৳168 = 4 kW × ৳42 on exactly the first
    /// recharge of Jun, Jul, Aug, Sep, Oct and Nov. Knowing the load therefore lets
    /// the recharge suggestion gross up correctly instead of medianing over months
    /// where a demand charge quietly ate ৳168 of the credit.
    ///
    /// Cached on the meter rather than read per-sync so the suggestion still works
    /// on a sync where the summary endpoint didn't answer. When 0, Forecast falls
    /// back to the observed credit ratio rather than inventing a charge.
    /// </summary>
    public double SanctionLoadKw { get; set; }

    /// <summary>Effective ৳ threshold — the override if set, otherwise the app default.</summary>
    [JsonIgnore]
    public int EffectiveLowTaka => LowTaka > 0 ? LowTaka : Store.LowTaka;

    /// <summary>Effective days threshold — the override if set, otherwise the app default.</summary>
    [JsonIgnore]
    public int EffectiveLowDays => LowDays > 0 ? LowDays : Store.LowDays;
}

/// <summary>A balance we observed ourselves. Used for NESCO, where there's no daily feed.</summary>
public class Reading
{
    public string Date { get; set; } = "";
    public double Balance { get; set; }
    public double Recharge { get; set; }
}

public class RechargeRow
{
    public string Date { get; set; } = "";      // yyyy-MM-dd
    public string Time { get; set; } = "";      // HH:mm (24h), when reported
    public double Amount { get; set; }           // payable / gross amount (what was paid)
    public double EnergyAmount { get; set; }     // electricity actually credited, when reported
    public string Via { get; set; } = "";
    public bool Success { get; set; } = true;

    /// <summary>
    /// The portal's own order id ("0000000750633318"). Genuinely stable across syncs,
    /// unlike <see cref="Reconcile.KeyOf"/>'s content hash — but NOT used as the verdict
    /// key, because switching the key orphans every verdict already on disk and would
    /// re-raise banners and re-fire notifications for rows settled weeks ago. Kept for
    /// diagnostics and as the migration target if that is ever done deliberately.
    /// </summary>
    public string OrderId { get; set; } = "";

    /// <summary>
    /// Demand charge bundled into this recharge, when the portal itemises it. DESCO
    /// levies it on the first recharge of a calendar month. It is NOT energy: counting
    /// it as such is what made the observed credit ratio look like it varied wildly
    /// (a ৳990 recharge crediting ৳781.35 instead of the usual ৳947.55).
    /// </summary>
    public double DemandCharge { get; set; }

    /// <summary>
    /// STS tokens for this transaction. Usually exactly one.
    ///
    /// A meter key-change emits a whole SET under a single order — twelve tokens with
    /// sequence "-3~9" in the captured data. Those must be shown (the user has to key
    /// every one of them) but must never be reconciled; see <see cref="MultiToken"/>.
    /// </summary>
    public string[] Tokens { get; set; } = Array.Empty<string>();

    /// <summary>
    /// True when the row carries more than one token, i.e. it is a key-change or
    /// reconfiguration set rather than a single credit. Its Amount does not correspond
    /// to one balance rise, so Reconcile's arithmetic has no meaning on it and
    /// Sync.Record must leave it out of the interval's recharge sum.
    /// </summary>
    public bool MultiToken { get; set; }

    /// <summary>Everything the utility reported for this recharge, for the detail modal.</summary>
    public Dictionary<string, string> Details { get; set; } = new();
}

public class DayRow
{
    public string Date { get; set; } = "";      // yyyy-MM-dd
    public double Taka { get; set; }
    public double Units { get; set; }

    /// <summary>An empty slot in a fixed-width chart — a day we don't have data for yet.
    /// Drawn as a dashed outline, ignored by every stat, and never persisted.</summary>
    [JsonIgnore] public bool Placeholder { get; set; }
}

public class MonthRow
{
    public string Month { get; set; } = "";     // yyyy-MM
    public double Taka { get; set; }
    public double Units { get; set; }

    /// <summary>
    /// True when this row was SYNTHESISED from the running month's total rather than
    /// published as a finished month by the utility (see DescoProvider). It is a partial
    /// month-to-date wearing a month row's clothes, and it must never be carried across
    /// a sync into the next month, where it would harden into a completed month that is
    /// simply wrong. <see cref="Store.SaveSnapshot"/> drops these rather than merging them.
    /// </summary>
    public bool Estimated { get; set; }
}

public class Snapshot
{
    public DateTime SyncedAt { get; set; }
    public double Balance { get; set; }

    /// <summary>
    /// False when the utility answered but included no balance figure — DESCO's
    /// unified (smart-meter) accounts often do this; even their own portal shows
    /// recharge history without a remaining-balance number.
    /// </summary>
    public bool BalanceReported { get; set; } = true;

    public string MeterNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string Address { get; set; } = "";

    /// <summary>When the utility last read the meter. DESCO's balance can be up to 24h stale.</summary>
    public DateTime? ReadingTime { get; set; }

    /// <summary>
    /// Emergency credit the meter may run into before it cuts off, and the smallest
    /// recharge the utility will accept — both READ FROM THE PORTAL, both nullable.
    /// Null means "the portal didn't tell us", and the UI then says nothing rather
    /// than inventing a figure. (The old build shipped a hard-coded 99999 inside the
    /// warning string — a placeholder shown to every user as if it were their meter's.)
    /// </summary>
    public double? EmergencyBalance { get; set; }
    public double? MinRecharge { get; set; }

    /// <summary>
    /// When DESCO will allow this meter's next live read, in LOCAL time. Set only when
    /// DESCO says today's read is used up (status DAILY_LIMIT_REACHED); null means a
    /// read is available (and always, for NESCO).
    ///
    /// While it is in the future the refresh control is greyed, and tapping it shows
    /// DESCO's limit message instead of sending a request. Background syncs never
    /// request a fresh read at all: they use the portal's free cached figure.
    /// </summary>
    public DateTime? NextEligible { get; set; }

    /// <summary>
    /// True when THIS sync heard DESCO's quota verdict. NextEligible is then current
    /// even when null ("not limited"), and Store.SaveSnapshot must not bring back an
    /// older stored limit over it. Not persisted.
    /// </summary>
    [JsonIgnore] public bool QuotaKnown { get; set; }

    /// <summary>
    /// Sanction load in kW as reported by the portal this sync; 0 when absent. Copied
    /// onto <see cref="Meter.SanctionLoadKw"/> by Sync so the recharge suggestion keeps
    /// working on syncs where the summary endpoint doesn't answer.
    /// </summary>
    public double SanctionLoadKw { get; set; }

    public double CurrentMonthConsumption { get; set; }
    public List<RechargeRow> Recharges { get; set; } = new();
    public List<DayRow> Days { get; set; } = new();
    public List<MonthRow> Months { get; set; } = new();
}
