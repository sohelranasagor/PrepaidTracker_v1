using System.Net;
using System.Text;

// Usage: dotnet run -- <email-or-phone> <password>
var user = args.ElementAtOrDefault(0);
var pass = args.ElementAtOrDefault(1);

const string Base = "https://prepaid.desco.org.bd";
var origin = new Uri(Base);
var jar = new CookieContainer();
var handler = new SocketsHttpHandler
{
    CookieContainer = jar,
    UseCookies = true,
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
};
var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

Console.WriteLine("=== 1. find the CSRF path (looking for 204) ===");
string? csrfPath = null;
foreach (var p in new[] { "/api/csrf", "/api/v1/csrf", "/csrf", "/api/auth/csrf", "/api/security/csrf" })
{
    try
    {
        using var r = await http.GetAsync(Base + p);
        Console.WriteLine($"  {p,-24} {(int)r.StatusCode}");
        if ((int)r.StatusCode is 200 or 204) { csrfPath ??= p; }
    }
    catch (Exception e) { Console.WriteLine($"  {p,-24} ERR {e.Message}"); }
}

Console.WriteLine("\n=== 2. cookies now held ===");
foreach (Cookie c in jar.GetCookies(origin))
    Console.WriteLine($"  {c.Name} = {c.Value[..Math.Min(12, c.Value.Length)]}…  path={c.Path}");

if (user is null || pass is null) { Console.WriteLine("\nNo credentials given; stopping."); return; }

var csrf = jar.GetCookies(origin).Cast<Cookie>()
    .FirstOrDefault(c => c.Name.Contains("csrf", StringComparison.OrdinalIgnoreCase)
                      || c.Name.Contains("xsrf", StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"\nCSRF cookie detected: {csrf?.Name ?? "(none)"}");

Console.WriteLine("\n=== 3. try login shapes ===");
string[] paths  = { "/api/login", "/api/auth/login", "/api/v1/auth/login", "/login" };
string[][] pairs = { new[]{"username","password"}, new[]{"email","password"},
                     new[]{"identifier","password"}, new[]{"emailOrPhone","password"} };
string[] headers = { "X-XSRF-TOKEN", "X-CSRF-TOKEN", "X-CSRF-Token" };

foreach (var path in paths)
foreach (var fields in pairs)
{
    var body = $"{{\"{fields[0]}\":{Esc(user)},\"{fields[1]}\":{Esc(pass)}}}";
    using var req = new HttpRequestMessage(HttpMethod.Post, Base + path)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    if (csrf is not null)
        foreach (var h in headers)
            req.Headers.TryAddWithoutValidation(h, Uri.UnescapeDataString(csrf.Value));
    req.Headers.Referrer = new Uri(Base + "/login");

    try
    {
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        Console.WriteLine($"  {path,-22} {fields[0],-13} {(int)res.StatusCode}  {Trim(text)}");
        if (res.IsSuccessStatusCode)
        {
            Console.WriteLine("\n*** LOGIN OK ***");
            Console.WriteLine($"  PathCsrf   = \"{csrfPath}\"");
            Console.WriteLine($"  PathLogin  = \"{path}\"");
            Console.WriteLine($"  CsrfCookie = \"{csrf?.Name}\"");
            Console.WriteLine($"  FieldUser  = \"{fields[0]}\"");
            await Probe(http, Base);
            return;
        }
    }
    catch (Exception e) { Console.WriteLine($"  {path,-22} {fields[0],-13} ERR {e.Message}"); }
}

static string Esc(string s) => System.Text.Json.JsonSerializer.Serialize(s);
static string Trim(string s) => s.Length > 110 ? s[..110].ReplaceLineEndings(" ") + "…" : s.ReplaceLineEndings(" ");

static async Task Probe(HttpClient http, string b)
{
    Console.WriteLine("\n=== 4. data endpoints ===");
    foreach (var p in new[] { "/api/session", "/api/accounts", "/api/realtime-balance",
                              "/api/summary", "/api/daily", "/api/consumption/daily",
                              "/api/monthly?monthFrom=2026-05&monthTo=2026-11",
                              "/api/transactions?dateFrom=2026-05-08&dateTo=2026-11-09" })
    {
        try
        {
            using var r = await http.GetAsync(b + p);
            var t = await r.Content.ReadAsStringAsync();
            Console.WriteLine($"  {p,-52} {(int)r.StatusCode}  {Trim(t)}");
        }
        catch (Exception e) { Console.WriteLine($"  {p,-52} ERR {e.Message}"); }
    }
}