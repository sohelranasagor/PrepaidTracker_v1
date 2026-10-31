using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace PrepaidTracker;

/// <summary>
/// The পরামর্শ tab. PrepaidPro brands this "AI চালিত"; what's actually useful is a
/// clear forecast, a concrete recharge number, and two or three honest observations
/// about your own usage — so that's what this is, without the label.
/// </summary>
public class InsightsPage : AppPage
{
    readonly VerticalStackLayout _body = new() { Spacing = 14, Padding = new Thickness(16, 16, 16, 40) };

    public InsightsPage()
    {
        Title = L.S("insights");
        SetBody(new ScrollView { Content = _body });

        // Build the first frame now — rendering only in OnAppearing left a
        // blank flash while Shell switched to this tab for the first time.
        if (ShouldRender(StateToken())) Render();
    }

    string StateToken()
    {
        var m = Store.Selected();
        if (m == null) return "empty|" + Store.Language;
        var snap = Store.Snapshot(m.Id);
        return string.Join('|', m.Id, Store.Readings(m.Id).Count, snap?.SyncedAt.Ticks ?? 0,
            Store.Language, Store.TargetDays, Store.Tariff ?? 0);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (ShouldRender(StateToken())) Render();
    }

    void Render()
    {
        _body.Children.Clear();

        var head = new HorizontalStackLayout { Spacing = 10 };
        head.Add(UI.Title(L.S("insightsTitle")));
        head.Add(UI.Pill("✦ " + L.S("modelBadge"), Color.FromArgb("#17342A"), Theme.Green));
        _body.Add(head);

        var meter = Store.Selected();
        var f = meter == null ? null : Forecast.Build(Store.Readings(meter.Id), Store.Snapshot(meter.Id));

        if (f?.DailyAvg == null)
        {
            _body.Add(UI.Hint(L.S("needMoreData")));
            return;
        }

        // ---- hero: days remaining ----
        var hero = UI.Card(Theme.Hero, 20);
        hero.Stroke = Colors.Transparent;
        hero.Padding = new Thickness(20, 24);
        hero.Content = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = "✦", FontSize = 20, TextColor = Color.FromArgb("#BFE6D4") },
                new Label { Text = L.S("basedOnAvg"), TextColor = Color.FromArgb("#C9E8D8"), FontSize = 13 },
                new Label
                {
                    // No balance from the utility → days-left is unknowable;
                    // lead with the spending rate instead of a fake "0 days".
                    // Singular when the rendered number is 1 — the key is chosen from the
                    // STRING that will be shown, not the raw double, so "1 days left" can't
                    // come back via rounding (0.6 and 1.4 both display as 1).
                    Text = f.DaysLeft != null
                        ? L.S($"{f.DaysLeft:0}" == "1" ? "willLast1" : "willLast",
                              L.N($"{f.DaysLeft:0}"))
                        : L.S("dailyRate", L.N($"{f.DailyAvg:0}")),
                    TextColor = Colors.White, FontSize = 32, FontAttributes = FontAttributes.Bold
                }
            }
        };
        _body.Add(hero);

        if (f.Confidence < 0.5) _body.Add(UI.Hint(L.S("confidenceLow")));

        // ---- suggested recharge ----
        var sug = UI.Card();
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        grid.Add(new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = L.S("suggestedRech"), TextColor = Theme.Muted, FontSize = 12 },
                UI.Money(f.SuggestedRecharge ?? 0, 28, Theme.Text),
                UI.Hint(L.S("enoughFor", L.N(Store.TargetDays.ToString())))
            }
        }, 0, 0);
        grid.Add(new Grid { WidthRequest = 40, VerticalOptions = LayoutOptions.Center, Children = { HomePage.MoneyIcon() } }, 1, 0);
        sug.Content = grid;
        _body.Add(sug);

        // ---- today / 7-day avg / month end ----
        // Prefer ActiveAvg (the active-day mean) so a vacation ৳0 in the window
        // doesn't pull "7-day average" down and put it out of step with the same
        // number on Home and Usage. Falls back to SimpleAvg only when every day
        // in the window was quiet — the same "no active-day answer" case where
        // ActiveAvg would be null anyway.
        var displayAvg = f.ActiveAvg ?? f.SimpleAvg;
        // Today when it's measured; otherwise the newest day we do have a real figure for,
        // under its own label. Only a dash when there is no daily data at all.
        var dayCell = f.Today != null
            ? (L.S("today"), "৳ " + L.N($"{f.Today:0}"))
            : f.LastFullDay != null
                ? (L.S("lastFullDay"), "৳ " + L.N($"{f.LastFullDay:0}"))
                : (L.S("today"), "—");

        _body.Add(UI.TripleStat(
            dayCell,
            (L.S("sevenDayAvg"), "৳ " + L.N($"{displayAvg:0}")),
            (L.S("monthEnd"), f.MonthEndEstimate != null ? "৳ " + L.N($"{f.MonthEndEstimate:0}") : "—")));

        // ---- observations ----
        // Collect first, then decide whether the section exists at all. Both observations
        // are conditional, and on a monthly-path meter BOTH are skipped — SavingsPct is
        // only computed on the daily path, and Today is null. Adding the heading up front
        // left "ব্যক্তিগত বিশ্লেষণ" sitting alone above the reminder button with nothing
        // beneath it: a section title promising an analysis that isn't there.
        var observations = new List<View>();

        if (f.SavingsPct is { } pct && Math.Abs(pct) >= 3)
            observations.Add(Insight(pct > 0 ? "📉" : "📈",
                pct > 0 ? L.S("savingPct", L.N($"{pct:0}")) : L.S("overPct", L.N($"{-pct:0}")),
                pct > 0 ? Theme.Green : Theme.Amber));

        // Compare today against the ACTIVE-day mean, not the raw SimpleAvg. On a day
        // right after a vacation the raw mean drops, and comparing today's spend to
        // that lowered figure would say "spending more than usual" for a completely
        // normal day. The active-day mean is the "typical usage day" reference the
        // observation actually wants.
        // Requires a MEASURED today. With no daily feed there is no such thing, and the
        // old code compared the monthly rate against itself — always equal, so the card
        // said "below average" unconditionally. No observation beats a false one.
        if (f.Today is { } today && (f.ActiveAvg ?? f.SimpleAvg) is { } avg && avg > 0)
            observations.Add(Insight("📅",
                today <= avg ? L.S("todayBelow", L.N($"{today:0}")) : L.S("todayAbove", L.N($"{today:0}")),
                today <= avg ? Theme.Green : Theme.Amber));

        if (observations.Count > 0)
        {
            _body.Add(UI.Section(L.S("personalAnalysis")));
            foreach (var o in observations) _body.Add(o);
        }

        // ---- reminder ----
        var remind = UI.Card(Color.FromArgb("#31280F"), 14);
        remind.Stroke = Color.FromArgb("#4A3C15");
        remind.Content = new Label
        {
            Text = "🔔  " + L.S("setReminder"),
            TextColor = Theme.Amber, FontSize = 15, FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Shell.Current.GoToAsync("//profile");
        remind.GestureRecognizers.Add(tap);
        _body.Add(remind);

        _body.Add(UI.Hint(
            f.FromUtility ? "Daily figures come straight from your utility."
            : f.FromMonthly ? L.S(meter!.Utility == Utility.Nesco ? "monthlyNote" : "monthlyNoteFallback")
            : "Daily figures are worked out from your own balance readings."));
    }

    static View Insight(string glyph, string text, Color accent)
    {
        var card = UI.Card();
        card.Padding = new Thickness(14, 14);
        var g = new Grid { ColumnSpacing = 14, ColumnDefinitions = { new ColumnDefinition(new GridLength(44)), new ColumnDefinition(GridLength.Star) } };

        var icon = UI.Card(Color.FromArgb("#17342A"), 10);
        icon.Padding = 0;
        icon.HeightRequest = 40;
        icon.Stroke = Colors.Transparent;
        icon.Content = new Label { Text = glyph, FontSize = 16, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
        g.Add(icon, 0, 0);

        g.Add(new Label { Text = text, TextColor = Theme.Text, FontSize = 14, VerticalOptions = LayoutOptions.Center }, 1, 0);
        card.Content = g;
        return card;
    }
}
