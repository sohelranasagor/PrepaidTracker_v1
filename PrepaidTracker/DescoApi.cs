using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrepaidTracker;

internal static class Wire
{
    public const string Base = "https://prepaid.desco.org.bd";

    // =====================================================================
    // CONFIRMED by tools/DescoProbe against account 41392222, 2026-11-09.
    // =====================================================================

    // Answers 204 and sets the XSRF-TOKEN cookie. Note it is NOT under /api —
    // /api/csrf exists but 401s, so the obvious guess is the wrong one.
    public const string PathCsrf = "/csrf";

    // /api/login also exists and also 401s, with AUTHENTICATION_REQUIRED. It is a
    // different endpoint entirely. The real one is under /auth and answers
    // {"authenticated":true,"userId":…,"mustChangePassword":false}.
    public const string PathLogin = "/api/auth/login";

    public const string CsrfCookie = "XSRF-TOKEN";
    // Spring Boot's default pairing for an XSRF-TOKEN cookie. The 404 bodies give the
    // framework away (timestamp / status / error / path), and this header was accepted.
    public const string CsrfHeader = "X-XSRF-TOKEN";

    public const string FieldUser = "username";
    public const string FieldPass = "password";

    // 200. Returns { accounts: [ { accountId, accountNo, meterNo, status, … } ], … }.
    // Also doubles as the session probe, because /api/session does not exist here.
    public const string PathAccounts = "/api/accounts";

    // Every data endpoint is scoped to the INTERNAL accountId (15539), not the account
    // number the customer knows (41392222). Passing the account number returns 403 on
    // every leaf, so resolving the id through /api/accounts first is mandatory rather
    // than an optimisation. It is also why the DevTools Network tab made the paths look
    // top-level — it only renders the last segment.
    public static string Acct(long accountId) => $"/api/accounts/{accountId}";

    // 200 (GET). customerName, installationAddress, meterNo, sanctionLoad ("4"),
    // phaseType, tariffSolution, meterModel, installationDate.
    //
    // It carries NO month-to-date figure. The old API's currentMonthConsumption has no
    // equivalent here, so Snapshot.CurrentMonthConsumption stays 0.
    public const string LeafSummary = "/summary";

    // 200 (GET). orderId, rechargeDate, totalAmount, energyAmount, demandCharge, token,
    // orderStatus ("Execution Successful" / "Not Returnd").
    public const string LeafTxns = "/transactions";   // ?dateFrom=yyyy-MM-dd&dateTo=yyyy-MM-dd

    // POST ONLY — every GET returns 500, which is what made this look broken for a day.
    //
    // And the verb is the whole design: this endpoint is a COMMAND that asks the meter
    // for a fresh reading, not a query against a cache. There is no free variant. So it
    // must be called at most once a day, on explicit user action — an app that called it
    // on every open would burn the allowance before the user ever pressed refresh.
    //
    // A refusal answers 200 with:
    //   {"status":"DAILY_LIMIT_REACHED","meterNo":"…","source":"REALTIME",
    //    "nextEligibleAt":"2026-11-09T18:00:00Z","rateLimitReason":"ALREADY_SUCCEEDED_TODAY"}
    //
    // ALREADY_SUCCEEDED_TODAY means today's read already happened, so whatever balance
    // the app holds IS current — the refusal is not an error and must not blank it.
    //
    // UNVERIFIED: the shape of a SUCCESSFUL response. Today's read was already spent
    // when probing, so only the refusal has been seen. The balance field name in
    // DescoProvider is a guess with fallbacks until a probe run after midnight Dhaka.
    public const string LeafRealtime = "/realtime-balance";

    // =====================================================================
    // KNOWN BROKEN / ABSENT
    // =====================================================================

    // 500 at every prefix. Would be a cleaner source for Reconcile's STS tokens than
    // digging them out of the transaction rows, if it ever starts answering.
    public const string LeafTokens = "/tokens";

    // No monthly endpoint found: /monthly, /monthly-consumption, /consumption and
    // /usage all 404. The portal's own dashboard says "No monthly data yet" for this
    // account, so it may genuinely not exist for UNIFIED meters.
    //
    // No daily endpoint either: /daily, /daily-consumption and /consumption/daily all
    // 404. Snapshot.Days and Snapshot.Months therefore stay empty, and Forecast falls
    // through to its own-readings path — which needs three distinct meter reads before
    // the first chart bar appears. At one read a day, that is three days.

    public static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(5);
}

public enum SessionState { Ok, NeedsLogin, Unreachable, Misconfigured }

/// <summary>
/// The authenticated DESCO session. One instance per ACCOUNT NUMBER — not per meter id —
/// because the portal's login owns an account, and two Meter rows for the same account
/// (which Store.DedupeMeters exists to clean up) must share one session rather than
/// racing two logins against each other.
/// </summary>
public sealed class DescoApi : IDisposable
{
    static readonly ConcurrentDictionary<string, DescoApi> _live = new();
    static readonly Uri Origin = new(Wire.Base);

    public static DescoApi For(string account) => _live.GetOrAdd(account, a => new DescoApi(a));

    /// <summary>
    /// Replace the instance after a session death. The old one is disposed on a delay,
    /// so a concurrent caller still inside a using-block on its response can finish
    /// rather than hitting a disposed handler.
    /// </summary>
    static DescoApi Recycle(string account)
    {
        if (_live.TryRemove(account, out var old))
            _ = Task.Delay(TimeSpan.FromSeconds(30))
                    .ContinueWith(_ => { try { old.Dispose(); } catch { } },
                                  CancellationToken.None,
                                  TaskContinuationOptions.None,
                                  TaskScheduler.Default);
        return _live.GetOrAdd(account, a => new DescoApi(a));
    }

    readonly string _account;
    readonly CookieContainer _jar = new();
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);

    DateTimeOffset _goodUntil = DateTimeOffset.MinValue;
    bool _jarLoaded;
    string _jarPrint = "";
    long _accountId;

    /// <summary>
    /// The outcome of the last session attempt, for callers that need a reason after a
    /// helper returned a plain null (AccountIdAsync, chiefly).
    /// </summary>
    public SessionState LastKnownState { get; private set; } = SessionState.Ok;

    DescoApi(string account)
    {
        _account = account;
        // Own handler, because this one needs its own cookie jar per account — but the
        // SAME host-scoped certificate rule as Providers.Http. Disabling validation
        // outright would be a real downgrade: this connection carries a PASSWORD now,
        // not just a meter number.
        var h = new SocketsHttpHandler
        {
            CookieContainer = _jar,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                {
                    if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                    var host = (sender as HttpRequestMessage)?.RequestUri?.Host
                               ?? sender as string ?? "";
                    return host.EndsWith("desco.org.bd", StringComparison.OrdinalIgnoreCase);
                }
            }
        };
        _http = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
    }

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }

    // ---- keys. All keyed on the ACCOUNT NUMBER. ----

    static string KUser(string a) => $"desco-user-{a}";
    static string KPass(string a) => $"desco-pass-{a}";
    static string KJar(string a) => $"desco-jar-{a}";
    static string KHas(string a) => $"desco-has-{a}";
    static string KDiag(string a) => $"desco-auth-{a}";

    public static bool HasCredential(string account) =>
        Preferences.Get(KHas(account), false);

    // ---- cookie jar, in SecureStorage ----

    string JarFingerprint() =>
        string.Join('|', _jar.GetCookies(Origin).Cast<Cookie>()
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Name + '=' + c.Value));

    record CookieDto(string Name, string Value, string Path, string Domain, DateTimeOffset Expires);

    async Task LoadJarAsync()
    {
        if (_jarLoaded) return;
        _jarLoaded = true;
        try
        {
            var raw = await SecureStorage.GetAsync(KJar(_account));
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var c in JsonSerializer.Deserialize<List<CookieDto>>(raw!) ?? new())
                if (c.Expires > DateTimeOffset.UtcNow)
                    _jar.Add(Origin, new Cookie(c.Name, c.Value, c.Path, c.Domain)
                    { Expires = c.Expires.UtcDateTime });
            _jarPrint = JarFingerprint();
        }
        catch { try { SecureStorage.Remove(KJar(_account)); } catch { } }
    }

    /// <summary>
    /// Only writes when a cookie actually changed. A three-endpoint sync would otherwise
    /// do three Keychain writes, each of which can throw on a locked device.
    /// </summary>
    async Task SaveJarAsync()
    {
        var now = JarFingerprint();
        if (now == _jarPrint) return;
        try
        {
            var list = _jar.GetCookies(Origin).Cast<Cookie>()
                .Select(c => new CookieDto(c.Name, c.Value, c.Path, c.Domain,
                    c.Expires == default
                        ? DateTimeOffset.UtcNow.AddDays(14)
                        : new DateTimeOffset(c.Expires.ToUniversalTime(), TimeSpan.Zero)))
                .ToList();
            await SecureStorage.SetAsync(KJar(_account), JsonSerializer.Serialize(list));
            _jarPrint = now;
        }
        catch { /* locked device: the session is still valid in-process */ }
    }

    string? Csrf() => _jar.GetCookies(Origin)[Wire.CsrfCookie]?.Value;

    // ---- session ----

    async Task<SessionState> EnsureAsync(CancellationToken ct)
    {
        if (_goodUntil > DateTimeOffset.UtcNow) return SessionState.Ok;

        await _gate.WaitAsync(ct);
        try
        {
            if (_goodUntil > DateTimeOffset.UtcNow) return SessionState.Ok;
            await LoadJarAsync();

            if (await ProbeAsync(ct))
            {
                _goodUntil = DateTimeOffset.UtcNow + Wire.SessionTtl;
                return LastKnownState = SessionState.Ok;
            }

            string? u = null, p = null;
            var storageOk = true;
            try
            {
                u = await SecureStorage.GetAsync(KUser(_account));
                p = await SecureStorage.GetAsync(KPass(_account));
            }
            catch { storageOk = false; }

            if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p))
                // A credential that EXISTS but can't be read — a locked Keychain during
                // a background wake — must not prompt a re-login. Fail open: the balance
                // ages quietly until the next foreground sync, which is far better than
                // a spurious "sign in again" at 3am for a session that was never broken.
                return LastKnownState = (!storageOk || HasCredential(_account))
                    ? SessionState.Unreachable
                    : SessionState.NeedsLogin;

            var st = await LoginCoreAsync(u!, p!, ct);
            if (st == SessionState.Ok) _goodUntil = DateTimeOffset.UtcNow + Wire.SessionTtl;
            return st;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Is the session still alive? /api/session does NOT exist on this portal (404), so
    /// /api/accounts is the probe: it is the cheapest authenticated call, and we need
    /// its payload anyway to resolve the accountId.
    /// </summary>
    async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var r = await _http.GetAsync(Wire.Base + Wire.PathAccounts, ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Called from the sign-in screen. The only place credentials are banked.</summary>
    public static async Task<SessionState> SignInAsync(
        string account, string user, string pass, CancellationToken ct)
    {
        var api = Recycle(account);                 // fresh jar for a fresh identity
        var st = await api.LoginCoreAsync(user, pass, ct);
        if (st != SessionState.Ok) return st;

        await SecureStorage.SetAsync(KUser(account), user);
        await SecureStorage.SetAsync(KPass(account), pass);
        Preferences.Set(KHas(account), true);
        api._goodUntil = DateTimeOffset.UtcNow + Wire.SessionTtl;
        return st;
    }

    public static void SignOut(string account)
    {
        foreach (var k in new[] { KUser(account), KPass(account), KJar(account) })
            try { SecureStorage.Remove(k); } catch { }
        Preferences.Remove(KHas(account));
        Preferences.Remove(KDiag(account));
        if (_live.TryRemove(account, out var api)) api.Dispose();
    }

    async Task<SessionState> LoginCoreAsync(string user, string pass, CancellationToken ct)
    {
        try
        {
            await LoadJarAsync();

            // Prime the CSRF cookie. This answers 204 with no body — the cookie IS the
            // payload, Spring's double-submit pattern.
            using (await _http.GetAsync(Wire.Base + Wire.PathCsrf, ct)) { }

            var csrf = Csrf();
            if (csrf is null) return Diag(SessionState.Misconfigured, "no csrf cookie");

            var body = new JsonObject { [Wire.FieldUser] = user, [Wire.FieldPass] = pass }.ToJsonString();
            using var req = new HttpRequestMessage(HttpMethod.Post, Wire.Base + Wire.PathLogin)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation(Wire.CsrfHeader, Uri.UnescapeDataString(csrf));
            req.Headers.Referrer = new Uri(Wire.Base + "/login");

            using var res = await _http.SendAsync(req, ct);

            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Diag(SessionState.NeedsLogin, "rejected");
            // Any other 4xx is OUR bug, not the user's: a wrong path or wrong field
            // names. Separating it matters because NeedsLogin prompts for a password the
            // user will retype correctly and still fail, forever.
            if ((int)res.StatusCode is >= 400 and < 500)
                return Diag(SessionState.Misconfigured, $"HTTP {(int)res.StatusCode}");
            if (!res.IsSuccessStatusCode)
                return Diag(SessionState.Unreachable, $"HTTP {(int)res.StatusCode}");

            // A 200 that says authenticated:false would be a silent failure — a
            // cookie-based flow has no other way to tell us.
            try
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                var node = JsonNode.Parse(text);
                if (node?["authenticated"]?.GetValue<bool>() == false)
                    return Diag(SessionState.NeedsLogin, "authenticated:false");
                if (node?["mustChangePassword"]?.GetValue<bool>() == true)
                    // Logged in, but the portal will refuse data until the password is
                    // changed on their website. Nothing the app can do about it.
                    return Diag(SessionState.Misconfigured, "mustChangePassword");
            }
            catch { /* unparseable 200: treat the status as the answer */ }

            await SaveJarAsync();
            _accountId = 0;                       // new identity; re-resolve
            return Diag(SessionState.Ok, "ok");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Diag(SessionState.Unreachable, "timeout"); }
        catch (Exception e) { return Diag(SessionState.Unreachable, e.Message); }
    }

    // ---- the account id every data endpoint hangs off ----

    /// <summary>
    /// The portal's internal accountId for this account number, or 0 on failure.
    ///
    /// Mandatory, not an optimisation: /api/accounts/15539/summary works while
    /// /api/accounts/41392222/summary returns 403. Cached for the life of the instance —
    /// it is a database id and does not change — and cleared on a fresh login.
    /// </summary>
    public async Task<long> AccountIdAsync(CancellationToken ct)
    {
        if (_accountId > 0) return _accountId;

        var (node, st) = await GetAsync(Wire.PathAccounts, ct);
        LastKnownState = st;
        if (st != SessionState.Ok || node is null) return 0;

        if (node.Value.ValueKind == JsonValueKind.Object
            && node.Value.TryGetProperty("accounts", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                if (a.TryGetProperty("accountNo", out var no)
                    && string.Equals(no.GetString(), _account, StringComparison.OrdinalIgnoreCase)
                    && a.TryGetProperty("accountId", out var id))
                    return _accountId = id.GetInt64();
            }

            // One linked account and the number didn't match: the user may have typed a
            // meter number, or the portal may render the account number differently.
            // Taking the only account beats failing outright.
            if (arr.GetArrayLength() == 1 && arr[0].TryGetProperty("accountId", out var only))
                return _accountId = only.GetInt64();

            // Several accounts and none matched — this login doesn't own that number.
            // NOT a session problem; a re-login would change nothing.
            LastKnownState = SessionState.Misconfigured;
        }
        return 0;
    }

    // ---- authed GET, with exactly one re-login retry ----

    public async Task<(JsonElement? Node, SessionState State)> GetAsync(
        string path, CancellationToken ct, bool isRetry = false)
    {
        var st = await EnsureAsync(ct);
        if (st != SessionState.Ok) return (null, st);

        HttpResponseMessage res;
        try { res = await _http.GetAsync(Wire.Base + path, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (null, SessionState.Unreachable); }

        bool unauthorized;
        using (res)
        {
            unauthorized = res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            if (!unauthorized)
            {
                await SaveJarAsync();
                // A 404 or 500 is a broken or wrong-verb endpoint — not an auth failure.
                // Report Ok so one dead endpoint doesn't discard data another already
                // returned, and doesn't drag the user into a pointless re-login.
                if (!res.IsSuccessStatusCode) return (null, SessionState.Ok);
                try
                {
                    var text = await res.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(text);
                    // Some endpoints may wrap the payload in { "data": … }; the confirmed
                    // ones return it bare. Handle both.
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind != JsonValueKind.Null)
                        return (data.Clone(), SessionState.Ok);
                    return (doc.RootElement.Clone(), SessionState.Ok);
                }
                catch { return (null, SessionState.Ok); }
            }
        }
        // Outside the using — Recycle may dispose this instance.
        if (isRetry) return (null, SessionState.NeedsLogin);
        _goodUntil = DateTimeOffset.MinValue;
        try { SecureStorage.Remove(KJar(_account)); } catch { }
        return await Recycle(_account).GetAsync(path, ct, isRetry: true);
    }

    // ---- authed POST ----

    /// <summary>
    /// POST with the CSRF header. Used only for realtime-balance, which is a COMMAND —
    /// it asks the meter for a fresh reading — rather than a query. That is why it must
    /// never be called speculatively: each call either spends the day's single allowance
    /// or is refused with DAILY_LIMIT_REACHED.
    ///
    /// Note the retry rule is the same single re-login as GetAsync, and it is safe here
    /// for the same reason it would normally be dangerous on a POST: a refused call
    /// changes nothing, and a successful one is idempotent within the day — the second
    /// attempt simply reports ALREADY_SUCCEEDED_TODAY.
    /// </summary>
    public async Task<(JsonElement? Node, SessionState State)> PostAsync(
        string path, CancellationToken ct, bool isRetry = false)
    {
        var st = await EnsureAsync(ct);
        if (st != SessionState.Ok) return (null, st);

        HttpResponseMessage res;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Wire.Base + path)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (Csrf() is { } c)
                req.Headers.TryAddWithoutValidation(Wire.CsrfHeader, Uri.UnescapeDataString(c));
            req.Headers.Referrer = new Uri(Wire.Base + "/dashboard");
            res = await _http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (null, SessionState.Unreachable); }

        bool unauthorized;
        using (res)
        {
            unauthorized = res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            if (!unauthorized)
            {
                await SaveJarAsync();
                if (!res.IsSuccessStatusCode) return (null, SessionState.Ok);
                try
                {
                    var text = await res.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind != JsonValueKind.Null)
                        return (data.Clone(), SessionState.Ok);
                    return (doc.RootElement.Clone(), SessionState.Ok);
                }
                catch { return (null, SessionState.Ok); }
            }
        }
        if (isRetry) return (null, SessionState.NeedsLogin);
        _goodUntil = DateTimeOffset.MinValue;
        try { SecureStorage.Remove(KJar(_account)); } catch { }
        return await Recycle(_account).PostAsync(path, ct, isRetry: true);
    }

    /// <summary>Written to Preferences so ProfilePage.DiagText can read it on a device
    /// with no debugger. Keyed on the account, matching every other key here.</summary>
    SessionState Diag(SessionState s, string msg)
    {
        LastKnownState = s;
        Preferences.Set(KDiag(_account), $"{DateTime.Now:MM-dd HH:mm}  {s}  {msg}");
        return s;
    }
}