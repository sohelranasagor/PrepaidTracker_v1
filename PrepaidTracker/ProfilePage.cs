using System.Globalization;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace PrepaidTracker;

public class ProfilePage : AppPage
{
    readonly VerticalStackLayout _body = new() { Spacing = 14, Padding = new Thickness(16, 16, 16, 40) };

    public ProfilePage()
    {
        Title = L.S("profile");
        SetBody(new ScrollView { Content = _body });

        // Build the first frame now — rendering only in OnAppearing left a
        // blank flash while Shell switched to this tab for the first time.
        if (ShouldRender(StateToken())) Render();
    }

    string StateToken()
    {
        var m = Store.Selected();
        // Include every per-meter setting the notification sheet can flip so the "🔔 …"
        // summary row re-renders after the sheet closes. Without this, on/off flips
        // paint correctly inside the sheet but the parent page keeps yesterday's
        // summary until something else forces a redraw.
        var notif = (Store.AlertsEnabled ? "1" : "0") + "-" +
                    string.Join(",", Store.Meters().Select(x =>
                        $"{(x.NotificationsEnabled ? 1 : 0)}:{x.LowTaka}:{x.LowDays}"));
        return string.Join('|', Store.Meters().Count, m?.Id ?? "-", Store.Language,
            Store.LowTaka, Store.LowDays, Store.Tariff ?? 0, Store.TargetDays, notif,
            Store.Snapshot(m?.Id ?? "")?.SyncedAt.Ticks ?? 0);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (ShouldRender(StateToken())) Render();
    }

    void Refresh()
    {
        Invalidate();
        Render();
    }

    void Render()
    {
        _body.Children.Clear();
        _body.Add(UI.Title(L.S("profile")));

        var meters = Store.Meters();
        var selected = Store.Selected();

        // ---- app card ----
        var appCard = UI.Card();
        var ag = new Grid { ColumnSpacing = 14, ColumnDefinitions = { new ColumnDefinition(new GridLength(48)), new ColumnDefinition(GridLength.Star) } };
        ag.Add(Badge("⚡", Color.FromArgb("#17342A")), 0, 0);
        ag.Add(new VerticalStackLayout
        {
            Spacing = 2, VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = L.S("app"), TextColor = Theme.Text, FontSize = 16, FontAttributes = FontAttributes.Bold },
                new Label { Text = selected != null ? $"A/C {L.N(selected.Account)}" : L.S("tagline"), TextColor = Theme.Faint, FontSize = 12, FontFamily = "Menlo" }
            }
        }, 1, 0);
        appCard.Content = ag;
        _body.Add(appCard);

        // ---- accounts ----
        _body.Add(UI.Eyebrow(L.S("yourAccounts")));
        var accounts = UI.Card();
        accounts.Padding = new Thickness(0, 4);
        var astack = new VerticalStackLayout { Spacing = 0 };

        foreach (var m in meters)
        {
            astack.Add(AccountRow(m, selected?.Id == m.Id));
            astack.Add(UI.Rule());
        }
        astack.Add(AddAccountRow());
        accounts.Content = astack;
        _body.Add(accounts);

        if (meters.Count > 0) _body.Add(UI.Hint(L.S("swipeToDelete")));

        // ---- settings ----
        _body.Add(UI.Eyebrow(L.S("settings")));
        var settings = UI.Card();
        var ss = new VerticalStackLayout { Spacing = 0 };

        ss.Add(SettingRow("🌐", L.S("language"), L.Bn ? "বাংলা" : "English", ChooseLanguage));
        ss.Add(UI.Rule());
        // Summary reflects the master switch. When on, count how many meters are muted
        // AND how many have customised their thresholds — one is a warning ("not every
        // account is receiving alerts"), the other is a positive signal ("you've tuned
        // this per account"). Both aggregate into a compact right-side hint.
        string notifSummary;
        if (!Store.AlertsEnabled) notifSummary = L.Bn ? "বন্ধ" : "Off";
        else
        {
            var mutedCount = meters.Count(m => !m.NotificationsEnabled);
            if (mutedCount > 0)
                notifSummary = L.N(mutedCount.ToString()) + (L.Bn ? " বন্ধ" : " muted");
            else if (meters.Count > 0 && meters.All(m => m.NotificationsEnabled))
                notifSummary = L.Bn ? "সব চালু" : "All on";
            else
                notifSummary = "";
        }
        ss.Add(SettingRow("🔔", L.S("notification"), notifSummary, EditAlerts));
        ss.Add(UI.Rule());
        ss.Add(SettingRow("⚡", L.S("tariff"), Store.Tariff != null ? "৳ " + L.N($"{Store.Tariff:0.00}") : "—", EditTariff));
        ss.Add(UI.Rule());
        ss.Add(SettingRow("📅", L.S("suggestedRech"), L.S("enoughFor", L.N(Store.TargetDays.ToString())), EditTargetDays));

        // There WAS an "Emergency credit" row here, for the user to type in the figure the
        // portal doesn't publish. It's gone: nobody filled it in, so the one thing it fed —
        // a clause in the low-balance banner — never appeared. A setting that exists only
        // to stay empty is a setting worth deleting. The banner now quotes the figure only
        // when the utility actually publishes it (Snapshot.EmergencyBalance).

        settings.Content = ss;
        _body.Add(settings);

        // The diagnostics card lived here: endpoint names, probe results, raw field counts,
        // and a Copy button. It is a developer panel — it meant nothing to the person
        // holding the phone and it made the settings screen read like a debugger. Gone from
        // the UI; DiagText() below is kept because it is still the only thing that can say
        // WHY a sync failed on a device with no debugger attached, and re-rendering it is
        // four lines whenever that is needed again.

        // ---- background refresh + privacy ----
        // The hint has to match the platform: iOS is woken by BGTaskScheduler and stops
        // dead once force-quit, Android by WorkManager, which survives a swipe-away but
        // gets killed outright by some OEM battery managers. One string each, and neither
        // promises anything the phone in the hand doesn't do.
        bool android = DeviceInfo.Current.Platform == DevicePlatform.Android;

        var bg = UI.Card();
        bg.Content = new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                UI.Eyebrow(L.S("bgSync")),
                new Label
                {
                    Text = L.S(android ? "bgHintAndroid" : "bgHint"),
                    TextColor = Theme.Muted, FontSize = 12
                }
            }
        };
        _body.Add(bg);

        var priv = UI.Card();
        priv.Content = new VerticalStackLayout
        { Spacing = 6, Children = { UI.Eyebrow("🔒 " + L.S("privacyTitle")), new Label { Text = L.S("privacy"), TextColor = Theme.Muted, FontSize = 12 } } };
        _body.Add(priv);

        _body.Add(UI.Hint(L.S("unofficial")));
    }

    // ---------- rows ----------

    /// <summary>Tap to switch, swipe left to delete — the iOS convention.</summary>
    View AccountRow(Meter m, bool active)
    {
        var snap = Store.Snapshot(m.Id);
        var name = snap?.CustomerName is { Length: > 0 } cn ? Titlecase(cn) : (m.Label.Length > 0 ? m.Label : m.Account);

        var row = new Grid
        {
            Padding = new Thickness(16, 12), ColumnSpacing = 12,
            BackgroundColor = Theme.Card,
            ColumnDefinitions = { new ColumnDefinition(new GridLength(44)), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        row.Add(Badge("⚡", Color.FromArgb("#17342A")), 0, 0);
        row.Add(new VerticalStackLayout
        {
            Spacing = 2, VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = name, TextColor = Theme.Text, FontSize = 15 },
                new Label { Text = $"{m.Utility.ToString().ToUpperInvariant()} · A/C {L.N(m.Account)}", TextColor = Theme.Faint, FontSize = 11, FontFamily = "Menlo" }
            }
        }, 1, 0);
        if (active) row.Add(UI.StatusPill(L.S("active"), Color.FromArgb("#17342A"), Theme.Green), 2, 0);

        var tap = new TapGestureRecognizer();
        var id = m.Id;
        tap.Tapped += (_, _) => { Store.SelectedMeterId = id; Refresh(); };
        row.GestureRecognizers.Add(tap);

        var delete = new SwipeItem
        {
            Text = L.S("delete"),
            BackgroundColor = Theme.Red
        };
        delete.Invoked += async (_, _) =>
        {
            if (await Modal.Confirm(Root, L.S("delete"), L.S("deleteMeterQ"), L.S("delete"), L.S("cancel"), destructive: true))
            { Store.DeleteMeter(id); Refresh(); }
            else Refresh();   // spring the row back
        };

        return new SwipeView
        {
            RightItems = new SwipeItems(new[] { delete }) { Mode = SwipeMode.Reveal },
            Content = row
        };
    }

    View AddAccountRow()
    {
        var row = new Grid
        {
            Padding = new Thickness(16, 12), ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(new GridLength(44)), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        row.Add(Badge("＋", Color.FromArgb("#31280F"), Theme.Amber), 0, 0);
        row.Add(new Label { Text = L.S("addAccount"), TextColor = Theme.Green, FontSize = 15, VerticalOptions = LayoutOptions.Center }, 1, 0);
        row.Add(new Label { Text = "›", TextColor = Theme.Faint, FontSize = 18, VerticalOptions = LayoutOptions.Center }, 2, 0);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            var meter = await Modal.AddAccount(Root);
            if (meter == null) return;
            Store.AddMeter(meter);
            Refresh();
        };
        row.GestureRecognizers.Add(tap);
        return row;
    }

    static Border Badge(string glyph, Color bg, Color? fg = null)
    {
        var b = UI.Card(bg, 10);
        b.Padding = 0;
        b.HeightRequest = 40;
        b.Stroke = Colors.Transparent;
        b.Content = new Label
        {
            Text = glyph, TextColor = fg ?? Theme.Text, FontSize = 15,
            HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center
        };
        return b;
    }

    static View SettingRow(string glyph, string label, string value, Func<Task> onTap)
    {
        var g = new Grid
        {
            Padding = new Thickness(0, 14), ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(new GridLength(34)), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(new GridLength(18)) }
        };
        g.Add(new Label { Text = glyph, FontSize = 15, VerticalOptions = LayoutOptions.Center }, 0, 0);
        g.Add(new Label { Text = label, TextColor = Theme.Text, FontSize = 15, VerticalOptions = LayoutOptions.Center }, 1, 0);
        g.Add(new Label { Text = value, TextColor = Theme.Faint, FontSize = 13, VerticalOptions = LayoutOptions.Center }, 2, 0);
        g.Add(new Label { Text = "›", TextColor = Theme.Faint, FontSize = 17, VerticalOptions = LayoutOptions.Center }, 3, 0);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => _ = onTap();
        g.GestureRecognizers.Add(tap);
        return g;
    }

    // ---------- flows, all in-app ----------

    async Task ChooseLanguage()
    {
        var pick = await Modal.Choose(Root, L.S("language"), new[] { "English", "বাংলা" }, L.Bn ? 1 : 0);
        if (pick == null) return;
        var newLang = pick == 1 ? "bn" : "en";
        if (newLang == Store.Language) return;
        Store.Language = newLang;
        // Every queued reminder the OS is holding was built with the OLD language's
        // strings frozen into its body — the language flag isn't consulted at fire time.
        // Cancel them all (and clear any already-delivered low-balance banners) so the
        // next sync re-arms the whole set in the language now in effect. Without this
        // step, a Bengali user keeps seeing "Recharge your meter · Balance is likely low
        // now" for days after switching from English.
        Notifier.CancelAllScheduled();
        AppShell.Restart();
    }

    async Task EditAlerts()
    {
        // The per-account notification sheet handles everything now: master switch,
        // per-meter switches, per-meter ৳ and days thresholds. Writes as it goes; no
        // save button. Refresh() at the end updates the "🔔 …" summary row on Profile.
        await Modal.NotificationSettings(Root);
        Refresh();
    }

    async Task EditTariff()
    {
        var t = await Modal.Prompt(Root, L.S("tariff"), L.S("tariffHint"),
            Store.Tariff?.ToString(CultureInfo.InvariantCulture), "5.72", Keyboard.Numeric);
        if (t == null) return;
        Store.Tariff = double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
        Refresh();
    }

    async Task EditTargetDays()
    {
        var t = await Modal.Prompt(Root, L.S("suggestedRech"), L.S("targetDays", Store.TargetDays),
            Store.TargetDays.ToString(), keyboard: Keyboard.Numeric);
        if (int.TryParse(t, out var d) && d is >= 7 and <= 90) Store.TargetDays = d;
        Refresh();
    }

    /// <summary>A short, pasteable report of what the last sync actually returned.</summary>
    static string DiagText(Meter m, Snapshot? snap, Forecast? f)
    {
        var lines = new List<string>
        {
            $"utility        {m.Utility.ToString().ToUpperInvariant()}",
            $"synced         {(snap != null ? snap.SyncedAt.ToString("g") : "never")}",
            $"balance        {(snap != null ? snap.Balance.ToString("0.00") : "—")}",
            $"readingTime    {(snap?.ReadingTime?.ToString("g") ?? "—")}",
            // "—" here means the portal never published the field, which is why the
            // low-balance banner stays silent about it.
            $"emergencyCr    {(snap?.EmergencyBalance?.ToString("0.##") ?? "—")}",
            $"minRecharge    {(snap?.MinRecharge?.ToString("0.##") ?? "—")}",
            $"monthToDate    {(snap != null ? snap.CurrentMonthConsumption.ToString("0.00") : "—")}",
            $"daily rows     {snap?.Days.Count ?? 0}",
            $"recharge rows  {snap?.Recharges.Count ?? 0}",
            $"monthly rows   {snap?.Months.Count ?? 0}",
            $"rate source    {(f == null ? "—" : f.FromUtility ? "utility daily feed" : f.FromMonthly ? "month total ÷ days" : "own readings")}"
        };

        if (m.Utility == Utility.Desco)
        {
            lines.Add("");
            lines.Add("endpoint probe:");
            lines.Add(DescoProvider.ProbeLog.Length > 0 ? DescoProvider.ProbeLog : "  (no probe recorded)");
        }
        return string.Join("\n", lines);
    }

    static string Titlecase(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
}
