using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace PrepaidTracker;

/// <summary>
/// Owner-controlled access. On first launch the device claims a unique username; the
/// server records it against this install's device id. From then on the app asks the
/// server every few seconds whether this user is still allowed in. If the owner blocks
/// them, the customer list is wiped and the app is replaced by <see cref="BlockedPage"/>.
///
/// Two rules keep this from turning into a footgun:
///
///   * FAIL OPEN on an unreachable server, FAIL CLOSED on a definite "no". A network
///     error returns null and we keep the LAST KNOWN state — the free Render tier cold
///     starts for up to a minute, and a 60-second cold start must not lock out a
///     paying-attention user. But because the last known state is persisted, a blocked
///     user can't escape by turning off wifi either.
///
///   * The whole feature is OFF when <see cref="ApiBase"/> is empty. No server, no
///     username screen, no polling — the app behaves exactly as it did before.
/// </summary>
public static class Access
{
    // ---------------- dynamic API base ----------------
    //
    // The API base used to be a hardcoded const, which meant that whenever the
    // Render URL changed, every installed APK had to be rebuilt and redistributed.
    // It is now fetched at startup from a small JSON file that the owner controls,
    // and cached in Preferences. If the Render URL ever changes again, edit the
    // JSON — no rebuild needed.

    /// <summary>
    /// The ONE URL that must never change. Baked into every APK. If you ever
    /// rename or delete the config repo, every installed app breaks.
    /// </summary>
    private const string ConfigUrl =
        "https://raw.githubusercontent.com/sohelranasagor/TestConfig/main/config.json";

    /// <summary>
    /// Used only on first launch before the lookup completes, or if the very
    /// first launch has no internet. Keep this pointed at the current working
    /// API base URL at the moment you build the APK.
    /// </summary>
    private const string FallbackApiBase = "https://prepaidtracker-api.onrender.com";

    private const string CacheKey = "cached_api_base";

    /// <summary>
    /// Your Render URL, no trailing slash. Empty = access control disabled.
    /// Now dynamic: reads the most recent value fetched from the config file
    /// (cached in Preferences), or the fallback if we've never successfully
    /// fetched. All call sites that reference Access.ApiBase (string
    /// interpolation, etc.) keep working unchanged.
    /// </summary>
    public static string ApiBase =>
        Preferences.Get(CacheKey, FallbackApiBase);

    public static bool Enabled => ApiBase.Length > 0;

    /// <summary>
    /// Fetch the current API base URL from the config file and cache it.
    /// Fire-and-forget from app startup (see MauiProgram.CreateMauiApp).
    /// Safe to call repeatedly; failures are swallowed so the app continues
    /// with the last cached URL when offline.
    /// </summary>
    public static async Task RefreshConfigAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync(ConfigUrl);
            using var doc = JsonDocument.Parse(json);
            var url = doc.RootElement.GetProperty("PrepaidTrackerApiBase").GetString();
            if (!string.IsNullOrWhiteSpace(url))
                Preferences.Set(CacheKey, url.TrimEnd('/'));
        }
        catch
        {
            // Offline, key missing, or fetch failed — keep using the last cached URL.
        }
    }

    /// <summary>
    /// The background poll. Short timeout on purpose: if the server is asleep this tick
    /// is meant to fail fast and leave the verdict alone, not queue up behind a cold start.
    /// </summary>
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// The client for the two calls a human is WAITING on: claiming a username, and
    /// tapping "Check again". A free Render service spins down after 15 minutes idle and
    /// cold starts in 30–60 seconds, so the first request of the morning is slow — not
    /// broken. A 15-second timeout here turned that into "Couldn't reach the server" on
    /// first launch, which is the one moment the app cannot afford to look dead.
    /// </summary>
    static readonly HttpClient Slow = new() { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary>3–20 chars, lowercase letters, digits and underscore. Anything else is
    /// a support ticket waiting to happen ("was that an l or a 1?").</summary>
    static readonly Regex Valid = new("^[a-z0-9_]{3,20}$", RegexOptions.Compiled);

    public static bool IsValidUsername(string s) => Valid.IsMatch(s);

    public enum Claim { Ok, Taken, Invalid, Unreachable }

    /// <summary>Claim a username for THIS device. Idempotent: re-registering the same
    /// name from the same device just refreshes the row.</summary>
    public static async Task<Claim> ClaimAsync(string username, CancellationToken ct = default)
    {
        username = username.Trim().ToLowerInvariant();
        if (!IsValidUsername(username)) return Claim.Invalid;

        try
        {
            using var res = await Slow.PostAsJsonAsync($"{ApiBase}/v1/register", new
            {
                username,
                device = Store.InstallId,
                platform = DeviceInfo.Platform.ToString(),
                model = DeviceInfo.Model,
                version = AppInfo.VersionString
            }, ct);

            if (res.StatusCode == HttpStatusCode.Conflict) return Claim.Taken;
            if (!res.IsSuccessStatusCode) return Claim.Unreachable;

            var body = await res.Content.ReadAsStringAsync(ct);
            Store.Username = username;
            Store.Blocked = Flag(body, "blocked");
            return Claim.Ok;
        }
        catch { return Claim.Unreachable; }
    }

    /// <summary>The server's verdict. Wipe is only ever true alongside Blocked.</summary>
    public readonly record struct Verdict(bool Blocked, bool Wipe);

    /// <summary>
    /// The verdict, or null if we couldn't tell (in which case: keep the last state).
    /// <paramref name="patient"/> waits out a cold start; the 5s poll never does.
    /// </summary>
    public static async Task<Verdict?> CheckAsync(CancellationToken ct = default, bool patient = false)
    {
        if (!Enabled || Store.Username.Length == 0) return null;

        try
        {
            var url = $"{ApiBase}/v1/status?username={Uri.EscapeDataString(Store.Username)}"
                    + $"&device={Uri.EscapeDataString(Store.InstallId)}";

            using var res = await (patient ? Slow : Http).GetAsync(url, ct);

            // The name is registered to a DIFFERENT device: someone is using a username
            // that isn't theirs. Wall them, but do NOT wipe — the data on this phone
            // belongs to whoever is holding it, and they may just have picked a name
            // that was already taken.
            if (res.StatusCode == HttpStatusCode.Forbidden) return new Verdict(true, false);
            if (!res.IsSuccessStatusCode) return null;

            var body = await res.Content.ReadAsStringAsync(ct);

            // The server has never heard of this name: the owner deleted the row, or the
            // reaper collected it after a long quiet spell (see REAP_AFTER_DAYS). Just
            // re-announce ourselves — the user shouldn't have to know any of this happened.
            if (!Flag(body, "known"))
            {
                var claim = await ClaimAsync(Store.Username, ct);

                // ...unless somebody else has taken the name in the meantime. Then we have
                // no identity on the server, and quietly carrying on would mean this device
                // can never be blocked. Send them back to the username screen to pick again.
                if (claim == Claim.Taken)
                {
                    Store.Username = "";
                    MainThread.BeginInvokeOnMainThread(Gate.Apply);
                    return null;
                }

                return new Verdict(Store.Blocked, false);
            }

            return new Verdict(Flag(body, "blocked"), Flag(body, "wipe"));
        }
        catch { return null; }
    }

    static bool Flag(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    // ---------------- the 5-second watch ----------------

    static CancellationTokenSource? _cts;

    /// <summary>Poll while the app is in the foreground. Stopped in OnSleep: a timer that
    /// keeps firing in the background drains the battery and can't repaint anything anyway.</summary>
    public static void StartWatch()
    {
        if (!Enabled || _cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = Loop(cts.Token);
    }

    public static void StopWatch()
    {
        _cts?.Cancel();
        _cts = null;
    }

    static async Task Loop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            // Check immediately too — waiting 5s to notice a block on launch is 5s of
            // a blocked user reading their neighbour's balance.
            await ApplyAsync(ct);
            while (await timer.WaitForNextTickAsync(ct))
                await ApplyAsync(ct);
        }
        catch (OperationCanceledException) { /* app went to sleep */ }
    }

    static async Task ApplyAsync(CancellationToken ct)
    {
        var verdict = await CheckAsync(ct);
        if (verdict == null) return;                 // unreachable: keep the last known state
        Apply(verdict.Value);
    }

    /// <summary>
    /// Persist the verdict and, if it changed, swap the app's root page.
    ///
    /// The owner chooses per block whether it is destructive:
    ///
    ///   SUSPEND (wipe=false) — the wall goes up, the data stays on the phone, and
    ///                          unblocking hands it all back. Reversible.
    ///   REVOKE  (wipe=true)  — the wall goes up AND the customer list is deleted here.
    ///                          NOT reversible: the server never had the readings, so
    ///                          unblocking cannot restore them. The device starts over
    ///                          at onboarding.
    ///
    /// Suspend is the default. A block is an administrative act; destroying someone's
    /// months of meter readings is a different act, and it should have to be asked for.
    /// </summary>
    public static void Apply(Verdict verdict)
    {
        // The wipe is evaluated BEFORE the no-change return. Escalating an existing block
        // to a wipe leaves verdict.Blocked equal to Store.Blocked, so the old ordering
        // discarded the whole verdict and the wipe silently never happened.
        if (verdict.Blocked && verdict.Wipe) Store.WipeMeters();

        if (verdict.Blocked == Store.Blocked) return;

        Store.Blocked = verdict.Blocked;

        MainThread.BeginInvokeOnMainThread(Gate.Apply);
    }
}

/// <summary>Decides which root page the app shows: blocked screen, username screen, or the app.</summary>
public static class Gate
{
    public static Type Wanted =>
        !Access.Enabled                 ? typeof(AppShell)
        : Store.Blocked                 ? typeof(BlockedPage)
        : Store.Username.Length == 0    ? typeof(UsernamePage)
                                        : typeof(AppShell);

    public static Page Root()
    {
        var t = Wanted;
        if (t == typeof(BlockedPage)) return new BlockedPage();
        if (t == typeof(UsernamePage)) return new UsernamePage();
        return new AppShell();
    }

    /// <summary>Swap the root ONLY when the required page type actually changed — otherwise
    /// every poll tick would tear down and rebuild the whole Shell.</summary>
    public static void Apply()
    {
        var app = Application.Current;
        if (app == null || app.MainPage?.GetType() == Wanted) return;
        app.MainPage = Root();
    }
}

/// <summary>First launch: claim a unique name. This is what the owner sees and blocks by.</summary>
public class UsernamePage : ContentPage
{
    public UsernamePage()
    {
        BackgroundColor = Theme.Bg;

        var field = UI.Field(L.S("usernamePlaceholder"));
        field.Keyboard = Keyboard.Plain;

        var box = UI.Card(Theme.Card, 14);
        box.Content = field;

        var error = new Label { TextColor = Theme.Red, FontSize = 12, IsVisible = false };
        var go = UI.Accent(L.S("continue") + "  →");
        var busy = new ActivityIndicator { Color = Theme.Green, IsVisible = false, IsRunning = false };

        async void Submit()
        {
            var name = (field.Text ?? "").Trim().ToLowerInvariant();

            if (!Access.IsValidUsername(name))
            {
                error.Text = "⚠︎ " + L.S("usernameInvalid");
                error.IsVisible = true;
                box.Stroke = Theme.Red;
                return;
            }

            field.Unfocus();
            go.IsEnabled = false;
            busy.IsVisible = busy.IsRunning = true;
            error.IsVisible = false;

            // A cold-starting free Render service can take the better part of a minute.
            // A spinner alone reads as "this app is broken"; say what's happening.
            using var waking = new CancellationTokenSource();
            _ = Task.Delay(6000, waking.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    error.Text = L.S("wakingServer");
                    error.TextColor = Theme.Muted;
                    error.IsVisible = true;
                });
            }, TaskScheduler.Default);

            var result = await Access.ClaimAsync(name);

            waking.Cancel();
            busy.IsVisible = busy.IsRunning = false;
            go.IsEnabled = true;
            error.TextColor = Theme.Red;

            switch (result)
            {
                case Access.Claim.Ok:
                    Access.StartWatch();
                    Gate.Apply();
                    return;
                case Access.Claim.Taken:
                    error.Text = "⚠︎ " + L.S("usernameTaken");
                    break;
                case Access.Claim.Invalid:
                    error.Text = "⚠︎ " + L.S("usernameInvalid");
                    break;
                default:
                    error.Text = "⚠︎ " + L.S("usernameOffline");
                    break;
            }
            error.IsVisible = true;
            box.Stroke = Theme.Red;
        }

        go.Clicked += (_, _) => Submit();
        field.Completed += (_, _) => Submit();
        field.TextChanged += (_, _) => { error.IsVisible = false; box.Stroke = Theme.Border; };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 80, 24, 40),
                Spacing = 12,
                Children =
                {
                    new Label { Text = "⚡", FontSize = 44 },
                    UI.Title(L.S("usernameTitle")),
                    new Label { Text = L.S("usernameSub"), TextColor = Theme.Muted, FontSize = 14 },
                    box,
                    new Label { Text = L.S("usernameHint"), TextColor = Theme.Faint, FontSize = 11 },
                    error,
                    go,
                    busy
                }
            }
        };
    }
}

/// <summary>The wall a blocked device hits. No tabs, no back, no data — just a re-check.</summary>
public class BlockedPage : ContentPage
{
    public BlockedPage()
    {
        // Deliberately NOT the app's green: this screen should not look like a state of
        // the app, it should look like the app is gone.
        BackgroundColor = Color.FromArgb("#0B1220");

        var status = new Label
        {
            TextColor = Theme.Faint, FontSize = 13, IsVisible = false,
            HorizontalTextAlignment = TextAlignment.Center
        };

        var check = new Button
        {
            Text = L.S("checkAgain"),
            BackgroundColor = Color.FromArgb("#2F6BFF"),
            TextColor = Colors.White,
            FontSize = 17,
            FontAttributes = FontAttributes.Bold,
            CornerRadius = 16,
            HeightRequest = 62,
            Margin = new Thickness(0, 20, 0, 0)
        };

        check.Clicked += async (_, _) =>
        {
            check.IsEnabled = false;
            status.Text = L.S("checking");
            status.TextColor = Theme.Faint;
            status.IsVisible = true;

            using var waking = new CancellationTokenSource();
            _ = Task.Delay(6000, waking.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                MainThread.BeginInvokeOnMainThread(() => status.Text = L.S("wakingServer"));
            }, TaskScheduler.Default);

            var verdict = await Access.CheckAsync(patient: true);
            waking.Cancel();

            // Unblocked: Apply() swaps this page out from under us.
            if (verdict is { Blocked: false }) { Access.Apply(verdict.Value); return; }

            status.Text = verdict == null ? L.S("usernameOffline") : L.S("stillBlocked");
            status.TextColor = verdict == null ? Theme.Amber : Theme.Faint;
            check.IsEnabled = true;
        };

        Content = new Grid
        {
            Padding = new Thickness(28, 0),
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 14,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new Label
                        {
                            Text = "⛔", FontSize = 64,
                            HorizontalTextAlignment = TextAlignment.Center
                        },
                        new Label
                        {
                            Text = L.S("blockedTitle"),
                            TextColor = Colors.White, FontSize = 32, FontAttributes = FontAttributes.Bold,
                            HorizontalTextAlignment = TextAlignment.Center
                        },
                        new Label
                        {
                            Text = L.S("blockedBody"),
                            TextColor = Color.FromArgb("#9AA3B2"), FontSize = 16,
                            HorizontalTextAlignment = TextAlignment.Center,
                            Margin = new Thickness(0, 2, 0, 0)
                        },
                        check,
                        status,
                        new Label
                        {
                            Text = Store.Username.Length > 0 ? "@" + Store.Username : "",
                            TextColor = Color.FromArgb("#3C4557"), FontSize = 12,
                            HorizontalTextAlignment = TextAlignment.Center,
                            Margin = new Thickness(0, 16, 0, 0)
                        }
                    }
                }
            }
        };
    }

    /// <summary>Android hardware back must not dismiss the wall.</summary>
    protected override bool OnBackButtonPressed() => true;
}
