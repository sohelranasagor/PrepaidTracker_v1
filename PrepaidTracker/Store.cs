using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace PrepaidTracker;

public static class Store
{
    static string Dir => FileSystem.AppDataDirectory;
    static string MetersPath => Path.Combine(Dir, "meters.json");
    static string ReadingsPath(string id) => Path.Combine(Dir, $"readings-{id}.json");
    static string SnapshotPath(string id) => Path.Combine(Dir, $"snapshot-{id}.json");

    /// <summary>
    /// Deserialized-file cache. Snapshot(), Readings() and MeterUpdateCount() are each
    /// called TWICE per refresh — once from the page's StateToken(), once from its
    /// Render() — and every one of those was a File.Exists + ReadAllText + Deserialize
    /// on the UI thread.
    ///
    /// The cache is keyed by path and dropped on Write/Delete, so it can never serve a
    /// stale file: nothing else writes to this directory. BackgroundSync writes these
    /// files off the UI thread while the pages read them on it, so every touch of the
    /// dictionary is locked.
    /// </summary>
    static readonly Dictionary<string, object?> _cache = new();

    static T? Read<T>(string path)
    {
        lock (_cache)
            if (_cache.TryGetValue(path, out var hit)) return (T?)hit;

        T? value;
        try { value = File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch { return default; }   // transient failure: don't cache it, a later call may recover

        lock (_cache) _cache[path] = value;
        return value;
    }

    static void Forget(string path)
    {
        lock (_cache) { _cache.Remove(path); _cache.Remove(path + ".bak"); }
    }

    static void Write<T>(string path, T value)
    {
        Forget(path);
        // Atomic: iOS terminates suspended apps without warning, and a direct
        // WriteAllText interrupted mid-write truncates the file — a truncated
        // meters.json means every account vanishes at the next launch. Write to
        // a temp file and move it into place, keeping the previous good copy
        // as .bak so even a corrupt main file is recoverable.
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value));
            if (File.Exists(path))
                try { File.Copy(path, path + ".bak", true); } catch { }
            File.Move(tmp, path, true);
        }
        catch { }
    }

    // ---------- meters ----------
    // Cached: Meters() is called on every render, and a single transient read
    // failure returning an empty list makes the UI flash back to onboarding.
    static List<Meter>? _meters;

    public static List<Meter> Meters()
    {
        if (_meters != null) return _meters;

        // No file at all = genuinely a fresh install; cache the empty list.
        if (!File.Exists(MetersPath)) return _meters = new();

        // File exists: a null here means CORRUPTION, not "no accounts".
        // Try the last good copy before giving up.
        var list = Read<List<Meter>>(MetersPath) ?? Read<List<Meter>>(MetersPath + ".bak");
        if (list != null) return _meters = list;

        // Corrupt and no usable backup: do NOT cache — a later call may still
        // recover — and the returned empty list must never be treated as truth.
        return new();
    }

    public static void SaveMeters(List<Meter> meters)
    {
        _meters = meters;
        Write(MetersPath, meters);
        if (meters.All(m => m.Id != SelectedMeterId))
            SelectedMeterId = meters.FirstOrDefault()?.Id ?? "";
    }

    /// <summary>
    /// Persist a single meter that is already in the list, by id. Used by Sync when the
    /// portal reports something worth remembering on the meter itself — currently the
    /// DESCO sanction load.
    ///
    /// Deliberately NOT an upsert: a meter that isn't in the list is a bug upstream, and
    /// silently inserting it here would resurrect accounts the user just deleted.
    /// </summary>
    public static void SaveMeter(Meter meter)
    {
        var list = Meters();
        var idx = list.FindIndex(x => x.Id == meter.Id);
        if (idx < 0) return;

        list[idx] = meter;
        _meters = list;
        Write(MetersPath, list);
    }

    /// <summary>
    /// Adding an account that is already here is a SELECT, not an insert.
    ///
    /// Identity is utility + account number. The existing row's Id is kept: its readings,
    /// its snapshot and its armed notifications all hang off it.
    /// </summary>
    public static void AddMeter(Meter m)
    {
        var list = Meters();

        var existing = list.FirstOrDefault(x => SameAccount(x, m));
        if (existing != null)
        {
            // A label typed on the second attempt is still worth keeping.
            if (!string.IsNullOrWhiteSpace(m.Label)) existing.Label = m.Label.Trim();
            _meters = list;
            Write(MetersPath, list);
            SelectedMeterId = existing.Id;
            return;
        }

        list.RemoveAll(x => x.Id == m.Id);
        m.Account = m.Account.Trim();
        list.Add(m);
        _meters = list;
        Write(MetersPath, list);
        if (string.IsNullOrEmpty(SelectedMeterId)) SelectedMeterId = m.Id;
    }

    static bool SameAccount(Meter a, Meter b) =>
        a.Utility == b.Utility &&
        string.Equals(a.Account.Trim(), b.Account.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One-time cleanup for installs that already collected duplicate rows before AddMeter
    /// started refusing them. The losers go through DeleteMeter, so their queued
    /// notifications are cancelled rather than left armed in the OS.
    ///
    /// The survivor of each group is the row with the MOST readings, not simply the oldest.
    /// Ties break toward the currently selected row, then toward the oldest.
    /// </summary>
    public static void DedupeMeters()
    {
        var list = Meters();
        if (list.Count < 2) return;

        var groups = new List<List<Meter>>();
        foreach (var m in list)
        {
            var g = groups.FirstOrDefault(x => SameAccount(x[0], m));
            if (g != null) g.Add(m);
            else groups.Add(new List<Meter> { m });
        }
        if (groups.All(g => g.Count == 1)) return;

        var selected = SelectedMeterId;
        foreach (var g in groups)
        {
            if (g.Count == 1) continue;

            var keep = g
                .OrderByDescending(m => Readings(m.Id).Count)
                .ThenByDescending(m => m.Id == selected)
                .ThenBy(m => list.IndexOf(m))
                .First();

            foreach (var m in g)
            {
                if (m.Id == keep.Id) continue;
                // Don't lose the user's place: if the row being removed was the selected
                // one, the selection moves to its survivor.
                if (SelectedMeterId == m.Id) SelectedMeterId = keep.Id;
                DeleteMeter(m.Id);
            }
        }
    }

    public static void DeleteMeter(string id)
    {
        var list = Meters();
        var meter = list.FirstOrDefault(x => x.Id == id);
        list.RemoveAll(x => x.Id == id);

        // A removed account must stop talking. Without this its pre-armed 9am reminders
        // stayed in the OS queue and kept firing for up to three more mornings.
        Notifier.CancelFor(id);
        SetScheduledAlerts(id, "");

        // Destroy the DESCO credential with the account — but ONLY when no other row still
        // uses it. The credential is keyed by account number, and DedupeMeters deletes
        // duplicate rows of the SAME account: signing out there destroyed the surviving
        // row's password, and an upgrading user opened the app signed out for no reason.
        // When the last row for the account goes (a real delete, or a wipe), the secret
        // goes with it.
        if (meter is { Utility: Utility.Desco } && meter.Account.Length > 0
            && !list.Any(x => SameAccount(x, meter)))
            DescoApi.SignOut(meter.Account);

        _meters = list;
        Write(MetersPath, list);
        Forget(ReadingsPath(id)); Forget(SnapshotPath(id)); Forget(UpdatesPath(id)); Forget(VerdictsPath(id));
        try { File.Delete(ReadingsPath(id)); File.Delete(SnapshotPath(id)); File.Delete(UpdatesPath(id)); File.Delete(VerdictsPath(id)); } catch { }
        if (SelectedMeterId == id) SelectedMeterId = list.FirstOrDefault()?.Id ?? "";
    }

    public static string SelectedMeterId
    {
        get => Preferences.Get("selected", "");
        set => Preferences.Set("selected", value);
    }

    public static Meter? Selected()
    {
        var list = Meters();
        return list.FirstOrDefault(m => m.Id == SelectedMeterId) ?? list.FirstOrDefault();
    }

    // ---------- per-meter data ----------
    public static List<Reading> Readings(string id) =>
        (Read<List<Reading>>(ReadingsPath(id)) ?? new())
        .OrderBy(r => r.Date, StringComparer.Ordinal).ToList();

    public static void AddReading(string id, Reading r)
    {
        var list = Readings(id);
        list.RemoveAll(x => x.Date == r.Date);
        list.Add(r);
        Write(ReadingsPath(id), list.OrderBy(x => x.Date, StringComparer.Ordinal).ToList());
    }

    public static void DeleteReading(string id, string date)
    {
        var list = Readings(id);
        list.RemoveAll(x => x.Date == date);
        Write(ReadingsPath(id), list);
    }

    public static Snapshot? Snapshot(string id) => Read<Snapshot>(SnapshotPath(id));

    public static void SaveSnapshot(string id, Snapshot s)
    {
        var prev = Snapshot(id);

        // Accumulate the daily feed across syncs. Kept despite the new portal exposing no
        // daily endpoint: installs upgrading from the old build still hold real history
        // here, and discarding it on first sync would reset their charts to nothing.
        if (prev is { Days.Count: > 0 })
        {
            if (s.Days.Count == 0)
            {
                s.Days = prev.Days;
            }
            else
            {
                var byDate = prev.Days.ToDictionary(d => d.Date, d => d);
                foreach (var d in s.Days) byDate[d.Date] = d;      // fresh rows correct old ones
                s.Days = byDate.Values
                    .OrderBy(d => d.Date, StringComparer.Ordinal)
                    .TakeLast(400)
                    .ToList();
            }
        }

        // Same treatment for the monthly history, except prior ESTIMATED rows are dropped
        // and only genuine finished months kept; the incoming set wins wherever the two
        // overlap.
        if (prev is { Months.Count: > 0 })
        {
            var byMonth = prev.Months
                .Where(m => !m.Estimated)
                .ToDictionary(m => m.Month, m => m);
            foreach (var m in s.Months) byMonth[m.Month] = m;
            s.Months = byMonth.Values
                .OrderBy(m => m.Month, StringComparer.Ordinal)
                .TakeLast(36)
                .ToList();
        }

        // Carry forward DESCO's live-read limit ONLY when this sync didn't hear DESCO's
        // verdict at all (the cached realtime call failed). When it did, the fresh value
        // is the truth — including null, which means "a read is available".
        //
        // The guard is also on the VALUE: once the moment has passed there is nothing to
        // preserve, so a stale limit can never outlive itself and jam the button shut.
        if (!s.QuotaKnown && s.NextEligible is null
            && prev?.NextEligible is { } priorLimit && priorLimit > DateTime.Now)
            s.NextEligible = priorLimit;

        // Sanction load changes essentially never, and it arrives only from the summary
        // endpoint. Keeping the last known figure means one unanswered summary call
        // doesn't silently switch the recharge suggestion back to its fallback path.
        if (s.SanctionLoadKw <= 0 && prev is { SanctionLoadKw: > 0 })
            s.SanctionLoadKw = prev.SanctionLoadKw;

        Write(SnapshotPath(id), s);
    }

    // ---------- failed-recharge verdicts (see Reconcile.cs) ----------
    static string VerdictsPath(string id) => Path.Combine(Dir, $"verdicts-{id}.json");

    public static Dictionary<string, Verdict> Verdicts(string id) =>
        Read<Dictionary<string, Verdict>>(VerdictsPath(id)) ?? new();

    /// <summary>
    /// Bounded to the newest 60 rows. The key is prefixed with the recharge date, so an
    /// ordinal sort is a chronological one and the oldest settled verdicts fall off first.
    /// </summary>
    public static void SaveVerdicts(string id, Dictionary<string, Verdict> v)
    {
        if (v.Count > 60)
            v = v.OrderByDescending(kv => kv.Key, StringComparer.Ordinal)
                 .Take(60)
                 .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Write(VerdictsPath(id), v);
    }

    // ---------- meter update log ----------
    // Every DISTINCT reading time the utility has published for this meter.
    static string UpdatesPath(string id) => Path.Combine(Dir, $"updates-{id}.json");

    public static void RecordMeterUpdate(string id, DateTime readingTime)
    {
        var list = Read<List<string>>(UpdatesPath(id)) ?? new();
        var key = readingTime.ToString("yyyy-MM-dd HH:mm:ss");
        if (list.Contains(key)) return;
        list.Add(key);
        Write(UpdatesPath(id), list);
    }

    public static int MeterUpdateCount(string id) =>
        (Read<List<string>>(UpdatesPath(id)) ?? new()).Count;

    // ---------- access control (see Access.cs) ----------

    /// <summary>
    /// Anonymous, per-install id. NOT the account number and not anything about the
    /// person — the server only ever needs to answer "is this install still allowed in".
    /// </summary>
    public static string InstallId
    {
        get
        {
            var v = Preferences.Get("installId", "");
            if (v.Length == 0) Preferences.Set("installId", v = Guid.NewGuid().ToString("N"));
            return v;
        }
    }

    /// <summary>The unique name this device claimed on first launch. The owner blocks by this.</summary>
    public static string Username
    {
        get => Preferences.Get("username", "");
        set => Preferences.Set("username", value);
    }

    /// <summary>Last verdict from the server, persisted: a blocked user must not be able to
    /// escape the wall simply by putting the phone in airplane mode.</summary>
    public static bool Blocked
    {
        get => Preferences.Get("blocked", false);
        set => Preferences.Set("blocked", value);
    }

    /// <summary>
    /// Everything the user added, gone. Used when the owner blocks the device. DeleteMeter
    /// also clears each DESCO account's stored password and session once its last row
    /// goes, so a wipe genuinely leaves nothing behind.
    /// </summary>
    public static void WipeMeters()
    {
        foreach (var m in Meters().ToList()) DeleteMeter(m.Id);
        _meters = new();
        SaveMeters(new());
    }

    // ---------- settings ----------
    public static string Language
    {
        get => Preferences.Get("lang", "en");
        set => Preferences.Set("lang", value);
    }

    public static double? Tariff
    {
        get
        {
            var v = Preferences.Get("tariff", "");
            return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
        }
        set => Preferences.Set("tariff", value?.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    public static int LowDays
    {
        get => Preferences.Get("lowDays", 5);
        set => Preferences.Set("lowDays", value);
    }

    /// <summary>Alert when the balance falls under this many Taka.</summary>
    public static int LowTaka
    {
        get => Preferences.Get("lowTaka", 200);
        set => Preferences.Set("lowTaka", value);
    }

    /// <summary>How many days a suggested recharge should cover.</summary>
    public static int TargetDays
    {
        get => Preferences.Get("targetDays", 30);
        set => Preferences.Set("targetDays", value);
    }

    public static bool AlertsEnabled
    {
        get => Preferences.Get("alerts", true);
        set => Preferences.Set("alerts", value);
    }

    public static string LastAlert(string meterId) => Preferences.Get("alert-" + meterId, "");
    public static void SetLastAlert(string meterId, string date) => Preferences.Set("alert-" + meterId, date);

    /// <summary>
    /// The dates this meter currently has an OS-held reminder armed for, as "yyyy-MM-dd:idx"
    /// pairs. Writing the dates down at arming time is how the live alert knows to stay
    /// quiet instead of adding a second banner to the same morning.
    /// </summary>
    public static string ScheduledAlerts(string meterId) => Preferences.Get("sched-" + meterId, "");
    public static void SetScheduledAlerts(string meterId, string csv) => Preferences.Set("sched-" + meterId, csv);

    /// <summary>
    /// Every meter id we've ever handed to the notifier, kept so an orphan sweep can find
    /// alerts still queued for a meter that's since been deleted or replaced.
    /// </summary>
    public static HashSet<string> KnownMeterIds()
    {
        var csv = Preferences.Get("knownMeterIds", "");
        return csv.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(csv.Split('\u001F', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }

    public static void SetKnownMeterIds(IEnumerable<string> ids) =>
        Preferences.Set("knownMeterIds", string.Join('\u001F', ids));

    public static void RememberMeterId(string id)
    {
        var s = KnownMeterIds();
        if (s.Add(id)) SetKnownMeterIds(s);
    }
}
