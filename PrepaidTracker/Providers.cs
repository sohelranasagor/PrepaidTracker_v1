using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrepaidTracker;

public class FetchResult
{
    public bool Ok;
    public string? Error;
    public Snapshot? Snapshot;
}

public interface IProvider
{
    Task<FetchResult> FetchAsync(string account, CancellationToken ct = default);
}

public static class Providers
{
    // DESCO's certificate chain is unreliable. The exception is scoped to DESCO's own
    // hosts: this handler also carries NESCO's POSTs, and accepting anybody's bad
    // certificate because one utility misconfigured theirs is a different decision.
    //
    // Now `internal` rather than private: DescoApi carries the authenticated session and
    // must go through this same host-scoped callback. Giving it its own handler with
    // validation disabled outright would be a real downgrade, because that connection
    // now carries a PASSWORD, not just a meter number.
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, _, _, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                var host = (sender as HttpRequestMessage)?.RequestUri?.Host
                           ?? sender as string ?? "";
                return host.EndsWith("desco.org.bd", StringComparison.OrdinalIgnoreCase);
            }
        },
        AutomaticDecompression = DecompressionMethods.All,
        CookieContainer = new CookieContainer(),
        UseCookies = true
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    static Providers() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1");

    public static IProvider For(Utility u) => u == Utility.Desco
        ? new DescoProvider(Http)
        : new NescoProvider(Http);

    /// <summary>Bangla numerals to ASCII. Both portals mix them in.</summary>
    public static string Digits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch >= '\u09E6' && ch <= '\u09EF' ? (char)('0' + (ch - '\u09E6')) : ch);
        return sb.ToString();
    }

    public static double Num(string? s)
    {
        var m = Regex.Match(Digits(s).Replace(",", ""), @"-?\d+(?:\.\d+)?");
        return m.Success && double.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}

// ---------------------------------------------------------------------------
// DESCO — authenticated session API (new portal, November 2026).
//
// The old unauthenticated api/tkdes/customer/* family is gone. DESCO rebuilt the
// portal with a login — their own notice says authentication was added to protect
// customer data — so every call now rides a session cookie obtained by DescoApi.
//
// Endpoints, as observed on the new portal:
//
//   realtime-balance   balance + the live-read quota state
//   summary            month-to-date, sanction load, customer details
//   monthly            finished months
//   transactions       recharges, with energyAmount, demandCharge and STS tokens
//
// Three facts shape everything below:
//
//   1. LIVE READS ARE RATIONED — roughly one a day, after which the portal answers
//      status DAILY_LIMIT_REACHED and names a nextEligibleAt. So an ordinary sync
//      NEVER requests a fresh read: it uses lastRealtimeBalance, which is free and is
//      the same midnight-snapshot figure the old getBalance returned. Only an explicit
//      user refresh spends an attempt, and only once nextEligibleAt has passed. The old
//      "sync on every app open" habit would exhaust the day's allowance before noon.
//
//   2. THEIR OWN /balance ENDPOINT 404s on UNIFIED meters — their portal renders "The
//      requested resource was not found" in its own UI. We don't call it.
//
//   3. NO DAILY FEED has been located on the new portal. Snapshot.Days therefore stays
//      empty and Forecast falls through to the monthly total, then to our own readings.
//      At ~1 read/day that needs three syncs before the first derived bar appears.
// ---------------------------------------------------------------------------
public class DescoProvider : IProvider
{
    /// <summary>
    /// Why the last fetch failed, when it failed for session reasons. NeedsLogin is the
    /// one the caller must act on: only the user can fix it, and Sync must not bank a
    /// reading or run Reconcile off a fetch that never happened.
    /// </summary>
    public SessionState LastState { get; private set; } = SessionState.Ok;

    /// <summary>
    /// Request a fresh meter read this sync, spending one of the day's rationed reads.
    /// Set by the pull-to-refresh path only; every background caller leaves it false.
    /// Carried on the instance rather than the call because IProvider's signature is
    /// shared with NescoProvider and changing it would ripple through every call site.
    /// </summary>
    public bool UserRequestedFresh { get; set; }

    /// <summary>What happened last sync, for Profile → Diagnostics.</summary>
    public static string ProbeLog { get; private set; } = "";

    readonly HttpClient _http;
    readonly List<string> _log = new();

    public DescoProvider(HttpClient http) => _http = http;

    void Log(string what, string outcome) => _log.Add($"{what,-24} {outcome}");

    public async Task<FetchResult> FetchAsync(string account, CancellationToken ct = default)
    {
        _log.Clear();
        LastState = SessionState.Ok;

        var api = DescoApi.For(account);
        var snap = new Snapshot { SyncedAt = DateTime.Now };

        // ---- balance + quota state --------------------------------------------
        // This call ALONE decides the session verdict. A 401 on a secondary endpoint
        // later must not throw away a balance we already hold: an earlier design hung
        // the state off the provider and let whichever endpoint ran last overwrite it,
        // so a good reading was discarded because the monthly feed expired mid-sync.
        var (rt, st) = await api.GetAsync(Wire.PathRealtime, ct);
        if (st != SessionState.Ok)
        {
            LastState = st;
            Log("realtime-balance", st.ToString());
            ProbeLog = string.Join("\n", _log);
            return new FetchResult
            {
                Error = st switch
                {
                    SessionState.NeedsLogin => L.S("authExpiredBody"),
                    SessionState.Misconfigured => L.S("authPortalChanged"),
                    _ => L.S("authUnreachable")
                }
            };
        }

        var row = rt is null ? null : PickMeter(rt.Value, account);
        if (row is null)
        {
            Log("realtime-balance", rt is null ? "no data" : "no meter row");
            ProbeLog = string.Join("\n", _log);
            return new FetchResult { Error = "No data returned.\n\n" + ProbeLog };
        }

        var status = FirstString(row.Value, "status");
        var nextOk = ParseIso(FirstString(row.Value, "nextEligibleAt"));
        var lastAt = ParseIso(FirstString(row.Value, "lastRealtimeReceivedAt", "readingTime"));

        // Spend a rationed read only when the user asked AND it can actually succeed.
        // Note the portal's own fields disagree under DAILY_LIMIT_REACHED (it reported
        // attemptsToday 1 with remainingAttempts 2 while refusing), so `status` and
        // `nextEligibleAt` are the sources of truth and remainingAttempts is ignored.
        if (UserRequestedFresh
            && !status.Equals("DAILY_LIMIT_REACHED", StringComparison.OrdinalIgnoreCase)
            && (nextOk is null || DateTime.Now >= nextOk))
        {
            var (fresh, fs) = await api.GetAsync(Wire.PathRealtime + Wire.RefreshQuery, ct);
            if (fs != SessionState.Ok)
            {
                LastState = fs;
                Log("realtime-refresh", fs.ToString());
            }
            else if (fresh is not null && PickMeter(fresh.Value, account) is { } fr)
            {
                row = fr;
                nextOk = ParseIso(FirstString(fr, "nextEligibleAt"));
                lastAt = ParseIso(FirstString(fr, "lastRealtimeReceivedAt", "readingTime"));
                Log("realtime-refresh", "fresh read spent");
            }
        }
        else if (UserRequestedFresh)
            Log("realtime-refresh", $"skipped: {(status.Length > 0 ? status : "not yet eligible")}");

        // A balance of 0.00, or a negative one (emergency credit), is a valid and
        // urgent answer. The question is whether the portal gave us the FIELD.
        var balText = FirstString(row.Value, "lastRealtimeBalance", "balance", "remainingBalance");
        snap.BalanceReported = balText.Length > 0;
        snap.Balance = snap.BalanceReported ? Providers.Num(balText) : 0;
        snap.MeterNo = FirstString(row.Value, "meterNo");

        // lastRealtimeReceivedAt is UTC with a Z suffix ("2026-11-09T03:05:02.976Z" =
        // 09:05 Dhaka, which is what the portal's own UI shows). ParseIso converts; do
        // NOT substitute the sync time, which would imply a liveness the data lacks.
        snap.ReadingTime = lastAt;
        snap.NextEligible = nextOk;
        Log("realtime-balance", snap.BalanceReported ? "ok" : "no balance");

        // ---- summary: month-to-date, sanction load, customer -------------------
        var (sum, _) = await api.GetAsync(Wire.PathSummary, ct);
        if (sum is not null)
        {
            snap.CurrentMonthConsumption =
                FirstOf(sum.Value, "currentMonthConsumption", "monthToDate", "consumedTaka");
            snap.SanctionLoadKw = FirstOf(sum.Value, "sanctionLoad", "sanctionedLoad", "loadKw");
            snap.CustomerName = FirstString(sum.Value, "customerName", "name");
            snap.Address = FirstString(sum.Value, "installationAddress", "address");
            snap.MinRecharge = Positive(FirstOf(sum.Value, "minRecharge", "minimumRecharge"));
            snap.EmergencyBalance = Positive(FirstOf(sum.Value, "emergencyBalance", "emergencyCredit"));
            Log("summary", "ok");
        }
        else Log("summary", "no data");

        // ---- monthly consumption ----------------------------------------------
        var monthFrom = DateTime.Now.AddMonths(-11).ToString("yyyy-MM");
        var monthTo = DateTime.Now.ToString("yyyy-MM");
        var (mon, _) = await api.GetAsync($"{Wire.PathMonthly}?monthFrom={monthFrom}&monthTo={monthTo}", ct);
        if (mon is not null)
        {
            // Parse and validate before accepting: rows whose month string is shorter
            // than "yyyy-MM" are dropped downstream, and counting raw rows left Months
            // empty with nothing left to try.
            var parsed = AsArray(mon).Select(m => new MonthRow
            {
                Month = FirstString(m, "month", "yearMonth", "monthName", "period"),
                Taka = FirstOf(m, "consumedTaka", "amount", "taka"),
                Units = FirstOf(m, "consumedUnit", "unit", "kwh")
            }).Where(m => m.Month.Length >= 7).ToList();

            snap.Months.AddRange(parsed);
            Log("monthly", parsed.Count > 0 ? $"{parsed.Count} rows" : "0 rows");
        }
        else Log("monthly", "no data");

        // ---- transactions ------------------------------------------------------
        var from = DateTime.Now.AddDays(-365).ToString("yyyy-MM-dd");
        var to = DateTime.Now.ToString("yyyy-MM-dd");
        var (txn, _) = await api.GetAsync($"{Wire.PathTxns}?dateFrom={from}&dateTo={to}", ct);
        if (txn is not null)
        {
            foreach (var r in AsArray(txn))
            {
                var raw = FirstString(r, "rechargeDate", "date");
                if (raw.Length < 10) continue;

                var amount = FirstOf(r, "totalAmount", "amount");
                if (amount <= 0) continue;

                // The portal's own spelling, typo included: "Execution Successful"
                // or "Not Returnd". Anything that isn't the success string is a
                // failure, which is the safe direction — a row wrongly treated as
                // failed gets settled by the next read; one wrongly treated as
                // successful silently inflates the day's usage.
                var orderStatus = FirstString(r, "orderStatus", "status");
                var success = orderStatus.Equals("Execution Successful", StringComparison.OrdinalIgnoreCase);

                // A meter key-change arrives as ONE transaction carrying a SET of
                // tokens (sequence "-3~9", twelve of them). Reconcile must not try to
                // settle that against a single balance rise — see CreditState.Informational.
                var tokens = FirstString(r, "token")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                snap.Recharges.Add(new RechargeRow
                {
                    Date = raw[..10],
                    Time = raw.Length >= 16 ? raw.Substring(11, 5) : "",
                    Amount = amount,
                    // energyAmount already EXCLUDES the demand charge: 600 → 574.27 with
                    // none, 400 → 214.85 with ৳168 of it. Verified across 24 recharges.
                    EnergyAmount = FirstOf(r, "energyAmount", "energy"),
                    DemandCharge = FirstOf(r, "demandCharge"),
                    OrderId = FirstString(r, "orderId"),
                    Via = FirstString(r, "rechargeOperator", "operator", "channel").ToUpperInvariant(),
                    Success = success,
                    Tokens = tokens,
                    MultiToken = tokens.Length > 1,
                    Details = AllFields(r)
                });
            }
            Log("transactions", $"{snap.Recharges.Count} rows");
        }
        else Log("transactions", "no data");

        snap.Recharges = snap.Recharges.OrderBy(r => r.Date, StringComparer.Ordinal).ToList();

        // ---- synthesise the running month -------------------------------------
        // Some accounts return a month-to-date figure while the monthly list hasn't
        // appended the running month yet. Same authoritative source, different endpoint.
        // Flagged Estimated so Store.SaveSnapshot drops it rather than letting a
        // month-to-date harden into a completed month next month.
        if (snap.CurrentMonthConsumption > 0)
        {
            var thisMonth = DateTime.Now.ToString("yyyy-MM");
            if (!snap.Months.Any(m => m.Month == thisMonth))
                snap.Months.Add(new MonthRow
                {
                    Month = thisMonth,
                    Taka = snap.CurrentMonthConsumption,
                    Estimated = true
                });
        }
        snap.Months = snap.Months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList();

        // No daily endpoint on the new portal. The old daily-feed sanity check (which
        // discarded a feed whose this-month sum exceeded 2× currentMonthConsumption)
        // has nothing to guard and is gone with the feed. Restore BOTH together if a
        // daily endpoint is ever located — an unvalidated feed on an unknown scale is
        // what produced the "Spent this month ৳549 / Daily avg ৳314" contradiction.
        Log("daily", "no endpoint on new portal");

        ProbeLog = string.Join("\n", _log);

        // Only fail when DESCO gave us nothing at all. A missing balance with working
        // recharge history is a partial success, not an error.
        if (!snap.BalanceReported && snap.Recharges.Count == 0 && snap.Months.Count == 0)
            return new FetchResult { Error = "No data returned.\n\n" + ProbeLog };

        return new FetchResult { Ok = true, Snapshot = snap };
    }

    static double? Positive(double v) => v > 0 ? v : null;

    /// <summary>
    /// The row for this account out of the realtime-balance payload. The response is
    /// { enabled, meters: [ … ] } — one entry per meter on the login, so a user with
    /// two meters gets two, and matching on the account the user typed is the only way
    /// to pick the right one. Falls back to the first entry, then to the bare object
    /// in case a future shape drops the wrapper.
    /// </summary>
    static JsonElement? PickMeter(JsonElement root, string account)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("meters", out var meters)
            && meters.ValueKind == JsonValueKind.Array)
        {
            JsonElement? first = null;
            foreach (var m in meters.EnumerateArray())
            {
                first ??= m;
                if (FirstString(m, "accountNo") == account || FirstString(m, "meterNo") == account)
                    return m;
            }
            return first;
        }
        return root.ValueKind == JsonValueKind.Object ? root : null;
    }

    /// <summary>
    /// ISO-8601 with a Z suffix → LOCAL time. The portal reports UTC; Dhaka is +6, so
    /// treating these as local would shift every read time by six hours and make a
    /// midnight read look like a 6am one.
    /// </summary>
    static DateTime? ParseIso(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d.LocalDateTime : null;

    // --- JSON helpers. Same contracts as the previous DescoProvider: DESCO's wrapping
    //     and field naming are both inconsistent, so never read one hardcoded name and
    //     never assume a bare array. ---

    static IEnumerable<JsonElement> AsArray(JsonElement? e)
    {
        if (e is null) yield break;
        var v = e.Value;

        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in v.EnumerateArray()) yield return i;
            yield break;
        }

        if (v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                {
                    foreach (var i in p.Value.EnumerateArray()) yield return i;
                    yield break;
                }
    }

    static double FirstOf(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        foreach (var n in names)
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind == JsonValueKind.Number
                        ? p.Value.GetDouble() : Providers.Num(p.Value.ToString());
        return 0;
    }

    static string FirstString(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return "";
        foreach (var n in names)
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind != JsonValueKind.Null)
                    return p.Value.ToString() ?? "";
        return "";
    }

    /// <summary>Every non-empty scalar field, prettified for the detail modal.</summary>
    static Dictionary<string, string> AllFields(JsonElement e)
    {
        var d = new Dictionary<string, string>();
        if (e.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array) continue;
            var v = p.Value.ToString().Trim();
            if (v.Length == 0) continue;
            // camelCase → "Camel Case"
            var key = Regex.Replace(p.Name, "(?<=[a-z0-9])(?=[A-Z])", " ");
            key = char.ToUpperInvariant(key[0]) + key[1..];
            d[key] = v;
        }
        return d;
    }
}

// ---------------------------------------------------------------------------
// NESCO — no API. The portal is a Laravel form: GET for the CSRF token, then
// POST {_token, cust_no, submit}, where `submit` is a Bengali label choosing
// which table comes back. No login, no captcha.
//
// UNCHANGED by the DESCO migration.
// ---------------------------------------------------------------------------
public class NescoProvider : IProvider
{
    public const string Panel = "https://customer.nesco.gov.bd/pre/panel";
    const string SubmitRecharge = "রিচার্জ হিস্ট্রি";
    const string SubmitMonthly = "মাসিক ব্যবহার";

    readonly HttpClient _http;
    public NescoProvider(HttpClient http) => _http = http;

    public async Task<FetchResult> FetchAsync(string account, CancellationToken ct = default)
    {
        string token;
        try
        {
            var landing = await _http.GetStringAsync(Panel, ct);
            var m = Regex.Match(landing, @"name=[""']csrf-token[""']\s+content=[""']([^""']+)");
            if (!m.Success) return new FetchResult { Error = "NESCO portal changed — no CSRF token found." };
            token = m.Groups[1].Value;
        }
        catch (Exception e) { return new FetchResult { Error = $"Couldn't reach NESCO: {e.Message}" }; }

        var snap = new Snapshot { SyncedAt = DateTime.Now };

        try
        {
            var rechargeHtml = await Post(token, account, SubmitRecharge, ct);
            var fields = LabelledFields(rechargeHtml, account);

            // When the portal finds no consumer it renders only the search box, which
            // we've already excluded. No fields at all means no account.
            if (fields.Count == 0)
                return new FetchResult { Error = "No NESCO consumer found for that number." };

            snap.CustomerName = Pick(fields, 1, "নাম", "name");
            snap.Address = Pick(fields, 3, "ঠিকানা", "address");
            snap.MeterNo = Pick(fields, 8, "মিটার", "meter");

            // Numbers the portal actually publishes about this meter. Both are
            // OPTIONAL: if NESCO doesn't render the field, these stay null and the
            // UI stays quiet about them. Never substitute a guess.
            snap.MinRecharge = PickNum(fields, "মিনিমাম রিচার্জ", "সর্বনিম্ন রিচার্জ", "minimum recharge", "min recharge");
            snap.EmergencyBalance = PickNum(fields, "জরুরি", "জরুরী", "ইমার্জেন্সি", "emergency");

            // Prefer a labelled balance field; the portal otherwise puts it last.
            // A meter running on emergency credit reports a NEGATIVE balance
            // (e.g. -86.674) and a meter that has just run out reads exactly 0.00.
            // Both are real, correct readings — and the ones the customer most needs
            // to see. So the question is "did the portal give us a balance field?",
            // never "is that number positive?".
            var balText = Pick(fields, -1, "ব্যালেন্স", "balance", "স্থিতি", "জের");
            var balNum = Providers.Num(balText);

            // Never mistake the echoed account/meter number for money.
            var echoedAccount = balText.Length > 0 && Providers.Num(account) == balNum;

            snap.BalanceReported = balText.Length > 0 && !echoedAccount;
            snap.Balance = snap.BalanceReported ? balNum : 0;

            // The portal DOES publish when that balance was read, in a small
            // caption under the label: "(সময়ঃ 11 July 2026 12:00:00 AM)". That
            // moment — not our sync time — is how fresh the money figure really
            // is, and it's what lets the app count the meter's actual updates.
            snap.ReadingTime = ExtractReadingTime(rechargeHtml);

            // The portal's recharge table has shipped with 12, 13 and 14+ column
            // variants, so hard-coded indices silently produce zero rows. Find the
            // columns instead: headers first, cell content as the fallback.
            var headers = HeaderCells(rechargeHtml);
            int amountCol = FindHeader(headers, "রিচার্জের পরিমাণ", "মোট", "total", "gross", "recharge amount");
            int energyCol = FindHeader(headers, "বিদ্যুৎ", "এনার্জি", "energy");
            int viaCol = FindHeader(headers, "অপারেটর", "operator", "মাধ্যম", "channel");

            foreach (var row in TableRows(rechargeHtml))
            {
                // The date cell anchors the row — a row without one isn't a recharge.
                string? date = null; var time = ""; int dateIdx = -1;
                for (int i = 0; i < row.Count; i++)
                {
                    var d = ParseNescoDate(row[i]);
                    if (d == null) continue;
                    date = d; time = ParseNescoTime(row[i]); dateIdx = i;
                    break;
                }
                if (date == null) continue;

                double amount = amountCol >= 0 && amountCol < row.Count ? Providers.Num(row[amountCol]) : 0;
                if (amount <= 0) amount = GuessAmount(row, dateIdx);
                if (amount <= 0) continue;

                double energy = energyCol >= 0 && energyCol < row.Count && energyCol != dateIdx
                    ? Providers.Num(row[energyCol]) : 0;

                var via = viaCol >= 0 && viaCol < row.Count && viaCol != dateIdx ? row[viaCol] : "";
                if (via.Length == 0)
                    via = row.FirstOrDefault(c => OperatorWords.IsMatch(c)) ?? "";

                // Everything the portal showed, header-labelled, for the detail modal.
                var details = new Dictionary<string, string>();
                for (int i = 0; i < row.Count; i++)
                {
                    if (row[i].Length == 0) continue;
                    var key = i < headers.Count && headers[i].Length > 0 ? headers[i] : $"#{i + 1}";
                    if (!details.ContainsKey(key)) details[key] = row[i];
                }

                snap.Recharges.Add(new RechargeRow
                {
                    Date = date,
                    Time = time,
                    Amount = amount,
                    EnergyAmount = energy,
                    Via = via.ToUpperInvariant(),
                    Success = !row.Any(c => Regex.IsMatch(c, @"fail|unsuccess|ব্যর্থ|বাতিল|cancel", RegexOptions.IgnoreCase)),
                    Details = details
                });
            }
        }
        catch (Exception e) { return new FetchResult { Error = $"NESCO sync failed: {e.Message}" }; }

        try
        {
            var monthlyHtml = await Post(token, account, SubmitMonthly, ct);

            // Same customer box is rendered on this page too — a second chance at
            // the "(সময়ঃ …)" caption if the recharge page didn't carry it.
            snap.ReadingTime ??= ExtractReadingTime(monthlyHtml);

            foreach (var row in TableRows(monthlyHtml))
            {
                if (row.Count < 5) continue;
                var year = (int)Providers.Num(row[0]);
                var month = MonthNumber(row[1]);
                if (year < 2015 || month == 0) continue;
                snap.Months.Add(new MonthRow { Month = $"{year:0000}-{month:00}", Taka = Providers.Num(row[4]) });
            }
            snap.Months = snap.Months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList();
        }
        catch { /* monthly is optional */ }

        var thisMonth = DateTime.Now.ToString("yyyy-MM");
        snap.CurrentMonthConsumption = snap.Months.FirstOrDefault(m => m.Month == thisMonth)?.Taka ?? 0;

        // NESCO publishes no daily feed, so Snapshot.Days stays empty. The forecast
        // falls back to the monthly figure, then to balance readings we take ourselves.
        return new FetchResult { Ok = true, Snapshot = snap };
    }

    async Task<string> Post(string token, string account, string submit, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_token"] = token,
            ["cust_no"] = account,
            ["submit"] = submit
        });
        var req = new HttpRequestMessage(HttpMethod.Post, Panel) { Content = form };
        req.Headers.Referrer = new Uri(Panel);
        var res = await _http.SendAsync(req, ct);
        return await res.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Walk the HTML in document order pairing each &lt;label&gt; with the &lt;input&gt; that
    /// follows it. Matching on the label text is the only safe way to find the balance —
    /// counting inputs breaks the moment the portal adds a field.
    /// </summary>
    static List<(string Label, string Value)> LabelledFields(string html, string account)
    {
        var fields = new List<(string, string)>();
        string? pendingLabel = null;

        foreach (Match tag in Regex.Matches(html,
                     @"<label[^>]*>(?<lab>.*?)</label>|<input\b(?<attrs>[^>]*)>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (tag.Groups["lab"].Success)
            {
                pendingLabel = Clean(tag.Groups["lab"].Value);
                continue;
            }

            var attrs = tag.Groups["attrs"].Value;

            // Skip the search box, hidden CSRF fields and buttons.
            var name = Attr(attrs, "name");
            var type = Attr(attrs, "type").ToLowerInvariant();
            if (name is "cust_no" or "_token" || type is "hidden" or "submit" or "button") { pendingLabel = null; continue; }

            var value = Providers.Digits(WebUtility.HtmlDecode(Attr(attrs, "value"))).Trim();
            if (value.Length == 0) { pendingLabel = null; continue; }
            if (value == account) { pendingLabel = null; continue; }   // echoed input, not data

            fields.Add((pendingLabel ?? "", value));
            pendingLabel = null;
        }
        return fields;
    }

    static string Attr(string attrs, string name)
    {
        var m = Regex.Match(attrs, name + @"\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    static string Clean(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", " ", RegexOptions.Singleline)), @"\s+", " ").Trim();

    /// <summary>
    /// When NESCO last read this meter's balance — the "(সময়ঃ 11 July 2026
    /// 12:00:00 AM)" caption under the balance box.
    ///
    /// Two traps, both of which silently returned null before:
    ///   1. UNICODE. "সময়" can be encoded two ways — য়=U+09DF precomposed, or
    ///      য+় = U+09AF U+09BC decomposed. They render identically and compare
    ///      UNEQUAL. Both sides are normalised to FormD here.
    ///   2. MARKUP. The caption is its own element, so tags between the keyword
    ///      and the date broke a raw-HTML match. We flatten first.
    /// </summary>
    public static DateTime? ExtractReadingTime(string html)
    {
        // The customer box sits above the first table; stay out of the recharge rows.
        var cut = html.IndexOf("<table", StringComparison.OrdinalIgnoreCase);
        var scope = cut > 0 ? html[..cut] : html;

        var text = Providers.Digits(Clean(scope)).Normalize(NormalizationForm.FormD);

        const string DatePart = @"\d{1,2}\s+[A-Za-z]{3,9}\s+(?:19|20)\d{2}"
                             + @"(?:\s+\d{1,2}:\d{2}(?::\d{2})?\s*(?:[AaPp]\.?\s*[Mm]\.?)?)?";

        var keyword = "সময়".Normalize(NormalizationForm.FormD);
        var m = Regex.Match(text, Regex.Escape(keyword) + @"\s*[ঃ:]?\s*(?<v>" + DatePart + ")");

        if (!m.Success)
        {
            // No keyword (portal reworded it?) — take the LAST such date in the box,
            // which is the one sitting with the balance.
            var all = Regex.Matches(text, @"(?<v>" + DatePart + ")");
            if (all.Count == 0) return null;
            m = all[^1];
        }

        var raw = Regex.Replace(m.Groups["v"].Value, @"\s+", " ")
                       .Replace(".", "", StringComparison.Ordinal)   // A.M → AM
                       .Trim();

        string[] formats =
        {
            "d MMMM yyyy h:mm:ss tt", "d MMMM yyyy h:mm tt",
            "d MMM yyyy h:mm:ss tt",  "d MMM yyyy h:mm tt",
            "d MMMM yyyy HH:mm:ss",   "d MMMM yyyy HH:mm",
            "d MMM yyyy HH:mm:ss",    "d MMM yyyy HH:mm",
            "d MMMM yyyy",            "d MMM yyyy"
        };

        return DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var d)
            ? d : null;
    }

    /// <summary>
    /// Bangla label comparison, immune to the nukta trap: য়/ড়/ঢ় each have a
    /// precomposed AND a decomposed encoding that render identically but compare
    /// unequal. Fold both sides to FormD before matching.
    /// </summary>
    static bool LabelHas(string label, string word) =>
        label.Normalize(NormalizationForm.FormD)
             .Contains(word.Normalize(NormalizationForm.FormD), StringComparison.OrdinalIgnoreCase);

    /// <summary>A labelled numeric field, or null if the portal didn't publish it.</summary>
    static double? PickNum(List<(string Label, string Value)> fields, params string[] words)
    {
        foreach (var w in words)
            foreach (var f in fields)
                if (LabelHas(f.Label, w) && Regex.IsMatch(f.Value, @"-?\d"))
                    return Providers.Num(f.Value);
        return null;
    }

    static string Pick(List<(string Label, string Value)> fields, int fallbackIndex, params string[] words)
    {
        foreach (var w in words)
            foreach (var f in fields)
                if (LabelHas(f.Label, w))
                    return f.Value;

        if (fallbackIndex == -1)
        {
            // Last numeric-looking field. `> 0` used to be the test here, which
            // silently skipped past a negative (emergency-credit) balance.
            for (int i = fields.Count - 1; i >= 0; i--)
                if (Regex.IsMatch(fields[i].Value, @"^-?\d[\d,]*(\.\d+)?$")) return fields[i].Value;
            return "";
        }
        return fallbackIndex >= 0 && fallbackIndex < fields.Count ? fields[fallbackIndex].Value : "";
    }

    /// <summary>
    /// "01-JAN-2025 3:10 PM" → "15:10". The old version dropped the meridiem, so
    /// afternoon recharges showed as e.g. "3:10" with no way to tell AM from PM.
    /// Store 24-hour; the UI formats it back to 12-hour with AM/PM.
    /// </summary>
    static string ParseNescoTime(string raw)
    {
        var m = Regex.Match(Providers.Digits(raw), @"(\d{1,2}):(\d{2})(?::\d{2})?\s*([AaPp])\.?\s*[Mm]?");
        if (!m.Success)
        {
            m = Regex.Match(Providers.Digits(raw), @"(\d{1,2}):(\d{2})");
            return m.Success ? $"{int.Parse(m.Groups[1].Value):00}:{m.Groups[2].Value}" : "";
        }
        int h = int.Parse(m.Groups[1].Value);
        var pm = char.ToUpperInvariant(m.Groups[3].Value[0]) == 'P';
        if (pm && h < 12) h += 12;
        if (!pm && h == 12) h = 0;
        return $"{h:00}:{m.Groups[2].Value}";
    }

    static readonly Regex OperatorWords = new(
        @"bkash|rocket|nagad|upay|cellfin|okwallet|ok\s*wallet|ipay|dmoney|tap|ussd|online|web|bank|pos|বিকাশ|রকেট|নগদ|উপায়",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Header cells (&lt;th&gt;) of the recharge table, cleaned the same way data cells are.</summary>
    static List<string> HeaderCells(string html)
    {
        var table = Regex.Match(html, @"<table[^>]*bfont_post[^>]*>(.*?)</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var scope = table.Success ? table.Groups[1].Value : html;

        var cells = new List<string>();
        foreach (Match th in Regex.Matches(scope, @"<th[^>]*>(.*?)</th>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            cells.Add(Regex.Replace(Providers.Digits(WebUtility.HtmlDecode(Regex.Replace(th.Groups[1].Value, "<.*?>", " ", RegexOptions.Singleline))), @"\s+", " ").Trim());
        return cells;
    }

    static int FindHeader(List<string> headers, params string[] words)
    {
        for (int i = 0; i < headers.Count; i++)
            foreach (var w in words)
                if (headers[i].Contains(w, StringComparison.OrdinalIgnoreCase))
                    return i;
        return -1;
    }

    /// <summary>
    /// No usable amount header: pick the money cell ourselves. The gross amount
    /// is often a PLAIN INTEGER (500) while its components carry decimals
    /// (478.56, 354.36) — the old decimals-first rule therefore returned the
    /// energy amount, not what the customer paid.
    /// </summary>
    static double GuessAmount(List<string> row, int dateIdx)
    {
        double best = 0;
        for (int i = 1; i < row.Count; i++)   // index 0 is the serial number
        {
            if (i == dateIdx) continue;
            var cell = row[i].Replace(",", "").Trim();
            if (!Regex.IsMatch(cell, @"^\d{1,6}(?:\.\d{1,2})?$")) continue;
            var v = Providers.Num(cell);
            if (v is >= 10 and <= 200000) best = Math.Max(best, v);
        }
        return best;
    }

    static IEnumerable<List<string>> TableRows(string html)
    {
        var table = Regex.Match(html, @"<table[^>]*bfont_post[^>]*>(.*?)</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var scope = table.Success ? table.Groups[1].Value : html;

        foreach (Match tr in Regex.Matches(scope, @"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var cells = new List<string>();
            foreach (Match td in Regex.Matches(tr.Groups[1].Value, @"<td[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                cells.Add(Regex.Replace(Providers.Digits(WebUtility.HtmlDecode(Regex.Replace(td.Groups[1].Value, "<.*?>", " ", RegexOptions.Singleline))), @"\s+", " ").Trim());
            if (cells.Count > 0) yield return cells;
        }
    }

    /// <summary>"01-JAN-2025 11:00 AM" → "2025-01-01".</summary>
    static string? ParseNescoDate(string raw)
    {
        var m = Regex.Match(Providers.Digits(raw), @"(\d{1,2})-([A-Za-z]{3})-(\d{4})");
        if (!m.Success) return null;
        var mo = MonthNumber(m.Groups[2].Value);
        return mo == 0 ? null : $"{int.Parse(m.Groups[3].Value):0000}-{mo:00}-{int.Parse(m.Groups[1].Value):00}";
    }

    static int MonthNumber(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, out var n) && n is >= 1 and <= 12) return n;
        var names = new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        var key = s.ToLowerInvariant();
        for (int i = 0; i < 12; i++) if (key.StartsWith(names[i])) return i + 1;
        return 0;
    }
}