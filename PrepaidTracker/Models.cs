using System.Text.Json.Serialization;

namespace PrepaidTracker;

public enum Utility { Nesco, Desco }

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

    public double CurrentMonthConsumption { get; set; }
    public List<RechargeRow> Recharges { get; set; } = new();
    public List<DayRow> Days { get; set; } = new();
    public List<MonthRow> Months { get; set; } = new();
}
