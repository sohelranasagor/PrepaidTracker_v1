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

        // Snapshot.BalanceReported DEFAULTS TO TRUE, and on this portal a balance has to
        // be earned: it arrives only from a successful realtime POST, or by carrying a
        // previous reading forward. Leaving the default in place reports a phantom
        // ৳0.00 — which reads as an empty meter, trips the low-balance banner, and would
        // fire a notification off a number that never existed. Exactly what this flag
        // exists to prevent.
        snap.BalanceReported = false;

        // ---- resolve the internal accountId -----------------------------------
        // Every data endpoint hangs off this, NOT off the account number the customer
        // knows: /api/accounts/15539/summary works, /api/accounts/41392222/summary
        // returns 403. So this lookup is mandatory and its failure is fatal to the sync.
        long id;
        try { id = await api.AccountIdAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { id = 0; }

        if (id <= 0)
        {
            // Either the session is gone or the account isn't linked to this login. Both
            // need the user; neither is retryable on its own.
            LastState = api.LastKnownState == SessionState.Ok
                ? SessionState.NeedsLogin
                : api.LastKnownState;
            Log("accounts", "no accountId for " + account);
            ProbeLog = string.Join("\n", _log);
            return new FetchResult
            {
                Error = LastState == SessionState.Misconfigured
                    ? L.S("authPortalChanged")
                    : L.S("authExpiredBody")
            };
        }

        var root = Wire.Acct(id);
        Log("accounts", $"accountId {id}");

        // ---- summary: the one endpoint that reliably answers --------------------
        // customerName, installationAddress, meterNo, sanctionLoad, phaseType, tariff.
        // This call decides the session verdict, because it is the first authenticated
        // data call and the only one guaranteed to work when the session is healthy.
        var (sum, st) = await api.GetAsync(root + Wire.LeafSummary, ct);
        if (st != SessionState.Ok)
        {
            LastState = st;
            Log("summary", st.ToString());
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

        if (sum is not null)
        {
            snap.CustomerName = FirstString(sum.Value, "customerName");
            snap.Address = FirstString(sum.Value, "installationAddress");
            snap.MeterNo = FirstString(sum.Value, "meterNo");
            // Reported as a STRING ("4"), not a number, so FirstOf's Providers.Num
            // fallback is what actually parses it.
            snap.SanctionLoadKw = FirstOf(sum.Value, "sanctionLoad", "sanctionedLoad");
            Log("summary", "ok");
        }
        else Log("summary", "no data");

        // ---- balance ------------------------------------------------------------
        // POST, not GET: this endpoint is a COMMAND that asks the meter for a fresh
        // reading, and every GET against it returns 500. There is no cached variant, so
        // calling it speculatively would burn the day's single allowance — which is
        // exactly what an app that syncs on every open would do.
        //
        // So: only on an explicit user refresh, and only when the stored snapshot says
        // we are past nextEligibleAt. Every other sync keeps the reading we already have,
        // which is correct — if today's read already succeeded, the stored figure IS
        // today's reading.
        var meterId = Store.Meters().FirstOrDefault(m =>
            string.Equals(m.Account.Trim(), account.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? "";
        var prior = meterId.Length > 0 ? Store.Snapshot(meterId) : null;

        // Carry the previous reading forward. A sync that doesn't ask for a new read
        // must not blank the balance the user is looking at.
        if (prior is { BalanceReported: true })
        {
            snap.Balance = prior.Balance;
            snap.BalanceReported = true;
            snap.ReadingTime = prior.ReadingTime;
        }
        // Carried separately from the balance: the quota applies even when we have never
        // managed to read one, and losing it would unlock the refresh button early.
        snap.NextEligible = prior?.NextEligible;

        var eligible = snap.NextEligible is not { } next || DateTime.Now >= next;

        if (UserRequestedFresh && eligible)
        {
            var (rt, rtState) = await api.PostAsync(root + Wire.LeafRealtime, ct);
            if (rtState != SessionState.Ok) LastState = rtState;

            if (rt is not null)
            {
                var row = rt.Value;
                var status = FirstString(row, "status");

                // Recorded whether the call succeeded or was refused — a refusal is the
                // ONLY place the portal tells us when we may ask again.
                if (ParseIso(FirstString(row, "nextEligibleAt")) is { } n) snap.NextEligible = n;

                // UNVERIFIED field name: every probe so far has hit the daily limit, so
                // a SUCCESSFUL response has never been seen. Three candidates, in order
                // of likelihood. If the balance stays blank after a successful refresh,
                // this is the line to fix — check Profile → Diagnostics, which logs the
                // status the portal returned.
                var balText = FirstString(row, "balance", "lastRealtimeBalance",
                                               "remainingBalance", "currentBalance");
                if (balText.Length > 0)
                {
                    snap.Balance = Providers.Num(balText);
                    snap.BalanceReported = true;
                    snap.ReadingTime = ParseIso(FirstString(row, "readingTime", "receivedAt",
                                                                    "lastRealtimeReceivedAt"))
                                           ?? DateTime.Now;
                    Log("realtime", "fresh read ok");
                }
                else
                {
                    // ALREADY_SUCCEEDED_TODAY means today's read has happened, so the
                    // figure we already hold IS current. Not an error.
                    Log("realtime", status.Length > 0 ? status : "200, no balance field");
                }
            }
            else Log("realtime", "POST failed");
        }
        else if (UserRequestedFresh)
            Log("realtime", "quota: not eligible until " + (snap.NextEligible?.ToString("g") ?? "?"));
        else
            Log("realtime", snap.BalanceReported ? "cached reading" : "no reading yet");

        // ---- transactions -------------------------------------------------------
        var from = DateTime.Now.AddDays(-365).ToString("yyyy-MM-dd");
        var to = DateTime.Now.ToString("yyyy-MM-dd");
        var (txn, _) = await api.GetAsync($"{root}{Wire.LeafTxns}?dateFrom={from}&dateTo={to}", ct);

        if (txn is not null)
        {
            foreach (var r in AsArray(txn))
            {
                var raw = FirstString(r, "rechargeDate", "date");
                if (raw.Length < 10) continue;

                var amount = FirstOf(r, "totalAmount", "amount");
                if (amount <= 0) continue;

                // The portal's own spelling, typo included: "Execution Successful" or
                // "Not Returnd". Anything that isn't the success string is treated as a
                // failure, which is the safe direction — a row wrongly called failed gets
                // settled by the next meter read, whereas one wrongly called successful
                // silently inflates the day's usage.
                var orderStatus = FirstString(r, "orderStatus", "status");
                var success = orderStatus.Equals("Execution Successful", StringComparison.OrdinalIgnoreCase);

                // A meter key-change arrives as ONE transaction carrying a SET of tokens
                // (twelve, under sequence "-3~9"). Reconcile must not settle that against
                // a single balance rise — see CreditState.Informational.
                var tokens = FirstString(r, "token")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                snap.Recharges.Add(new RechargeRow
                {
                    Date = raw[..10],
                    Time = raw.Length >= 16 ? raw.Substring(11, 5) : "",
                    Amount = amount,
                    // energyAmount already EXCLUDES the demand charge: ৳600 → ৳574.27
                    // with none, ৳400 → ৳214.85 with ৳168 of it. Verified across 24 rows.
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

        // ---- what this portal does NOT give us ----------------------------------
        // No monthly endpoint (/monthly, /monthly-consumption, /consumption, /usage all
        // 404) and no daily one either. Months and Days both stay empty, so Forecast
        // falls through to its own-readings path: the daily series is derived from
        // consecutive balance readings, which needs three distinct reads before the
        // first bar appears. At one read a day, that is three days.
        //
        // There is also no month-to-date figure anywhere in this API — the old
        // getBalance.currentMonthConsumption has no equivalent — so
        // CurrentMonthConsumption stays 0 and FillMonth computes the month from our own
        // rows rather than from an authoritative total.
        Log("monthly", "no endpoint on new portal");
        Log("daily", "no endpoint on new portal");

        ProbeLog = string.Join("\n", _log);

        // Only a total failure is an error. A missing balance with a working recharge
        // history is a partial success — it is most of what the Usage tab needs.
        if (!snap.BalanceReported && snap.Recharges.Count == 0 && snap.CustomerName.Length == 0)
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