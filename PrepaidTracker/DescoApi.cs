using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrepaidTracker;

internal static class Wire
{
    public const string Base = "https://prepaid.desco.org.bd";

    // === EVERY VALUE BELOW IS A GUESS — replace from tools/DescoProbe output ===
    public const string PathCsrf = "/api/csrf";
    public const string PathSession = "/api/session";
    public const string PathLogin = "/api/login";
    public const string PathRealtime = "/api/realtime-balance";
    public const string PathSummary = "/api/summary";
    public const string PathMonthly = "/api/monthly";
    public const string PathTxns = "/api/transactions";
    public const string CsrfCookie = "XSRF-TOKEN";
    public const string CsrfHeader = "X-XSRF-TOKEN";
    public const string FieldUser = "username";
    public const string FieldPass = "password";
    public const string RefreshQuery = "?refresh=true";
    // ==========================================================================

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
    /// so a concurrent caller still inside a using-block on its HttpResponseMessage can
    /// finish rather than hitting a disposed handler.
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
    /// Only writes when a cookie actually changed. A five-endpoint sync would otherwise
    /// do five Keychain writes, each of which can throw on a locked device.
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
                return SessionState.Ok;
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
                // A credential that EXISTS but can't be read — a locked Keychain during a
                // background wake — must not prompt a re-login. Fail open: the balance
                // ages quietly until the next foreground sync, which is far better than
                // a spurious "sign in again" at 3am for a session that was never broken.
                return (!storageOk || HasCredential(_account))
                    ? SessionState.Unreachable
                    : SessionState.NeedsLogin;

            var st = await LoginCoreAsync(u!, p!, ct);
            if (st == SessionState.Ok) _goodUntil = DateTimeOffset.UtcNow + Wire.SessionTtl;
            return st;
        }
        finally { _gate.Release(); }
    }

    async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var r = await _http.GetAsync(Wire.Base + Wire.PathSession, ct);
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

            // Prime the CSRF cookie. This endpoint answers 204 with no body — the cookie
            // IS the payload, double-submit style.
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
            // names. Separating it matters because NeedsLogin prompts for a password
            // the user will retype correctly and still fail, forever.
            if ((int)res.StatusCode is >= 400 and < 500)
                return Diag(SessionState.Misconfigured, $"HTTP {(int)res.StatusCode}");
            if (!res.IsSuccessStatusCode)
                return Diag(SessionState.Unreachable, $"HTTP {(int)res.StatusCode}");

            await SaveJarAsync();
            return Diag(SessionState.Ok, "ok");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Diag(SessionState.Unreachable, "timeout"); }
        catch (Exception e) { return Diag(SessionState.Unreachable, e.Message); }
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
                // A 404 is a broken endpoint — their own /balance does this on UNIFIED
                // meters — not an auth failure. Report Ok so one dead endpoint doesn't
                // discard a balance another endpoint already returned.
                if (!res.IsSuccessStatusCode) return (null, SessionState.Ok);
                try
                {
                    var text = await res.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(text);
                    // Most endpoints wrap the payload in { "data": … }; a few return it bare.
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

    /// <summary>Written to Preferences so ProfilePage.DiagText can read it on a device
    /// with no debugger. Keyed on the account, matching every other key here.</summary>
    SessionState Diag(SessionState s, string msg)
    {
        Preferences.Set(KDiag(_account), $"{DateTime.Now:MM-dd HH:mm}  {s}  {msg}");
        return s;
    }
}