using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Networking;
using System;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
// System.Drawing is in scope on iOS via implicit usings, so these three names are
// ambiguous without an alias. Same reason as the two above.
using Color = Microsoft.Maui.Graphics.Color;
using Label = Microsoft.Maui.Controls.Label;
// Implicit usings bring System.IO.Path into scope; we mean the drawable shape.
using Path = Microsoft.Maui.Controls.Shapes.Path;
using Point = Microsoft.Maui.Graphics.Point;
// iOSSpecific also defines a ScrollView (platform-config statics); alias the control.
using ScrollView = Microsoft.Maui.Controls.ScrollView;

namespace PrepaidTracker;

public class HomePage : AppPage
{
    // The header is pinned; only the body scrolls beneath it.
    readonly Grid _header = new();
    VerticalStackLayout _body = new() { Spacing = 14, Padding = new Thickness(16, 14, 16, 40) };
    readonly PullScrollView _scroll;

    // iOS already gets this right: RefreshView maps to UIRefreshControl, which slides
    // the content down and reveals a spinner. Android maps it to a Material
    // SwipeRefreshLayout — a puck dropping over stationary content — and no property
    // turns one into the other, so Android gets our own. Same surface either way.
#if ANDROID
    readonly PullRefresh _refresh = new();
#else
    readonly RefreshView _refresh = new();
#endif

    enum Mode { None, Onboarding, Home }
    Mode _mode = Mode.None;

    string _status = "";
    string _syncedThisLaunch = "";

    /// <summary>
    /// Real status-bar/notch inset from iOS, not a guess. The old hard-coded 54
    /// plus safety slack left a strip of dead green above the greeting on
    /// devices with a smaller inset. Falls back to 24 before the first layout
    /// (and on Android, where the page doesn't draw under the status bar).
    /// </summary>
    double StatusBarPad => Math.Max(On<iOS>().SafeAreaInsets().Top, 24);

    public HomePage()
    {
        Title = L.S("home");

        // Safe area off: the gradient runs to the top edge. We pad the content instead.
        On<iOS>().SetUseSafeArea(false);

        _scroll = new PullScrollView { Content = _body };
        _refresh.RefreshColor = Theme.Green;
        _refresh.Content = _scroll;
        _refresh.Refreshing += async (_, _) =>
        {
            Invalidate();
            // A deliberate pull is the user asking for a NEW number, so this is one of
            // only two paths allowed to spend DESCO's once-a-day live meter read. The
            // provider checks DESCO's limit first and only asks the meter if allowed.
            await Sync(userRequestedFresh: true);
            _refresh.IsRefreshing = false;
        };

        var layout = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        layout.Add(_header, 0, 0);
        layout.Add(_refresh, 0, 1);
        SetBody(layout);

        // Build the first frame now. Shell creates tab pages lazily on first
        // visit; rendering only in OnAppearing meant a blank flash mid-switch.
        if (ShouldRender(StateToken())) Render();
    }

    string StateToken()
    {
        var m = Store.Selected();
        // The inset is 0 until first layout; including it forces one re-render
        // the moment iOS reports the real value.
        var pad = Math.Round(StatusBarPad);

        // Wall-clock bucket, so opening the app at a later time forces a re-render
        // even when NOTHING has synced — the balance card's live estimate and the
        // "N ঘন্টা আগে" freshness both read DateTime.Now, and neither will update
        // if this token stays identical between opens.
        //
        // 5-minute granularity: fine enough that "1 মিনিট আগে" doesn't linger for
        // an hour, coarse enough that a burst of tab-switches inside a minute
        // doesn't cause layout churn. 288 unique buckets a day.
        var now = DateTime.Now;
        var bucket = $"{now:yyyyMMddHH}{now.Minute / 5}";

        if (m == null) return "empty|" + Store.Language + "|" + pad + "|" + bucket;
        var snap = Store.Snapshot(m.Id);
        return string.Join('|', m.Id, Store.Meters().Count, Store.Readings(m.Id).Count,
            snap?.SyncedAt.Ticks ?? 0, Store.Language, Store.TargetDays,
            // Every setting the header reads has to be in this token, or editing it
            // leaves a stale card here. The low banner uses the meter's EFFECTIVE
            // thresholds, which depend on both the app-wide defaults and the meter's own
            // overrides set in the notification sheet — so all four belong here. Tariff
            // feeds Forecast.
            Store.LowTaka, Store.LowDays, m.LowTaka, m.LowDays, Store.Tariff ?? 0,
            // A sync can settle a failed recharge without moving anything else this token
            // watches. Without the stamp the verdict is written to disk and the open page
            // never redraws — the banner would appear only after a tab switch.
            Reconcile.Stamp(m.Id),
            // The refresh control is disabled until DESCO allows another live read, so
            // the moment it becomes eligible belongs here too. The 5-minute bucket would
            // catch it eventually; this makes the transition exact.
            snap?.NextEligible?.Ticks ?? 0,
            // Signing in or out has to repaint the card immediately — nothing else in
            // this token notices that a credential appeared or vanished.
            m.Utility == Utility.Desco && DescoApi.HasCredential(m.Account),
            _status, pad, bucket);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (ShouldRender(StateToken())) Render();

        var meter = Store.Selected();
        if (meter != null && _syncedThisLaunch != meter.Id)
        {
            _syncedThisLaunch = meter.Id;
            // Cached read only — userRequestedFresh stays false. Opening the app three
            // times before noon must not exhaust the day's live-read allowance.
            await Sync(silent: true);
        }
    }

    void Refresh()
    {
        Invalidate();
        if (ShouldRender(StateToken())) Render();
    }

    // ---------------- sync ----------------

    bool _syncing;

    /// <summary>
    /// A fresh read the user asked for while another sync was already running.
    ///
    /// The old guard was a bare `if (_syncing) return;`. Opening the app starts a silent
    /// cached sync; a pull or ↻ tap that landed during it was thrown away without a word
    /// — the spinner stopped, no live read was sent, and the balance stayed exactly where
    /// it was. Now the request is remembered and run as soon as the current sync ends.
    /// </summary>
    bool _freshQueued;

    /// <param name="silent">No error modal — used by the on-appear auto-sync.</param>
    /// <param name="userRequestedFresh">
    /// True only when the person tapped refresh or pulled down. DESCO allows one fresh
    /// meter read per meter per day; every other path reads their cached figure, which
    /// costs nothing against the limit.
    /// </param>
    public async Task Sync(bool silent = false, bool userRequestedFresh = false)
    {
        // Pull-to-refresh, the ↻ button, and the on-appear auto-sync can all land at
        // once; two fetches in flight interleave status updates and double-record the
        // day's reading. A cached request arriving mid-sync is redundant and can be
        // dropped; a FRESH one is the user asking for something the running sync won't
        // do, so it is queued.
        if (_syncing)
        {
            _freshQueued |= userRequestedFresh;
            return;
        }

        await SyncCore(silent, userRequestedFresh);

        if (_freshQueued)
        {
            _freshQueued = false;
            await SyncCore(silent: false, userRequestedFresh: true);
        }
    }

    async Task SyncCore(bool silent, bool userRequestedFresh)
    {
        var meter = Store.Selected();
        if (meter == null) return;

        // A DESCO meter with no stored sign-in can't fetch anything. Ask here rather
        // than letting the fetch fail with an auth error: the user added this account
        // deliberately, and the next thing they need is the password prompt, not a
        // message about a session they never had. Silent (auto) syncs never prompt —
        // a modal appearing on its own when the app opens is startling.
        if (meter.Utility == Utility.Desco && !DescoApi.HasCredential(meter.Account))
        {
            if (silent) { _status = L.S("authNeeded"); Refresh(); return; }
            if (!await DescoSignIn.EnsureAsync(this, meter)) { Refresh(); return; }
        }

        if (Connectivity.NetworkAccess != NetworkAccess.Internet)
        {
            _status = L.S("offline");
            Refresh();
            return;
        }

        // _syncing is what drives the spinning ↻ (see SyncButton), so it must be
        // true only while the network call is genuinely in flight.
        _syncing = true;
        _status = L.S("syncing");
        Refresh();

        // IProvider.FetchAsync takes an account string and is shared with NESCO, so the
        // fresh-read request rides on the provider instance.
        var provider = Providers.For(meter.Utility);
        if (provider is DescoProvider dp) dp.UserRequestedFresh = userRequestedFresh;

        FetchResult result;
        try
        {
            result = await provider.FetchAsync(meter.Account);
        }
        finally
        {
            // Stop spinning BEFORE the re-render or any dialog — otherwise the icon
            // keeps turning behind an error modal as if work were still happening.
            _syncing = false;
        }

        // The session died and the stored password no longer works. Offer the prompt
        // immediately: this is the one failure a user can fix in ten seconds, and
        // burying it behind a generic error means the balance quietly ages while the
        // app looks like it's working.
        if (provider is DescoProvider auth && auth.LastState == SessionState.NeedsLogin)
        {
            _status = L.S("authExpiredBody");
            Refresh();
            if (silent) return;
            if (await DescoSignIn.PromptAsync(this, meter))
            {
                Notifier.ClearSessionWarning(meter.Id);
                await SyncCore(silent, userRequestedFresh);   // exactly one retry
            }
            return;
        }

        if (!result.Ok || result.Snapshot == null)
        {
            _status = result.Error ?? "Sync failed.";
            Refresh();
            if (!silent) await Modal.Info(Root, L.S("app"), _status);
            return;
        }

        // The portal reports the connection's sanction load, and the demand charge it
        // predicts is worth real money in the recharge suggestion. Cache it so a sync
        // where the summary endpoint didn't answer doesn't drop the suggestion back to
        // its fallback path. Written only on change — meters.json is rewritten whole.
        if (result.Snapshot.SanctionLoadKw > 0
            && Math.Abs(meter.SanctionLoadKw - result.Snapshot.SanctionLoadKw) > 0.01)
        {
            meter.SanctionLoadKw = result.Snapshot.SanctionLoadKw;
            Store.SaveMeter(meter);
        }

        // One shared recorder now (Sync.cs). There used to be two — this one and a copy
        // inside BackgroundSync — and they had drifted: the background copy counted FAILED
        // recharges as credit, inflating the day's spend, the rate, and days-left.
        //
        // Fully qualified on purpose: this class has its own Sync(bool) method, so a bare
        // `Sync.Record` binds to the method and the compiler rejects it (CS0119).
        PrepaidTracker.Sync.Record(meter, result.Snapshot);
        _status = "";
        Refresh();

        var f = Forecast.Build(Store.Readings(meter.Id), result.Snapshot);
        // Schedule BEFORE checking — see the note in Sync.RunAllAsync. The queued set has
        // to exist before the live alert decides whether it would be a duplicate.
        Notifier.ScheduleLowBalance(meter, f, result.Snapshot?.CustomerName);   // low later: queued with the OS
        Notifier.CheckLowBalance(meter, f, result.Snapshot?.CustomerName);      // low right now

        // Settle any failed recharge this read has overtaken. A verdict that flips to
        // Credited means the reading we just banked was computed without that money, so
        // bank it again — AddReading replaces by date, so this is a correction, not a
        // duplicate. Home used to skip this entirely, so verdicts only ever settled on a
        // background sync, which on iOS might be days away.
        if (Reconcile.Run(meter, result.Snapshot!, f?.DailyAvg ?? 0))
        {
            PrepaidTracker.Sync.Record(meter, result.Snapshot!);
            Refresh();
        }

        // The user asked for a fresh read and DESCO's limit is reached: say so, the same
        // way the portal does. Not awaited, so the pull-to-refresh spinner can settle.
        if (!silent && provider is DescoProvider q && q.QuotaBlocked)
            _ = ShowQuotaPopup(meter, result.Snapshot);

        // The user asked for a fresh read and it did NOT produce a new balance for any
        // other reason — DESCO answered with an error, timed out, or the meter didn't
        // respond. Every one of these used to be silent: the cached figure stayed on
        // screen looking exactly like a successful refresh. Say what happened, with
        // DESCO's own status, so "why didn't it update?" has an answer.
        if (!silent && provider is DescoProvider fr && fr.FreshFailure is { } why)
            _ = Modal.Info(Root, L.S("quotaTitle"), L.S("freshFailed", why));
    }

    // ---------------- render ----------------

    void Render()
    {
        // On iOS, Layout.Children.Clear() inside a RefreshView can leave the old
        // native subviews behind (the launch bug: onboarding visible stacked under
        // the home content). Replacing the ScrollView's content tears the native
        // tree down for real, so do that whenever the page changes mode.
        var mode = Store.Meters().Count == 0 ? Mode.Onboarding : Mode.Home;
        if (mode != _mode)
        {
            _body = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(16, 14, 16, 40) };
            _scroll.Content = _body;
            _mode = mode;
        }

        _header.Children.Clear();
        _body.Children.Clear();

        if (mode == Mode.Onboarding) { RenderOnboarding(); return; }

        var meter = Store.Selected()!;
        var snap = Store.Snapshot(meter.Id);
        var f = Forecast.Build(Store.Readings(meter.Id), snap);

        // The meter's EFFECTIVE thresholds — its own override if set, otherwise the app
        // default — the same figures Notifier alerts on. Using the app-wide values here
        // made the banner and the notification disagree for any customised meter.
        bool low = snap is not { BalanceReported: false }
                   && ((f?.DaysLeft is { } d && d < meter.EffectiveLowDays)
                       || (f != null && f.Balance < meter.EffectiveLowTaka));

        RenderHeader(meter, snap, f, low);

        // ---- three actions ----
        var actions = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }
        };
        actions.Add(ActionTile(CardIcon(), L.S("recharge"), () => _ = Launcher.OpenAsync("https://ekpay.gov.bd/#/payment/electricity-bill")), 0, 0);
        actions.Add(ActionTile(BarsIcon(), L.S("details"), () => _ = Shell.Current.GoToAsync("//usage")), 1, 0);
        actions.Add(ActionTile(SparkIcon(), L.S("advice"), () => _ = Shell.Current.GoToAsync("//insights")), 2, 0);
        _body.Add(actions);

        // ---- month summary ----
        _body.Add(UI.Section(L.S("monthSummary")));
        _body.Add(UI.TripleStatMixed(
            UI.Cell(
                L.S(f is { MonthSpentEstimated: true } ? "spentThisMonthEst" : "spentThisMonth"),
                f != null ? (f.MonthSpentEstimated ? "~৳ " : "৳ ") + $"{f.MonthSpent:#,0.##}" : "—"),
            DailyAvgCell(f),
            UI.Cell(
                L.S("estBill"),
                f?.MonthEndEstimate != null ? "৳ " + $"{f.MonthEndEstimate:#,0}" : "—")));

        // ---- chart: daily where the utility publishes one, monthly otherwise ----
        var daily = f is { Series.Count: > 1 };
        var completeMonths = f?.CompleteMonths() ?? new List<MonthRow>();
        var monthly = !daily && completeMonths.Count > 1;

        if (daily || monthly)
        {
            var head = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            head.Add(UI.Section(daily ? L.S("sevenDaySpend") : L.S("monthlySpend")), 0, 0);
            var seeAll = new Button { Text = L.S("seeAll"), FontSize = 13, TextColor = Theme.Green, BackgroundColor = Colors.Transparent, Padding = 0 };
            seeAll.Clicked += async (_, _) => await Shell.Current.GoToAsync("//usage");
            head.Add(seeAll, 1, 0);
            _body.Add(head);

            var bars = daily
                ? UI.LastNCalendarDays(f!.ChartDays(), 7)
                : completeMonths.TakeLast(6).Select(m => new DayRow { Date = m.Month + "-01", Taka = m.Taka }).ToList();

            var chart = UI.Card();
            chart.Content = UI.BarChart(bars, daily ? UI.ChartLabels.Weekday : UI.ChartLabels.Month);
            _body.Add(chart);

            _body.Add(daily && f != null
                ? UI.TripleStatMixed(
                    f.Today != null
                        ? UI.Cell(L.S("today"), "৳ " + $"{f.Today:0}")
                        : UI.Cell(f.LastFullDay != null ? L.S("lastFullDay") : L.S("today"),
                                  f.LastFullDay != null ? "৳ " + $"{f.LastFullDay:0}" : "—"),
                    AverageCell(f),
                    UI.Cell(L.S("highest"), "৳ " + $"{f.MaxDay:0}"))
                : UI.TripleStat(
                    (L.S("latest"), "৳ " + $"{bars[^1].Taka:#,0}"),
                    (L.S("average"), "৳ " + $"{bars.Average(b => b.Taka):#,0}"),
                    (L.S("highest"), "৳ " + $"{bars.Max(b => b.Taka):#,0}")));

            if (monthly) _body.Add(UI.Hint(L.S(meter.Utility == Utility.Nesco ? "monthlyNote" : "monthlyNoteFallback")));
        }
        else
        {
            _body.Add(UI.Hint(f is { FromMonthly: true }
                ? L.S(meter.Utility == Utility.Nesco ? "monthlyNote" : "monthlyNoteFallback")
                : meter.Utility == Utility.Nesco ? L.S("noDaily") : L.S("needMoreData")));
            // "+ Add reading" is a NESCO-only lever: NESCO publishes no daily feed,
            // so manual readings are the ONLY way to build a daily series. On DESCO
            // the rate comes from the utility's own figures, and surfacing an "add
            // reading" action there makes an API-fed account read as if the user has
            // to do the portal's job. Store.AddReading stays callable from Profile.
            if (meter.Utility == Utility.Nesco)
            {
                var add = UI.Ghost("＋ " + L.S("addReading"));
                add.Clicked += async (_, _) => { await AddReading(meter); Refresh(); };
                _body.Add(add);
            }
        }

        // ---- recent recharges ----
        if (snap is { Recharges.Count: > 0 })
        {
            var rhead = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            rhead.Add(UI.Section(L.S("recentRecharge")), 0, 0);
            var seeAllR = new Button { Text = L.S("seeAll"), FontSize = 13, TextColor = Theme.Green, BackgroundColor = Colors.Transparent, Padding = 0 };
            seeAllR.Clicked += async (_, _) => await Shell.Current.GoToAsync("//usage");
            rhead.Add(seeAllR, 1, 0);
            _body.Add(rhead);

            var card = UI.Card();
            var rs = new VerticalStackLayout { Spacing = 0 };
            foreach (var r in snap.Recharges
                         .OrderByDescending(x => x.Date, StringComparer.Ordinal)
                         .ThenByDescending(x => x.Time, StringComparer.Ordinal)
                         .Take(3))
            {
                // Home's rows were inert: RechargeRowView only attaches a gesture when it is
                // given one, and Home never passed it. Same modal as Usage — a person who taps
                // a recharge on the first screen they see means the same thing there.
                var row = r;   // capture per-iteration for the tap closure
                rs.Add(RechargeRowView(row, showStatus: !row.Success || row.MultiToken,
                    state: Reconcile.StateOf(meter.Id, row),
                    onTap: () => _ = Modal.Details(Root, L.S("rechargeDetails"), DetailRows(row))));
                rs.Add(UI.Rule());
            }
            card.Content = rs;
            _body.Add(card);
        }

        _body.Add(UI.Hint(L.S("unofficial")));
    }

    /// <summary>The green band, greeting, gear, and the balance card floating over it.</summary>
    void RenderHeader(Meter meter, Snapshot? snap, Forecast? f, bool low)
    {
        // Layered gradient with two soft light circles for depth — a flat two-stop
        // fill read as a plain rectangle.
        var decor = new Grid();
        decor.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Colors.White.WithAlpha(0.07f)),
            WidthRequest = 220,
            HeightRequest = 220,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            TranslationX = 70,
            TranslationY = -90
        });
        decor.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Colors.White.WithAlpha(0.05f)),
            WidthRequest = 140,
            HeightRequest = 140,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.End,
            TranslationX = -50,
            TranslationY = 60
        });

        var band = new Border
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb("#27A06A"), 0f),
                    new GradientStop(Color.FromArgb("#1A7A50"), 0.55f),
                    new GradientStop(Color.FromArgb("#0F5B3C"), 1f)
                },
                new Point(0, 0), new Point(0.9, 1)),
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(0, 0, 30, 30) },
            // Bottom edge lands ~30pt below the balance card's top — just under
            // its label row, exactly where it was marked in review. Tracks the
            // same padding expression as the content so the two never drift.
            HeightRequest = 96 + Math.Max(StatusBarPad - 36, 12),
            VerticalOptions = LayoutOptions.Start,
            Content = decor   // clipped to the rounded band
        };
        _header.Add(band);

        var name = snap?.CustomerName is { Length: > 0 } n ? Titlecase(n) : (meter.Label.Length > 0 ? meter.Label : meter.Account);

        var greeting = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),   // avatar
                new ColumnDefinition(GridLength.Star),   // welcome + name
                new ColumnDefinition(GridLength.Auto)    // gear
            }
        };
        greeting.Add(Avatar(name), 0, 0);
        greeting.Add(new VerticalStackLayout
        {
            Spacing = 1,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = Salutation(), TextColor = Color.FromArgb("#BFE6D4"), FontSize = 13 },
                new Label { Text = name, TextColor = Colors.White, FontSize = 22, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 }
            }
        }, 1, 0);
        greeting.Add(GearButton(), 2, 0);

        var content = new VerticalStackLayout
        {
            Spacing = 12,
            // Measured from a marked device screenshot: ~38pt of dead green sat
            // between the status bar and the greeting. Subtract exactly that,
            // with a floor so no device can push the greeting into the clock.
            Padding = new Thickness(16, Math.Max(StatusBarPad - 36, 12), 16, 14),
            Children = { greeting, BalanceCard(meter, snap, f, low) }
        };
        _header.Add(content);
    }

    /// <summary>
    /// Circle with the first letter of the name, like every wallet app. When no
    /// customer name has synced yet the "name" is the account number — a lone
    /// digit in a circle looks like a badge count, so show the app bolt instead.
    /// </summary>
    static View Avatar(string name)
    {
        var initial = name.Length > 0 && char.IsLetter(name[0]) ? name[..1].ToUpperInvariant() : "⚡";
        return new Border
        {
            WidthRequest = 44,
            HeightRequest = 44,
            StrokeShape = new RoundRectangle { CornerRadius = 22 },
            Stroke = Colors.White.WithAlpha(0.35f),
            StrokeThickness = 1.5,
            BackgroundColor = Colors.White.WithAlpha(0.16f),
            Content = new Label
            {
                Text = initial,
                TextColor = Colors.White,
                FontSize = 19,
                FontAttributes = FontAttributes.Bold,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            }
        };
    }

    /// <summary>Good morning / afternoon / evening beats a static "Welcome".</summary>
    static string Salutation() => DateTime.Now.Hour switch
    {
        >= 5 and < 12 => L.Bn ? "শুভ সকাল" : "Good morning",
        >= 12 and < 17 => L.Bn ? "শুভ অপরাহ্ন" : "Good afternoon",
        >= 17 and < 21 => L.Bn ? "শুভ সন্ধ্যা" : "Good evening",
        _ => L.Bn ? "শুভ রাত্রি" : "Good night"
    };

    /// <summary>
    /// The daily-average TripleStat cell, adaptive. When the all-days mean and the
    /// active-day mean agree to the taka (a week with no zero-consumption days), the
    /// cell shows one number and looks exactly like the pre-split design. When they
    /// disagree (any quiet day in the window), it stacks both under one shared label,
    /// with the active-day figure highlighted green — that's the number the days-left
    /// pill, bill estimate, and suggested-recharge all use.
    /// </summary>
    static View DailyAvgCell(Forecast? f) =>
        AverageCellCore(L.S("dailyAvg"),
                        f?.ActiveAvg, f?.SimpleAvg,
                        singleFallback: f?.ActiveAvg != null ? "৳ " + $"{f.ActiveAvg:0}" : "—");

    /// <summary>Same adaptive rule for the 7-day chart TripleStat's "গড়" cell.</summary>
    static View AverageCell(Forecast? f) =>
        AverageCellCore(L.S("average"),
                        f?.ActiveAvg, f?.SimpleAvg,
                        singleFallback: "৳ " + $"{(f?.ActiveAvg ?? f?.SimpleAvg ?? 0):0}");

    /// <summary>
    /// Shared: split into "all days / active days" if the two rounded figures differ
    /// AND the meter has at least one active day. Otherwise a single-value cell.
    ///
    /// The rounded-taka comparison is deliberate — a tiny EWMA-vs-flat-mean difference
    /// (৳57.8 vs ৳58.1) would show identical numbers on screen, and the split cell
    /// would look like the same number twice with two different labels.
    /// </summary>
    static View AverageCellCore(string label, double? active, double? allDays, string singleFallback)
    {
        if (active is > 0 && allDays is > 0 &&
            Math.Round(active.Value) != Math.Round(allDays.Value))
        {
            return UI.StackedCell(label,
                subALabel: L.S("avgAllDays"), valueA: "৳ " + $"{allDays:0}",
                subBLabel: L.S("avgActive"), valueB: "৳ " + $"{active:0}",
                highlightB: true);
        }
        return UI.Cell(label, singleFallback);
    }

    Border BalanceCard(Meter meter, Snapshot? snap, Forecast? f, bool low)
    {
        var card = UI.Card(Color.FromArgb("#18231E"), 24);
        // Compact per review: the card measured ~400pt tall on device.
        card.Padding = new Thickness(18, 14);
        card.Stroke = Color.FromArgb("#2C4A3C");   // green-tinted edge lifts it off the band
        card.Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.35f, Radius = 18, Offset = new Point(0, 8) };
        var hs = new VerticalStackLayout { Spacing = 9 };

        // Row 1 — label, account chip, refresh.
        var head = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            }
        };
        head.Add(new Label { Text = L.S("currentBalance"), TextColor = Theme.Muted, FontSize = 13, VerticalOptions = LayoutOptions.Center }, 0, 0);
        head.Add(AccountChip(meter), 1, 0);
        head.Add(SyncButton(meter, snap), 2, 0);
        hs.Add(head);

        // A DESCO meter that has never been signed in can show nothing at all. Say so
        // with an action rather than an empty card: the account is configured, the app
        // just needs a password it was never given.
        if (meter.Utility == Utility.Desco && !DescoApi.HasCredential(meter.Account))
        {
            hs.Add(new Label { Text = "৳ —", TextColor = Theme.Text, FontSize = 36, FontAttributes = FontAttributes.Bold });
            hs.Add(new Label { Text = L.S("authNeeded"), TextColor = Theme.Muted, FontSize = 12 });
            var signIn = UI.Ghost(L.S("authTitle"));
            signIn.HeightRequest = 38;
            signIn.FontSize = 13;
            signIn.Clicked += async (_, _) =>
            {
                if (await DescoSignIn.EnsureAsync(this, meter)) { _syncedThisLaunch = ""; await Sync(); }
                else Refresh();
            };
            hs.Add(signIn);
            card.Content = hs;
            return card;
        }

        // Row 2 — the balance itself, with a live estimate riding to its right when
        // the reading is stale enough to project past.
        if (snap is { BalanceReported: false })
        {
            hs.Add(new Label { Text = "৳ —", TextColor = Theme.Text, FontSize = 36, FontAttributes = FontAttributes.Bold });
            hs.Add(new Label { Text = L.S("balanceUnavailable"), TextColor = Theme.Muted, FontSize = 12 });
        }
        else
        {
            // The estimate may be carrying a failed recharge on trust (see
            // Reconcile.UnverifiedCredit). When it is, the chip says so — and the caption
            // names the figure, because "this number might be wrong" is not useful
            // without "by roughly this much".
            var unverified = Reconcile.UnverifiedCredit(meter.Id, snap);

            hs.Add(BalanceLine(f?.Balance ?? 0, Theme.Text,
                               Forecast.LiveEstimate(snap, f, meter.Id),
                               unverified > 0));

            if (unverified > 0)
                hs.Add(new Label
                {
                    Text = L.S("estProvisional", L.N($"{unverified:#,0.##}")),
                    TextColor = Theme.Amber,
                    FontSize = 10.5
                });
        }

        // Row 3 — days-left pill left, freshness right, under a hairline divider.
        hs.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#22322A") });
        var pillRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) } };
        if (f?.DaysLeft != null && snap is not { BalanceReported: false })
        {
            // A meter already in the red has no "days left" to report — saying
            // "0 days left" (and previously "-1 days left") is just noise.
            var overdrawn = f.Balance < 0;
            var days = $"{f.DaysLeft:0}";
            var pillText = overdrawn ? "⚠︎ " + L.S("rechargeNow")
                                     : (low ? "⚠︎ " : "")
                                       + L.S(days == "1" ? "willLast1" : "willLast", days);
            pillRow.Add(UI.Pill(pillText,
                low ? Color.FromArgb("#3A2420") : Color.FromArgb("#17342A"),
                low ? Theme.Red : Theme.Green), 0, 0);
        }
        if (snap != null)
            pillRow.Add(new Label
            {
                Text = Forecast.Freshness(snap),
                TextColor = Theme.Faint,
                FontSize = 12,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Center
            }, 1, 0);
        hs.Add(pillRow);

        // WHEN the meter was actually read, and how many distinct updates we've seen.
        // Our sync time says nothing; the portal's balance only moves when the meter
        // reports. Now doubly true on DESCO, where live reads are rationed — the figure
        // on screen is their cached one for most of the day by design.
        if (snap?.ReadingTime is { } readAt)
        {
            var count = Store.MeterUpdateCount(meter.Id);
            hs.Add(new Label
            {
                Text = L.S("meterReadAt", readAt.ToString("d MMM") + " " + L.T12(readAt.ToString("HH:mm")))
                       + (count > 0
                            ? "  ·  " + L.S(count == 1 ? "updateCount1" : "updateCount",
                                            L.N(count.ToString()))
                            : ""),
                TextColor = Theme.Faint,
                FontSize = 11
            });
        }

        if (_status.Length > 0)
        {
            // First line in colour; any probe log after it in a quiet mono block,
            // so a failed sync reads as information rather than a wall of red.
            var nl = _status.IndexOf('\n');
            var headline = nl < 0 ? _status : _status[..nl].Trim();
            var rest = nl < 0 ? "" : _status[nl..].Trim();

            hs.Add(new Label
            {
                Text = headline,
                TextColor = _status == L.S("syncing") || _status == L.S("offline") ? Theme.Muted : Theme.Red,
                FontSize = 12
            });
            if (rest.Length > 0)
            {
                var logBox = UI.Card(Theme.Card, 10);
                logBox.Padding = new Thickness(10, 8);
                logBox.Content = new Label { Text = rest, TextColor = Theme.Muted, FontSize = 10, FontFamily = "Menlo" };
                hs.Add(logBox);
            }
        }

        // Low balance: a proper amber banner, not a line of raw red text.
        if (low)
        {
            var warn = new Border
            {
                BackgroundColor = Color.FromArgb("#33240F"),
                Stroke = Color.FromArgb("#6B4A1F"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Padding = new Thickness(11, 7),
                Content = new Label
                {
                    Text = "⚠︎ " + WarningText(snap, f),
                    TextColor = Theme.Amber,
                    FontSize = 12
                }
            };
            hs.Add(warn);
        }

        // A failed recharge the meter has now DISPROVED. This is the only place the app
        // asks the user to go and do something with a keypad, so it earns its own banner
        // — and it carries the token itself, because "enter the token manually" without
        // the digits is advice nobody can act on.
        var unapplied = Reconcile.NeedsToken(meter.Id, snap);
        if (unapplied.Count > 0) hs.Add(TokenBanner(meter, unapplied[0]));

        card.Content = hs;
        return card;
    }

    /// <summary>The same message DESCO's own page shows when today's read is used up.</summary>
    Task ShowQuotaPopup(Meter meter, Snapshot? snap)
    {
        var when = snap?.NextEligible ?? DateTime.Today.AddDays(1);
        var meterNo = snap?.MeterNo is { Length: > 0 } mn ? mn : meter.Account;
        return Modal.Info(Root, L.S("quotaTitle"),
            L.S("quotaReachedMeter", meterNo,
                when.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Small ↻ in the balance card, for people who never discover pull-to-refresh.
    /// When DESCO says today's read is used up, the button goes quiet and a tap shows
    /// the same limit message the portal shows, instead of doing nothing.
    /// </summary>
    View SyncButton(Meter meter, Snapshot? snap)
    {
        var blocked = snap?.NextEligible is { } n && DateTime.Now < n;
        var icon = StrokeIcon(RefreshPath, blocked ? Theme.Faint : Theme.Green, 14, 1.6);

        var b = new Border
        {
            WidthRequest = 30,
            HeightRequest = 30,
            StrokeShape = new RoundRectangle { CornerRadius = 15 },
            Stroke = Theme.Border,
            StrokeThickness = 1,
            BackgroundColor = Theme.Card,
            Opacity = blocked ? 0.45 : 1,
            Content = new Grid { Children = { icon } }
        };

        // A sync takes seconds, and a dead icon makes the app look frozen. Refresh()
        // rebuilds this button, so if a sync is already running when it's rebuilt,
        // pick the spin straight back up.
        if (_syncing) Spin(icon);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (blocked)
            {
                await ShowQuotaPopup(meter, snap);
                return;
            }
            Invalidate();
            await Sync(userRequestedFresh: true);
        };
        b.GestureRecognizers.Add(tap);
        return b;
    }

    /// <summary>
    /// Turn the refresh icon for as long as the sync runs.
    ///
    /// The catch: SyncButton() calls this the moment the icon is CONSTRUCTED, which is
    /// before it is attached to the visual tree. At that point IsLoaded is false and
    /// RotateTo() is a no-op — so gating the loop on IsLoaded meant the loop never ran a
    /// single turn. Wait for the Loaded event instead, then spin.
    /// </summary>
    void Spin(View icon)
    {
        if (icon.IsLoaded) { SpinLoop(icon); return; }

        void OnLoaded(object? _, EventArgs __)
        {
            icon.Loaded -= OnLoaded;                  // one-shot
            if (_syncing) SpinLoop(icon);             // sync may have finished while we waited
        }
        icon.Loaded += OnLoaded;
    }

    /// <summary>
    /// One anti-clockwise turn per 900ms, linear so there's no stutter at the seam.
    /// The loop only re-checks _syncing on a turn boundary, so the icon always lands
    /// upright; the RotateTo(0) below is just a guard for the odd early exit.
    /// </summary>
    async void SpinLoop(View icon)
    {
        try
        {
            while (_syncing && icon.Parent != null)
            {
                // NEGATIVE, i.e. anti-clockwise. MAUI's positive Rotation is clockwise,
                // but the Tabler refresh glyph's two arrows both sweep anti-clockwise,
                // so turning it +360 dragged the arrowheads backwards against the
                // direction they point.
                await icon.RotateTo(-360, 900, Easing.Linear);
                icon.Rotation = 0;                    // -360° == 0°, so this is invisible
            }
            if (icon.Parent != null) await icon.RotateTo(0, 150, Easing.CubicOut);
        }
        catch (TaskCanceledException) { /* view went away mid-turn — fine */ }
    }

    View GearButton()
    {
        var b = UI.Card(Color.FromArgb("#2A9268"), 22);
        b.Padding = 0;
        b.WidthRequest = 44;
        b.HeightRequest = 44;
        b.Stroke = Color.FromArgb("#3FA97C");
        b.Content = StrokeIcon(GearOutlinePath, Colors.White, 21);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Shell.Current.GoToAsync("//profile");
        b.GestureRecognizers.Add(tap);
        return b;
    }

    // ---------------- onboarding ----------------

    void RenderOnboarding()
    {
        var utility = Utility.Desco;
        var error = new Label { Text = "", TextColor = Theme.Red, FontSize = 12, IsVisible = false };

        // The hero goes in the SCROLLING body, not the pinned header. Pinned, it ate
        // ~40% of the screen; the iOS number pad (which has no Done key) then covered
        // the Continue button with no way to scroll it into view.
        var hero = new Border
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb("#27A06A"), 0f),
                    new GradientStop(Color.FromArgb("#125E3F"), 1f)
                },
                new Point(0, 0), new Point(1, 1)),
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 24 },
            Margin = new Thickness(0, StatusBarPad - 12, 0, 4),
            Padding = new Thickness(20, 22),
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Label { Text = "⚡ " + L.S("app"), TextColor = Colors.White, FontSize = 20, FontAttributes = FontAttributes.Bold },
                    new Label { Text = L.S("tagline"), TextColor = Color.FromArgb("#C9E8D8"), FontSize = 12 },
                    new Label { Text = L.S("heroTitle"), TextColor = Colors.White, FontSize = 28, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 12, 0, 0) },
                    new Label { Text = L.S("heroSub"), TextColor = Color.FromArgb("#C9E8D8"), FontSize = 13 }
                }
            }
        };
        _body.Add(hero);

        _body.Add(UI.Eyebrow(L.S("chooseUtility")));

        // DESCO now needs a portal account as well as a meter number, and a first-run
        // user has no way to know that. The note appears only on the DESCO side, so a
        // NESCO user isn't warned about a step they'll never take.
        var descoNote = new Label
        {
            Text = L.S("authRegisterFirst"),
            TextColor = Theme.Faint,
            FontSize = 11,
            IsVisible = true
        };

        var toggle = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) } };
        Button descoBtn = new() { Text = "⚡ DESCO", HeightRequest = 56, CornerRadius = 14, FontAttributes = FontAttributes.Bold, BorderColor = Theme.Border, BorderWidth = 1 };
        Button nescoBtn = new() { Text = "⚡ NESCO", HeightRequest = 56, CornerRadius = 14, FontAttributes = FontAttributes.Bold, BorderColor = Theme.Border, BorderWidth = 1 };
        void Paint()
        {
            descoBtn.BackgroundColor = utility == Utility.Desco ? Theme.Green : Theme.Card;
            descoBtn.TextColor = utility == Utility.Desco ? Theme.Ink : Theme.Muted;
            nescoBtn.BackgroundColor = utility == Utility.Nesco ? Theme.Green : Theme.Card;
            nescoBtn.TextColor = utility == Utility.Nesco ? Theme.Ink : Theme.Muted;
            descoNote.IsVisible = utility == Utility.Desco;
        }
        descoBtn.Clicked += (_, _) => { utility = Utility.Desco; Paint(); };
        nescoBtn.Clicked += (_, _) => { utility = Utility.Nesco; Paint(); };
        Paint();
        toggle.Add(descoBtn, 0, 0);
        toggle.Add(nescoBtn, 1, 0);
        _body.Add(toggle);
        _body.Add(descoNote);

        _body.Add(new Label { Text = L.S("enterAccount"), TextColor = Theme.Text, FontSize = 19, FontAttributes = FontAttributes.Bold });
        _body.Add(new Label { Text = L.S("enterAccountSub"), TextColor = Theme.Muted, FontSize = 13 });

        var account = UI.Field(L.S("egAccount"), null, Keyboard.Numeric);
        var box = UI.Card(Theme.Card, 14);
        box.Content = account;
        _body.Add(box);
        _body.Add(error);

        var go = UI.Accent(L.S("continue") + "  →");

        async Task Submit()
        {
            var acct = (account.Text ?? "").Trim();
            if (acct.Length < 6 || !acct.All(char.IsDigit))
            {
                error.Text = "⚠︎ " + L.S("invalidAccount");
                error.IsVisible = true;
                box.Stroke = Theme.Red;
                return;
            }
            account.Unfocus();

            var meter = new Meter { Account = acct, Utility = utility };
            Store.AddMeter(meter);
            _syncedThisLaunch = "";
            Refresh();

            // DESCO: collect the portal sign-in before the first fetch, so the very
            // first thing a new user sees is a balance rather than an auth error.
            // Sync() would prompt anyway, but prompting here keeps the onboarding flow
            // in one continuous sequence.
            if (utility == Utility.Desco)
            {
                var added = Store.Meters().FirstOrDefault(m => m.Account == acct && m.Utility == utility);
                if (added != null && !await DescoSignIn.EnsureAsync(this, added)) { Refresh(); return; }
            }

            await Sync();
        }

        go.Clicked += async (_, _) => await Submit();
        account.Completed += async (_, _) => await Submit();   // hardware / non-numeric keyboards
        _body.Add(go);

        _body.Add(new Label { Text = "🔒 " + L.S("trustRow"), TextColor = Theme.Faint, FontSize = 11, HorizontalOptions = LayoutOptions.Center });

        // Room to scroll the button well above the keyboard...
        _body.Add(new BoxView { HeightRequest = 300, Color = Colors.Transparent });

        // ...and do that scroll automatically the moment the field is focused,
        // because the iOS number pad has no Done key to dismiss itself.
        account.Focused += async (_, _) =>
        {
            await Task.Delay(250);   // let the keyboard animation start first
            try { await _scroll.ScrollToAsync(go, ScrollToPosition.Center, true); } catch { }
        };

        // Tapping anywhere outside the field puts the keyboard away.
        var dismiss = new TapGestureRecognizer();
        dismiss.Tapped += (_, _) => account.Unfocus();
        hero.GestureRecognizers.Add(dismiss);
    }

    // ---------------- pieces ----------------

    /// <summary>
    /// The amber banner's text: the situation first, then whatever hard numbers the
    /// utility actually gave us for this meter. Every clause is conditional — a figure
    /// the portal didn't publish simply doesn't appear.
    /// </summary>
    static string WarningText(Snapshot? snap, Forecast? f)
    {
        var parts = new List<string> { L.S(f is { Balance: < 0 } ? "negWarning" : "lowWarning") };

        // ONLY what the portal published. There used to be a fallback to a figure the
        // user typed into Profile, but that setting is gone. If the utility didn't
        // publish it, the clause is omitted — an unknown number is not worth guessing at.
        if (snap?.EmergencyBalance is { } ec)
            parts.Add(L.S("emergencyCredit", L.N($"{ec:#,0.##}")));
        if (snap?.MinRecharge is { } min)
            parts.Add(L.S("minRecharge", L.N($"{min:#,0.##}")));

        return string.Join(" ", parts);
    }

    /// <summary>
    /// The big balance number, with an optional live-estimate chip riding to its
    /// right. The chip sits vertically centred on the big number so the eye reads
    /// them as a pair — measurement + projection — rather than as separate rows.
    ///
    /// A negative balance means the meter is running on emergency credit — the one
    /// number in this app that is genuinely bad news — so it overrides whatever
    /// colour the caller asked for and goes red. The minus sits ahead of the ৳ so it
    /// reads as "minus 86.67 taka", not as a stray dash after the currency mark.
    /// </summary>
    static View BalanceLine(double amount, Color color, double? estimate = null, bool provisional = false)
    {
        var negative = amount < 0;
        if (negative) color = Theme.Red;

        var text = $"{Math.Abs(amount):#,0.00}";
        var sign = negative ? "-৳" : "৳";
        var size = (text.Length + sign.Length) switch { <= 8 => 34.0, <= 11 => 30.0, <= 14 => 26.0, _ => 22.0 };

        var row = new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = sign, TextColor = color, FontSize = size * 0.62, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center },
                new Label { Text = text, TextColor = color, FontSize = size, FontAttributes = FontAttributes.Bold, FontFamily = "Menlo", LineBreakMode = LineBreakMode.NoWrap, MaxLines = 1, VerticalOptions = LayoutOptions.Center }
            }
        };

        // Colour the chip by what it tells the user relative to the big number:
        //   • measured POS, estimate POS → muted grey; both agree in spirit.
        //   • measured POS, estimate NEG → RED; the day's consumption has already
        //     pushed you into emergency credit even though the midnight snapshot
        //     still shows positive.
        //   • measured NEG, estimate POS → GREEN; a recharge landed AFTER the last
        //     read, so the red big number is stale bad news.
        //   • measured NEG, estimate NEG → RED; still on emergency credit, and the
        //     current debt is DEEPER than the midnight number shows.
        //
        // Sign detection happens AFTER rounding — otherwise a projection of −0.4 would
        // show as "≈ -৳0" in red, a sign and colour attached to a displayed zero.
        if (estimate is { } est)
        {
            var estRounded = Math.Round(est);
            var estNegative = estRounded < 0;

            var chipColor = estNegative ? Theme.Red
                           : negative ? Theme.Green
                           : Theme.Muted;

            // An estimate carrying an unverified recharge is a different KIND of number
            // from one built on measured figures alone. The ordinary projection drifts by
            // a fraction of a day's burn; this one can be several hundred taka wrong in a
            // single step. Red still wins — a negative estimate is the more urgent fact.
            if (provisional && !estNegative) chipColor = Theme.Amber;

            var absText = $"{Math.Abs(estRounded):#,0}";
            var chipText = L.S(estNegative ? "liveEstimateInlineNeg" : "liveEstimateInline",
                               L.N(absText));

            row.Children.Add(new Label
            {
                Text = chipText,
                TextColor = chipColor,
                FontSize = 14,
                FontAttributes = FontAttributes.Bold,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.NoWrap,
                MaxLines = 1,
                Margin = new Thickness(10, 0, 0, 0)   // breathing room from the big number
            });

            if (provisional)
                row.Children.Add(new Label
                {
                    Text = "•",
                    TextColor = Theme.Amber,
                    FontSize = 20,
                    FontAttributes = FontAttributes.Bold,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(2, 0, 0, 0)
                });
        }

        return row;
    }

    /// <summary>
    /// The manual-token prompt, raised when Reconcile has watched a meter read go by
    /// without the balance moving. Tapping it opens the same detail modal as the recharge
    /// row: the token is the one field most people need, but the rest of the transaction
    /// is what they'll want if they end up having to call the utility.
    /// </summary>
    View TokenBanner(Meter meter, RechargeRow r)
    {
        var stack = new VerticalStackLayout { Spacing = 7 };

        stack.Add(new Label
        {
            Text = "⚠︎ " + L.S("tokenNotApplied"),
            TextColor = Theme.Red,
            FontSize = 12.5,
            FontAttributes = FontAttributes.Bold
        });
        stack.Add(new Label
        {
            Text = L.S("tokenNotAppliedSub", L.N($"{r.Amount:#,0.##}")),
            TextColor = Theme.Muted,
            FontSize = 11
        });

        // AllTokens, not TokenOf. A meter key-change carries a SET — twelve in the
        // captured data — and every one has to be keyed, in order. Showing the first
        // and dropping eleven is worse than showing none, because the user would think
        // they were done.
        var tokens = Reconcile.AllTokens(r);
        if (tokens.Count > 0)
        {
            if (tokens.Count > 1)
                stack.Add(new Label
                {
                    Text = L.S("tokenSetSub"),
                    TextColor = Theme.Faint,
                    FontSize = 10.5
                });

            var box = UI.Card(Color.FromArgb("#0A0F0C"), 8);
            box.Padding = new Thickness(10, 7);
            box.Stroke = Colors.Transparent;
            var list = new VerticalStackLayout { Spacing = 3 };
            for (int i = 0; i < tokens.Count; i++)
            {
                list.Add(new Label
                {
                    // Menlo, grouped in fours. Twenty unbroken digits in a proportional
                    // font is not keyable, and losing your place means starting again.
                    // Numbered when there's more than one, so the user can keep count.
                    Text = (tokens.Count > 1 ? $"{i + 1}.  " : "") + tokens[i],
                    TextColor = Theme.Amber,
                    FontSize = 13,
                    FontFamily = "Menlo",
                    FontAttributes = FontAttributes.Bold
                });
            }
            box.Content = list;
            stack.Add(box);
        }
        else
        {
            stack.Add(new Label
            {
                Text = L.S("tokenFindInDetails"),
                TextColor = Theme.Faint,
                FontSize = 10.5
            });
        }

        // Dismissal is the user's call, not ours. The verdict itself is never revisited,
        // but whether the prompt still needs to be on screen is something only they know:
        // the app cannot see a keypad.
        var done = UI.Ghost(L.S("tokenEntered"));
        done.HeightRequest = 36;
        done.FontSize = 12;
        done.Clicked += (_, _) => { Reconcile.Dismiss(meter.Id, r); Invalidate(); Refresh(); };
        stack.Add(done);

        var b = new Border
        {
            BackgroundColor = Color.FromArgb("#33201C"),
            Stroke = Color.FromArgb("#7A3A31"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(12, 10),
            Content = stack
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => _ = Modal.Details(Root, L.S("rechargeDetails"), DetailRows(r));
        b.GestureRecognizers.Add(tap);
        return b;
    }

    View AccountChip(Meter meter)
    {
        var chip = UI.Card(Theme.Card, 16);
        chip.Padding = new Thickness(12, 6);
        // The chevron used to be the character "⌄" inside the label text. A glyph sits
        // on the TEXT BASELINE, not on the row's centre line, so it always rode low.
        // Draw it instead, and centre it against the number.
        chip.Content = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label
                {
                    Text = $"A/C {meter.Account}", TextColor = Theme.Muted, FontSize = 12,
                    VerticalOptions = LayoutOptions.Center
                },
                StrokeIcon(ChevronDownPath, Theme.Muted, 9, 2.2)
            }
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            var meters = Store.Meters();
            if (meters.Count < 2) { await Shell.Current.GoToAsync("//profile"); return; }

            var names = meters.Select(m => (m.Label.Length > 0 ? m.Label + " · " : "") + m.Account).ToList();
            var idx = await Modal.Choose(Root, L.S("yourAccounts"), names, meters.FindIndex(m => m.Id == meter.Id));
            if (idx == null) return;

            if (meters[idx.Value].Id == meter.Id) return;   // same account: nothing to rebuild

            Store.SelectedMeterId = meters[idx.Value].Id;
            _syncedThisLaunch = "";

            // Sync() opens by setting _status = "syncing" and calling Refresh(), which
            // repaints the whole page for the new account anyway. Calling Refresh() here
            // too rebuilt the entire tree twice in the same UI-thread turn.
            if (_syncing) Refresh();
            await Sync(silent: true);
        };
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    static View ActionTile(View icon, string label, Action onTap)
    {
        // Circular icon buttons with the label beneath — the flat rectangular
        // tiles read as placeholders; circles read as actions.
        var circle = new Border
        {
            WidthRequest = 58,
            HeightRequest = 58,
            StrokeShape = new RoundRectangle { CornerRadius = 29 },
            BackgroundColor = Theme.Card2,
            Stroke = Theme.Border,
            StrokeThickness = 1,
            HorizontalOptions = LayoutOptions.Center,
            Content = new Grid { Children = { icon } }
        };

        var stack = new VerticalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                circle,
                new Label { Text = label, TextColor = Theme.Text, FontSize = 13, HorizontalTextAlignment = TextAlignment.Center }
            }
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            // A quick press acknowledgment; silent taps feel broken.
            await circle.ScaleTo(0.9, 70, Easing.CubicOut);
            await circle.ScaleTo(1, 90, Easing.CubicOut);
            onTap();
        };
        stack.GestureRecognizers.Add(tap);
        return stack;
    }

    // ---- drawn icons, all in the mockup's Tabler-outline style: thin stroke,
    //      round joins, no heavy fills ----

    /// <summary>Tabler "credit-card": outlined card with the stripe line.</summary>
    public static View CardIcon() => StrokeIcon(
        "M3 8a3 3 0 0 1 3 -3h12a3 3 0 0 1 3 3v8a3 3 0 0 1 -3 3h-12a3 3 0 0 1 -3 -3z M3 10l18 0 M7 15l.01 0 M11 15l2 0",
        Theme.Green, 26);

    /// <summary>Tabler "chart-bar": three outlined ascending bars.</summary>
    public static View BarsIcon() => StrokeIcon(
        "M4 13a1 1 0 0 1 1 -1h3a1 1 0 0 1 1 1v6a1 1 0 0 1 -1 1h-3a1 1 0 0 1 -1 -1z " +
        "M10 9a1 1 0 0 1 1 -1h3a1 1 0 0 1 1 1v10a1 1 0 0 1 -1 1h-3a1 1 0 0 1 -1 -1z " +
        "M16 4a1 1 0 0 1 1 -1h3a1 1 0 0 1 1 1v15a1 1 0 0 1 -1 1h-3a1 1 0 0 1 -1 -1z",
        Theme.Green, 26);

    /// <summary>Tabler "sparkles": one large curved four-point star with a small companion.</summary>
    public static View SparkIcon() => StrokeIcon(
        "M9 18a6 6 0 0 1 6 -6a6 6 0 0 1 -6 -6a6 6 0 0 1 -6 6a6 6 0 0 1 6 6z " +
        "M17.5 8.5a3 3 0 0 1 3 -3a3 3 0 0 1 -3 -3a3 3 0 0 1 -3 3a3 3 0 0 1 3 3z",
        Theme.Amber, 26, 1.6);

    /// <summary>Banknote: rounded rect with a centre coin, for money contexts.</summary>
    public static View MoneyIcon()
    {
        var inner = new Grid();
        inner.Add(new Microsoft.Maui.Controls.Shapes.Ellipse
        {
            Fill = new SolidColorBrush(Theme.Ink.WithAlpha(0.45f)),
            WidthRequest = 9,
            HeightRequest = 9,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        });
        return new Border
        {
            WidthRequest = 32,
            HeightRequest = 20,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            Stroke = Colors.Transparent,
            BackgroundColor = Theme.Green,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = inner
        };
    }

    /// <summary>SVG path → filled MAUI shape, centred in its slot.</summary>
    static View VectorIcon(string pathData, Color color, double size) => new Path
    {
        // The converter's signature is object?, but it only returns null for
        // unparseable input — and ours are compile-time constants. The bang
        // silences CS8600 without changing behaviour.
        Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(pathData)!,
        Fill = new SolidColorBrush(color),
        Aspect = Stretch.Uniform,
        WidthRequest = size,
        HeightRequest = size,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center
    };

    /// <summary>
    /// Stroked (outline) icon — the Tabler look: thin consistent line, round
    /// joins, no fill. Filled glyphs read as heavy blobs at small sizes.
    /// </summary>
    static View StrokeIcon(string pathData, Color color, double size, double thickness = 1.7) => new Path
    {
        Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(pathData)!,
        Stroke = new SolidColorBrush(color),
        StrokeThickness = thickness,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round,
        Fill = new SolidColorBrush(Colors.Transparent),
        Aspect = Stretch.Uniform,
        WidthRequest = size,
        HeightRequest = size,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center
    };

    /// <summary>Tabler "settings": outline gear with a centre ring.</summary>
    const string GearOutlinePath =
        "M10.325 4.317c.426 -1.756 2.924 -1.756 3.35 0a1.724 1.724 0 0 0 2.573 1.066" +
        "c1.543 -.94 3.31 .826 2.37 2.37a1.724 1.724 0 0 0 1.065 2.572c1.756 .426 1.756 2.924 0 3.35" +
        "a1.724 1.724 0 0 0 -1.066 2.573c.94 1.543 -.826 3.31 -2.37 2.37a1.724 1.724 0 0 0 -2.572 1.065" +
        "c-.426 1.756 -2.924 1.756 -3.35 0a1.724 1.724 0 0 0 -2.573 -1.066c-1.543 .94 -3.31 -.826 -2.37 -2.37" +
        "a1.724 1.724 0 0 0 -1.065 -2.572c-1.756 -.426 -1.756 -2.924 0 -3.35a1.724 1.724 0 0 0 1.066 -2.573" +
        "c-.94 -1.543 .826 -3.31 2.37 -2.37c1 .608 2.296 .07 2.572 -1.065z " +
        "M9 12a3 3 0 1 0 6 0a3 3 0 1 0 -6 0";

    /// <summary>Tabler "chevron-down". Drawn, not typed — see AccountChip.</summary>
    const string ChevronDownPath = "M6 9l6 6l6 -6";

    /// <summary>Tabler "refresh": two curved arrows.</summary>
    const string RefreshPath =
        "M20 11a8.1 8.1 0 0 0 -15.5 -2m-.5 -4v4h4 M4 13a8.1 8.1 0 0 0 15.5 2m.5 4v-4h-4";

    public static View RechargeRowView(RechargeRow r, bool showStatus = false,
                                       CreditState? state = null, Action? onTap = null)
    {
        var g = new Grid
        {
            Padding = new Thickness(0, 10),
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(new GridLength(36)), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        // A key-change set is neither a success nor a failure — it's a different kind of
        // transaction, so it gets its own mark rather than borrowing one that implies a
        // verdict we deliberately don't make.
        var glyph = r.MultiToken ? "⚿" : r.Success ? "⊕" : "✕";
        var glyphColor = r.MultiToken ? Theme.Muted : r.Success ? Theme.Green : Theme.Red;
        g.Add(new Label { Text = glyph, TextColor = glyphColor, FontSize = 17, VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.Center }, 0, 0);

        var mid = new VerticalStackLayout { Spacing = 2 };
        // Payable (gross) amount, with the electricity actually credited INLINE
        // after it: "৳ 500.00 · ⚡ ৳ 354.36". Spans keep the numbers in Menlo
        // while the ⚡ and separators use the system font (Menlo has no Bangla
        // or emoji glyphs).
        var amount = new FormattedString();
        amount.Spans.Add(new Span { Text = "৳ ", TextColor = Theme.Text, FontSize = 15, FontAttributes = FontAttributes.Bold });
        amount.Spans.Add(new Span { Text = $"{r.Amount:#,0.00}", TextColor = Theme.Text, FontSize = 15, FontAttributes = FontAttributes.Bold, FontFamily = "Menlo" });
        // Show the electricity-credited chip whenever the utility reported one — DESCO
        // reports a would-have-been energyAmount on failed rows too, which is genuinely
        // useful, but must not READ as "money that reached the meter". Colour carries
        // the outcome: green for money that arrived, muted grey for money that didn't.
        if (r.EnergyAmount > 0 && Math.Abs(r.EnergyAmount - r.Amount) > 0.005)
        {
            var chipColor = r.Success && !r.MultiToken ? Theme.Green : Theme.Muted;
            amount.Spans.Add(new Span { Text = "  ·  ", TextColor = Theme.Faint, FontSize = 13 });
            amount.Spans.Add(new Span { Text = "⚡ ৳ ", TextColor = chipColor, FontSize = 13 });
            amount.Spans.Add(new Span { Text = $"{r.EnergyAmount:#,0.00}", TextColor = chipColor, FontSize = 13, FontFamily = "Menlo" });
        }
        mid.Add(new Label { FormattedText = amount, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 });
        mid.Add(new Label
        {
            Text = r.Date + (r.Time.Length > 0 ? "  " + L.T12(r.Time) : "") + (r.Via.Length > 0 ? "  ·  " + r.Via : ""),
            TextColor = Theme.Faint,
            FontSize = 11
        });
        g.Add(mid, 1, 0);

        if (showStatus)
        {
            // "Failed" is the PORTAL's word, and on its own it misleads in both
            // directions: it covers money that arrived perfectly well, and it covers a
            // token still sitting unused in the transaction details. Report what WE know
            // instead — and say "verifying" for as long as that is the truthful answer.
            var st = state ?? (r.MultiToken ? CreditState.Informational
                             : r.Success ? CreditState.Credited : CreditState.Unverified);

            string text; Color bg, fg;
            if (st == CreditState.Informational)
            {
                text = L.S("tokenSetTitle", L.N(r.Tokens.Length.ToString()));
                bg = Color.FromArgb("#1E2630"); fg = Theme.Muted;
            }
            else if (r.Success) { text = L.S("success"); bg = Color.FromArgb("#17342A"); fg = Theme.Green; }
            else if (st == CreditState.Credited) { text = L.S("reachedMeter"); bg = Color.FromArgb("#17342A"); fg = Theme.Green; }
            else if (st == CreditState.NotCredited) { text = L.S("addToken"); bg = Color.FromArgb("#3A2420"); fg = Theme.Red; }
            else { text = L.S("verifying"); bg = Color.FromArgb("#2A2418"); fg = Theme.Amber; }

            g.Add(UI.Pill(text, bg, fg), 2, 0);
        }

        if (onTap != null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => onTap();
            g.GestureRecognizers.Add(tap);
        }
        return g;
    }

    /// <summary>
    /// Rows for the detail modal: the summary fields first, then everything
    /// the utility reported verbatim. Values are tidied here too, because
    /// snapshots cached by older builds may contain leaked HTML-attribute
    /// junk (class='…' data-token='…') from before the parser fix.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> DetailRows(RechargeRow r)
    {
        yield return new(L.S("recharge"), "৳ " + $"{r.Amount:#,0.00}");
        // The electricity value the utility calculated — shown on failed rows too,
        // because the Status field below already tells the reader whether it landed.
        if (r.EnergyAmount > 0)
            yield return new(L.S("electricity"), "৳ " + $"{r.EnergyAmount:#,0.00}");
        // The demand charge is a real deduction levied on the first recharge of a
        // month. Itemising it is what makes "I paid ৳400 and got ৳214 of power" stop
        // looking like a mistake.
        if (r.DemandCharge > 0)
            yield return new(L.Bn ? "ডিমান্ড চার্জ" : "Demand charge", "৳ " + $"{r.DemandCharge:#,0.00}");
        yield return new(L.Bn ? "তারিখ" : "Date",
            r.Date + (r.Time.Length > 0 ? "  " + L.T12(r.Time) : ""));
        if (r.Via.Length > 0) yield return new(L.Bn ? "মাধ্যম" : "Via", r.Via);
        yield return new(L.Bn ? "অবস্থা" : "Status", r.Success ? L.S("success") : L.S("failed"));

        // Every token, grouped and numbered. A key-change set carries a dozen; the
        // Details dictionary below would show them as one unreadable comma-run.
        var tokens = Reconcile.AllTokens(r);
        if (tokens.Count == 1)
            yield return new(L.Bn ? "টোকেন" : "Token", tokens[0]);
        else
            for (int i = 0; i < tokens.Count; i++)
                yield return new((L.Bn ? "টোকেন " : "Token ") + L.N((i + 1).ToString()), tokens[i]);

        foreach (var kv in r.Details)
        {
            // The raw token field is already rendered above, grouped. Skipping it here
            // avoids showing the same digits twice, once unreadably.
            if (kv.Key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 || kv.Key.Contains("টোকেন"))
                continue;
            var v = System.Text.RegularExpressions.Regex.Replace(kv.Value, @"\s+", " ").Trim();
            if (v.Length == 0 || v.Length > 200) continue;
            if (v.Contains("='") || v.Contains("=\"")) continue;   // HTML attributes from old caches
            yield return new(kv.Key, Token(kv.Key, v));
        }
    }

    /// <summary>
    /// The recharge token is the one value here a person actually retypes — into the
    /// meter keypad, 20 digits, standing at the wall. DESCO's own receipt prints it
    /// 1246-1502-1501-0810-5738; the API returns it as one unbroken run. Only touch a
    /// value that is all digits and long enough to be a token — a Seq No of "16" or an
    /// Order ID must pass through untouched.
    ///
    /// Still used for NESCO, whose detail keys may carry a token the Tokens array
    /// doesn't (its HTML table is scraped, not parsed from JSON).
    /// </summary>
    static string Token(string key, string value)
    {
        if (key.IndexOf("token", StringComparison.OrdinalIgnoreCase) < 0 && !key.Contains("টোকেন"))
            return value;
        if (value.Length < 16 || !value.All(char.IsDigit)) return value;

        var groups = Enumerable.Range(0, (value.Length + 3) / 4)
                               .Select(i => value.Substring(i * 4, Math.Min(4, value.Length - i * 4)));
        return string.Join("-", groups);
    }

    // One implementation, in Modal.AskReading — see the note there on why these two
    // copies of the prompt could not be allowed to differ.
    async Task AddReading(Meter meter) => await Modal.AskReading(Root, meter);

    static string Titlecase(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
}