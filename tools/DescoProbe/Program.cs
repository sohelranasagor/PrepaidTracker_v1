using System.Net;
using System.Text;

var user = args.ElementAtOrDefault(0);
var pass = args.ElementAtOrDefault(1);
if (user is null || pass is null) { Console.WriteLine("usage: dotnet run -- <user> <pass>"); return; }

const string Base = "https://prepaid.desco.org.bd";
var origin = new Uri(Base);
var jar = new CookieContainer();
var http = new HttpClient(new SocketsHttpHandler
{
    CookieContainer = jar,
    UseCookies = true,
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
})
{ Timeout = TimeSpan.FromSeconds(20) };

// ---- log in with the confirmed shape ----
using (await http.GetAsync(Base + "/csrf")) { }
var csrf = jar.GetCookies(origin)["XSRF-TOKEN"]!.Value;

var req = new HttpRequestMessage(HttpMethod.Post, Base + "/api/auth/login")
{ Content = new StringContent($"{{\"username\":{Esc(user)},\"password\":{Esc(pass)}}}", Encoding.UTF8, "application/json") };
req.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", Uri.UnescapeDataString(csrf));
req.Headers.Referrer = new Uri(Base + "/login");
using (var r = await http.SendAsync(req))
    Console.WriteLine($"login: {(int)r.StatusCode}  {Trim(await r.Content.ReadAsStringAsync())}\n");

// ---- the account list, in full ----
Console.WriteLine("=== /api/accounts (full) ===");
Console.WriteLine(await http.GetStringAsync(Base + "/api/accounts"));

// ---- pull the ids out and probe account-scoped paths ----
var body = await http.GetStringAsync(Base + "/api/accounts");
var accountId = Between(body, "\"accountId\":", ",")?.Trim();
var accountNo = Between(body, "\"accountNo\":\"", "\"");
var meterNo = Between(body, "\"meterNo\":\"", "\"");
Console.WriteLine($"\naccountId={accountId}  accountNo={accountNo}  meterNo={meterNo}\n");

var from = DateTime.Today.AddMonths(-6).ToString("yyyy-MM-dd");
var to = DateTime.Today.ToString("yyyy-MM-dd");
var mFrom = DateTime.Today.AddMonths(-6).ToString("yyyy-MM");
var mTo = DateTime.Today.ToString("yyyy-MM");

string[] leaves =
{
    "realtime-balance", "balance", "summary", "monthly?monthFrom=" + mFrom + "&monthTo=" + mTo,
    "transactions?dateFrom=" + from + "&dateTo=" + to, "daily?dateFrom=" + from + "&dateTo=" + to,
    "consumption/daily?dateFrom=" + from + "&dateTo=" + to, "tokens", "token-status-messages", "details"
};

string[] prefixes =
{
    $"/api/accounts/{accountId}/",
    $"/api/accounts/{accountNo}/",
    $"/api/meters/{meterNo}/",
    $"/api/customer/{accountId}/"
};

Console.WriteLine("=== account-scoped probe (looking for 200) ===");
foreach (var p in prefixes)
    foreach (var leaf in leaves)
    {
        var url = Base + p + leaf;
        try
        {
            using var res = await http.GetAsync(url);
            var text = await res.Content.ReadAsStringAsync();
            if ((int)res.StatusCode == 200)
                Console.WriteLine($"200  {p}{leaf}\n     {Trim(text, 400)}");
            else if ((int)res.StatusCode != 404)
                Console.WriteLine($"{(int)res.StatusCode}  {p}{leaf}");
        }
        catch (Exception e) { Console.WriteLine($"ERR  {p}{leaf}  {e.Message}"); }
    }

    Console.WriteLine("\n=== realtime-balance variants ===");
    var b = $"{Base}/api/accounts/{accountId}/";
    foreach (var (verb, leaf) in new[]
    {
        ("GET",  "realtime-balance"),
        ("POST", "realtime-balance"),
        ("GET",  $"realtime-balance?meterNo={meterNo}"),
        ("POST", $"realtime-balance?meterNo={meterNo}"),
        ("GET",  "balance"),
        ("GET",  "meters"),
        ("GET",  "monthly-consumption?monthFrom=2026-05&monthTo=2026-11"),
        ("GET",  "consumption?monthFrom=2026-05&monthTo=2026-11"),
        ("GET",  "usage?monthFrom=2026-05&monthTo=2026-11"),
        ("GET",  "daily-consumption?dateFrom=2026-11-01&dateTo=2026-11-08"),
        ("GET",  "token-status-messages"),
    })
    {
        try
        {
            var rq = new HttpRequestMessage(new HttpMethod(verb), b + leaf);
            rq.Headers.TryAddWithoutValidation("X-XSRF-TOKEN",
                Uri.UnescapeDataString(jar.GetCookies(origin)["XSRF-TOKEN"]!.Value));
            rq.Headers.Referrer = new Uri(Base + "/dashboard");
            if (verb == "POST") rq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var rs = await http.SendAsync(rq);
            Console.WriteLine($"{(int)rs.StatusCode,3} {verb,-4} {leaf}");
            if ((int)rs.StatusCode is 200 or 400 or 429)
                Console.WriteLine($"     {Trim(await rs.Content.ReadAsStringAsync(), 400)}");
        }
        catch (Exception e) { Console.WriteLine($"ERR {verb,-4} {leaf}  {e.Message}"); }
    }
Console.WriteLine("\n=== collection-level realtime-balance (the free cached one) ===");
using (var rb = await http.GetAsync(Base + "/api/accounts/realtime-balance"))
{
    Console.WriteLine($"{(int)rb.StatusCode} GET /api/accounts/realtime-balance");
    Console.WriteLine(await rb.Content.ReadAsStringAsync());
}

static string Esc(string s) => System.Text.Json.JsonSerializer.Serialize(s);
static string Trim(string s, int n = 160) =>
    (s.Length > n ? s[..n] + "…" : s).ReplaceLineEndings(" ");
static string? Between(string s, string a, string b)
{
    var i = s.IndexOf(a, StringComparison.Ordinal); if (i < 0) return null;
    i += a.Length;
    var j = s.IndexOf(b, i, StringComparison.Ordinal); return j < 0 ? null : s[i..j];
}