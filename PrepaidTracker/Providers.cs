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
    // DESCO's certificate chain is unreliable. Nothing secret is sent — only a
    // meter number — so a TLS hiccup shouldn't take the whole app down. But the
    // exception is scoped to DESCO's own hosts: this handler also carries NESCO's
    // POSTs, and accepting anybody's bad certificate because one utility
    // misconfigured theirs is a different decision, and not one this app needs.
    static readonly HttpClient Http = new(new SocketsHttpHandler
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
// DESCO — unauthenticated JSON API.
// getBalance / getCustomerInfo / getRechargeHistory / getCustomerMonthlyConsumption
// are confirmed. Daily BDT consumption exists too (it's what powers a 7-day chart
// on day one) but the endpoint name isn't documented publicly, so we probe a short
// list of candidates and remember whichever answers.
// ---------------------------------------------------------------------------
public class DescoProvider : IProvider
{
    const string Tkdes = "https://prepaid.desco.org.bd/api/tkdes/customer";
    const string Unified = "https://prepaid.desco.org.bd/api/unified/customer";

    static readonly string[] BalanceEndpoints = { $"{Tkdes}/getBalance", $"{Unified}/getBalance" };
    static readonly string[] InfoEndpoints = { $"{Tkdes}/getCustomerInfo", $"{Unified}/getCustomerInfo" };

    static readonly string[] RechargeEndpoints =
    {
        $"{Tkdes}/getRechargeHistory",
        $"{Unified}/getRechargeHistory",
        $"{Tkdes}/getCustomerRechargeHistory"
    };

    static readonly string[] MonthlyEndpoints =
    {
        $"{Tkdes}/getCustomerMonthlyConsumption",
        $"{Unified}/getCustomerMonthlyConsumption",
        $"{Tkdes}/getMonthlyConsumption"
    };

    static readonly string[] DailyEndpoints =
    {
        $"{Tkdes}/getCustomerDailyConsumption",
        $"{Unified}/getCustomerDailyConsumption",
        $"{Tkdes}/getCustomerDailyConsumptionInfo",
        $"{Tkdes}/getDailyConsumption",
        $"{Unified}/getDailyConsumption"
    };

    static string? _knownBalance, _knownRecharge, _knownMonthly, _knownDaily;
    static bool _warmed;   // portal page fetched once this run, for cookies + reachability

    /// <summary>What answered last sync, for Profile → Diagnostics.</summary>
    public static string BalanceEndpointUsed => Short(_knownBalance);
    public static string RechargeEndpointUsed => Short(_knownRecharge);
    public static string MonthlyEndpointUsed => Short(_knownMonthly);
    public static string DailyEndpointUsed => Short(_knownDaily);
    public static string ProbeLog { get; private set; } = "";
    static string Short(string? url) => url == null ? "none" : url[(url.LastIndexOf('/') + 1)..];

    readonly HttpClient _http;
    readonly List<string> _log = new();
    public DescoProvider(HttpClient http) => _http = http;

    public async Task<FetchResult> FetchAsync(string account, CancellationToken ct = default)
    {
        var acct = Uri.EscapeDataString(account);
        var snap = new Snapshot { SyncedAt = DateTime.Now };
        _log.Clear();

        // Load the portal page once per run: it sets any cookies the API expects,
        // and its status code tells us immediately if DESCO is unreachable or
        // blocking us outright.
        if (!_warmed)
        {
            try
            {
                using var res = await _http.GetAsync("https://prepaid.desco.org.bd/customer/", ct);
                Log("warmup", $"HTTP {(int)res.StatusCode}");
            }
            catch (Exception e) { Log("warmup", $"{e.GetType().Name}: {Snip(e.InnerException?.Message ?? e.Message)}"); }
            _warmed = true;
        }

        // ---- customer info FIRST: it answers with accountNo alone and gives us
        //      the meterNo, which the unified endpoints need alongside accountNo.
        //      (The official portal always sends both.) The portal also accepts
        //      logging in BY meter number, so if accountNo finds nothing, retry
        //      treating what the user typed as a meter number. ----
        var meterNo = "";
        var infoQueries = new[] { $"accountNo={acct}", $"meterNo={acct}" };
        foreach (var q in infoQueries)
        {
            foreach (var ep in Order(InfoEndpoints, null))
            {
                var info = await TryGet($"{ep}?{q}", ct);
                if (info is not { ValueKind: JsonValueKind.Object }) { Log(ep, "no info"); continue; }
                snap.CustomerName = Str(info.Value, "customerName");
                snap.Address = Str(info.Value, "installationAddress");
                meterNo = FirstString(info.Value, "meterNo", "meterNumber");
                var accountFound = FirstString(info.Value, "accountNo", "accountNumber");
                if (accountFound.Length > 0) acct = Uri.EscapeDataString(accountFound);
                if (snap.CustomerName.Length > 0 || meterNo.Length > 0) { Log(ep, "ok"); break; }
            }
            if (snap.CustomerName.Length > 0 || meterNo.Length > 0) break;
        }
        var pair = meterNo.Length > 0
            ? $"accountNo={acct}&meterNo={Uri.EscapeDataString(meterNo)}"
            : $"accountNo={acct}";

        // ---- balance ----
        // A balance of exactly 0.00 (or negative, during friendly hours) is a
        // valid answer — accept any response that HAS a balance-ish field. And
        // crucially: DESCO's unified (smart-meter) accounts often return valid
        // JSON with NO balance at all — their own portal shows those accounts
        // without a remaining-balance figure. That's not a reason to throw away
        // the recharge history and consumption we CAN get, so a missing balance
        // no longer aborts the sync.
        var balanceNames = new[] { "balance", "remainingBalance", "currentBalance", "balanceAmount" };
        var balQueries = meterNo.Length > 0
            ? new[] { pair }
            : new[] { pair, $"meterNo={acct}" };
        JsonElement? bal = null;
        string? balField = null;
        foreach (var q in balQueries)
        {
            foreach (var ep in Order(BalanceEndpoints, _knownBalance))
            {
                bal = await TryGet($"{ep}?{q}", ct);
                if (bal is { ValueKind: JsonValueKind.Object })
                {
                    balField = balanceNames.FirstOrDefault(n => HasField(bal.Value, n));
                    if (balField != null) { _knownBalance = ep; Log(ep, "ok"); break; }
                    Log(ep, "no balance; keys: " + Snip(string.Join(",", FieldNames(bal.Value))));
                }
                else Log(ep, "no balance");
                bal = null;
            }
            if (bal != null) break;
        }

        if (bal != null && balField != null)
        {
            snap.Balance = Dbl(bal.Value, balField);
            snap.BalanceReported = true;
            if (snap.MeterNo.Length == 0) snap.MeterNo = Str(bal.Value, "meterNo");
            snap.CurrentMonthConsumption = Dbl(bal.Value, "currentMonthConsumption");
            snap.ReadingTime = ParseTime(Str(bal.Value, "readingTime"));
        }
        else
        {
            snap.Balance = 0;
            snap.BalanceReported = false;
        }
        if (snap.MeterNo.Length == 0) snap.MeterNo = meterNo;

        // ---- recharge history ----
        var from = DateTime.Now.AddDays(-365).ToString("yyyy-MM-dd");
        var to = DateTime.Now.ToString("yyyy-MM-dd");
        foreach (var ep in Order(RechargeEndpoints, _knownRecharge))
        {
            var rc = await TryGet($"{ep}?{pair}&dateFrom={from}&dateTo={to}", ct);
            var rows = AsArray(rc).ToList();
            if (rows.Count == 0) { Log(ep, "0 rows"); continue; }

            foreach (var r in rows)
            {
                var raw = FirstString(r, "rechargeDate", "date", "rechargeTime");
                if (raw.Length < 10) continue;
                var amount = FirstOf(r, "totalAmount", "rechargeAmount", "amount", "energyAmount");
                if (amount <= 0) continue;

                // DESCO reports a status too ("Execution Successful" / "Not Returnd" /
                // "Cancelled"); hard-coding Success=true made failed recharges look
                // fine in the app while the portal showed them failed.
                var status = FirstString(r, "rechargeStatus", "executionStatus", "orderStatus", "status");
                var success = status.Length == 0
                    || !Regex.IsMatch(status, @"fail|not\s*return|cancel|reject|revok|pending|unsuccess", RegexOptions.IgnoreCase);

                snap.Recharges.Add(new RechargeRow
                {
                    Date = raw[..10],
                    Time = raw.Length >= 16 ? raw.Substring(11, 5) : "",
                    Amount = amount,
                    EnergyAmount = FirstOf(r, "energyAmount", "energy"),
                    // No orderNo here: a 20-digit order number is not an operator.
                    Via = FirstString(r, "rechargeOperator", "operator", "channel", "rechargeBy").ToUpperInvariant(),
                    Success = success,
                    Details = AllFields(r)
                });
            }
            if (snap.Recharges.Count > 0) { _knownRecharge = ep; Log(ep, $"{snap.Recharges.Count} rows"); break; }
            Log(ep, "rows unparsed");
        }
        snap.Recharges = snap.Recharges.OrderBy(r => r.Date, StringComparer.Ordinal).ToList();

        // ---- daily consumption: what makes a 7-day chart possible immediately ----
        // The window ends YESTERDAY. DESCO posts a day's figure once the day closes, and
        // a working public dashboard that reads this same endpoint deliberately requests
        // yesterday-back-30 rather than including today. A today-ended range is kept as a
        // second attempt for meters that do serve it.
        var yesterday = DateTime.Now.AddDays(-1);
        string[] dayRanges =
        {
            $"dateFrom={yesterday.AddDays(-39):yyyy-MM-dd}&dateTo={yesterday:yyyy-MM-dd}",
            $"dateFrom={DateTime.Now.AddDays(-40):yyyy-MM-dd}&dateTo={to}"
        };

        foreach (var ep in Order(DailyEndpoints, _knownDaily))
        {
            foreach (var range in dayRanges)
            {
                var daily = await TryGet($"{ep}?{pair}&{range}", ct);
                var rows = AsArray(daily).ToList();
                if (rows.Count == 0) { Log(ep, $"0 rows (to {range[(range.LastIndexOf('=') + 1)..]})"); continue; }

                foreach (var d in rows)
                {
                    var date = FindDate(d);
                    if (date == null) continue;
                    snap.Days.Add(new DayRow
                    {
                        Date = date,
                        Taka = FirstOf(d, "consumedTaka", "consumedAmount", "amount", "taka"),
                        Units = FirstOf(d, "consumedUnit", "consumedUnits", "unit", "kwh")
                    });
                }
                if (snap.Days.Count > 0) { _knownDaily = ep; Log(ep, $"{snap.Days.Count} rows"); break; }
                snap.Days.Clear();
                Log(ep, "rows unparsed");
            }
            if (snap.Days.Count > 0) break;
        }
        snap.Days = snap.Days.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();

        // ---- monthly consumption ----
        // This endpoint does NOT take dateFrom/dateTo like the others — it takes
        // monthFrom/monthTo in yyyy-MM. Sending the wrong spelling returns an empty
        // payload, and the symptom is an account whose balance and recharge history
        // work perfectly while the usage chart stays blank: Months ends up empty, the
        // synthesis below adds a single current-month row, and one row is not history.
        // Documented spelling first, the old one second in case a variant wants it.
        var monthFrom = DateTime.Now.AddMonths(-11).ToString("yyyy-MM");
        var monthTo = DateTime.Now.ToString("yyyy-MM");
        string[] monthRanges =
        {
            $"monthFrom={monthFrom}&monthTo={monthTo}",
            $"dateFrom={monthFrom}&dateTo={monthTo}"
        };

        foreach (var ep in Order(MonthlyEndpoints, _knownMonthly))
        {
            foreach (var range in monthRanges)
            {
                var mc = await TryGet($"{ep}?{pair}&{range}", ct);
                var rows = AsArray(mc).ToList();
                if (rows.Count == 0) { Log(ep, $"0 rows ({range[..range.IndexOf('=')]})"); continue; }

                // Parse and validate BEFORE accepting the endpoint. Rows whose month
                // string is shorter than "yyyy-MM" are dropped downstream, and taking
                // the endpoint on raw row count alone left Months empty with no
                // candidates left to try.
                var parsed = rows.Select(m => new MonthRow
                {
                    Month = FirstString(m, "month", "monthName"),
                    Taka = FirstOf(m, "consumedTaka", "amount"),
                    Units = FirstOf(m, "consumedUnit", "unit")
                }).Where(m => m.Month.Length >= 7).ToList();

                if (parsed.Count == 0) { Log(ep, "rows unparsed"); continue; }

                snap.Months.AddRange(parsed);
                _knownMonthly = ep;
                Log(ep, $"{parsed.Count} rows ({range[..range.IndexOf('=')]})");
                break;
            }
            if (snap.Months.Count > 0) break;
        }
        snap.Months = snap.Months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList();

        // If getBalance surfaced a currentMonthConsumption but the monthly endpoint
        // didn't include this month, fold it in. Same authoritative source, different
        // endpoint — some meter types return the monthly list without the running
        // month appended yet. Without this synthesis, an account whose daily feed is
        // discarded AND whose monthly list lags a month sees an empty Usage tab
        // (recharge history still shows, but no chart), when we already have the one
        // number that matters. Ordering stays sorted so the yearly bar chart draws
        // this month at the right end of the strip.
        if (snap.CurrentMonthConsumption > 0)
        {
            var thisMonth = DateTime.Now.ToString("yyyy-MM");
            if (!snap.Months.Any(m => m.Month == thisMonth))
            {
                snap.Months.Add(new MonthRow
                {
                    Month = thisMonth,
                    Taka = snap.CurrentMonthConsumption,
                    Estimated = true          // month-to-date, not a finished month
                });
                snap.Months = snap.Months.OrderBy(m => m.Month, StringComparer.Ordinal).ToList();
            }
        }

        // ---- daily-feed sanity check ----
        // The daily-consumption endpoint isn't publicly documented; we probe candidates
        // and accept whatever answers with a date and a "consumedTaka"-shaped number.
        // On some meter types the endpoint that finally answers is not returning per-day
        // Taka increments at all — the values may be cumulative-from-month-start, in
        // kWh, or on some other scale. When that happens every downstream figure (daily
        // average, 7-day chart, days-left, live estimate, month-end projection) inherits
        // the error while MonthSpent — which comes from getBalance's authoritative
        // currentMonthConsumption — stays right. The card then shows two mutually
        // contradictory numbers ("Spent this month ৳549" over a 7-day chart whose bars
        // average ৳314/day, i.e. more per day than the whole month claims to have cost).
        //
        // Cross-check this-month daily-row sum against currentMonthConsumption, and also
        // against the current-month row in the monthly feed when we have it. Discard the
        // daily rows on a large OVER-report — that's the scale-mismatch signature.
        // Under-reporting is legitimate (a lagging feed, a missing day or two) and the
        // EWMA over the days we DO have is still meaningful, so we leave that case alone.
        // When we discard, Forecast.Build falls through to its monthly-total path — the
        // same one NESCO uses, which is known to compute correctly.
        if (snap.Days.Count > 0)
        {
            var thisMonth = DateTime.Now.ToString("yyyy-MM");
            var monthSum = snap.Days.Where(d => d.Date.StartsWith(thisMonth)).Sum(d => d.Taka);
            var monthlyRowTaka = snap.Months.FirstOrDefault(m => m.Month == thisMonth)?.Taka ?? 0;
            var reference = snap.CurrentMonthConsumption > 0 ? snap.CurrentMonthConsumption : monthlyRowTaka;
            if (reference > 0 && monthSum > reference * 2)
            {
                Log("daily-sanity",
                    $"sum ৳{monthSum:0} > 2× ref ৳{reference:0} — daily feed discarded");
                snap.Days.Clear();
            }
        }

        ProbeLog = string.Join("\n", _log);

        // Only fail when DESCO gave us nothing whatsoever. A missing balance with
        // a working recharge history is a partial success, not an error.
        if (!snap.BalanceReported && snap.CustomerName.Length == 0
            && snap.Recharges.Count == 0 && snap.Days.Count == 0 && snap.Months.Count == 0)
            return new FetchResult { Error = "No data returned.\n\n" + ProbeLog };

        return new FetchResult { Ok = true, Snapshot = snap };
    }

    void Log(string endpoint, string outcome) => _log.Add($"{Short(endpoint),-32} {outcome}");

    // Try whatever worked last time first.
    static IEnumerable<string> Order(string[] all, string? known) =>
        known == null ? all : new[] { known }.Concat(all.Where(x => x != known));

    async Task<JsonElement?> TryGet(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Look exactly like the Angular portal's own XHR calls.
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.Referrer = new Uri("https://prepaid.desco.org.bd/customer/");
            req.Headers.TryAddWithoutValidation("Origin", "https://prepaid.desco.org.bd");

            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                Log(url, $"HTTP {(int)res.StatusCode} {Snip(body)}");
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                // Most endpoints wrap the payload in { "data": ... }; a few return it bare.
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    if (data.ValueKind == JsonValueKind.Null)
                    {
                        // Surface the API's own explanation, e.g. { code:404, desc:"..." }.
                        var why = FirstString(doc.RootElement, "desc", "message", "msg", "code");
                        Log(url, "data:null" + (why.Length > 0 ? " " + Snip(why) : ""));
                        return null;
                    }
                    return data.Clone();
                }
                return doc.RootElement.Clone();
            }
            catch
            {
                Log(url, $"not JSON: {Snip(body)}");   // e.g. a WAF/captcha HTML page
                return null;
            }
        }
        catch (Exception e)
        {
            // DNS, TLS, timeout — name it instead of hiding it.
            Log(url, $"{e.GetType().Name}: {Snip(e.InnerException?.Message ?? e.Message)}");
            return null;
        }
    }

    /// <summary>One line, 60 chars, no tags — enough to recognise an error page.</summary>
    static string Snip(string? s)
    {
        s = Regex.Replace(s ?? "", @"<[^>]*>|\s+", " ").Trim();
        return s.Length > 60 ? s[..60] + "…" : s;
    }

    /// <summary>
    /// The rows we want are sometimes the `data` array itself, and sometimes an array
    /// nested one level inside it. Don't assume — go and find it.
    /// </summary>
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

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ToString() ?? "" : "";

    static double Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble() : Providers.Num(v.ToString())
            : 0;

    /// <summary>True if the object has a non-null property with this name (case-insensitive).</summary>
    static bool HasField(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                && p.Value.ValueKind != JsonValueKind.Null)
                return true;
        return false;
    }

    /// <summary>Property names of a JSON object, for the probe log.</summary>
    static IEnumerable<string> FieldNames(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in e.EnumerateObject()) yield return p.Name;
    }

    /// <summary>Every non-empty field of a JSON object, prettified for the detail modal.</summary>
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

    /// <summary>Field names differ between DESCO's endpoints; take the first present, case-insensitively.</summary>
    static double FirstOf(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        foreach (var n in names)
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble() : Providers.Num(p.Value.ToString());
        return 0;
    }

    static string FirstString(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return "";
        foreach (var n in names)
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null)
                    return p.Value.ToString() ?? "";
        return "";
    }

    /// <summary>Any property whose value starts with a yyyy-MM-dd date.</summary>
    static string? FindDate(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
        {
            var v = p.Value.ToString();
            if (v is { Length: >= 10 } && Regex.IsMatch(v[..10], @"^\d{4}-\d{2}-\d{2}$"))
                return v[..10];
        }
        return null;
    }

    static DateTime? ParseTime(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

// ---------------------------------------------------------------------------
// NESCO — no API. The portal is a Laravel form: GET for the CSRF token, then
// POST {_token, cust_no, submit}, where `submit` is a Bengali label choosing
// which table comes back. No login, no captcha.
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
            snap.Address      = Pick(fields, 3, "ঠিকানা", "address");
            snap.MeterNo      = Pick(fields, 8, "মিটার", "meter");

            // Numbers the portal actually publishes about this meter. Both are
            // OPTIONAL: if NESCO doesn't render the field, these stay null and the
            // UI stays quiet about them. Never substitute a guess.
            snap.MinRecharge      = PickNum(fields, "মিনিমাম রিচার্জ", "সর্বনিম্ন রিচার্জ", "minimum recharge", "min recharge");
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
            // Matching raw HTML was too brittle: the caption is wrapped in its own
            // element, so any <span>/<br>/&nbsp; between "সময়ঃ" and the date broke
            // it silently. Flatten the page first, then read it.
            snap.ReadingTime = ExtractReadingTime(rechargeHtml);

            // The portal's recharge table has shipped with 12, 13 and 14+ column
            // variants, so hard-coded indices silently produce zero rows. Find the
            // columns instead: headers first, cell content as the fallback.
            var headers = HeaderCells(rechargeHtml);
            // The portal's actual headers: gross is "রিচার্জের পরিমাণ (টাকা)",
            // energy is "বিদ্যুৎ (টাকা)". Column order puts "বিদ্যুৎ (টাকা)"
            // before "সম্ভাব্য বিদ্যুৎ এর পরিমাণ", so first-match is the right one.
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
            ["_token"] = token, ["cust_no"] = account, ["submit"] = submit
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
    /// 12:00:00 AM)" caption under the balance box. This is the only honest
    /// freshness signal the portal gives: NESCO's figure moves once a day, so our
    /// sync clock says nothing about whether the money on screen is current.
    ///
    /// Two traps, both of which silently returned null before:
    ///   1. UNICODE. "সময়" can be encoded two ways — য়=U+09DF precomposed, or
    ///      য+় = U+09AF U+09BC decomposed. They render identically and compare
    ///      UNEQUAL. The portal serves one; a regex literal holds the other. Both
    ///      sides are normalised to FormD here so the bytes actually line up.
    ///   2. MARKUP. The caption is its own element, so tags between the keyword
    ///      and the date broke a raw-HTML match. We flatten first.
    /// If the keyword can't be found at all, fall back to the caption's distinctive
    /// SPACE-separated date shape ("11 July 2026"). The install-date field nearby
    /// is dash-separated ("20-Feb-2025"), so it can't be mistaken for this one.
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
    /// First field whose label mentions any of these words. If the portal drops its
    /// labels, fall back to a known position: index >= 0 counts from the start,
    /// -1 means the last field (where the balance lives).
    /// </summary>
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
    /// energy amount, not what the customer paid. Consider both in one pass:
    /// serials and seq numbers are tiny, consumer/meter numbers and tokens are
    /// 8+ digits, so a 2–6 digit value in the money range wins by magnitude.
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
