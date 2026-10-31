using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace PrepaidTracker;

/// <summary>The Usage tab: 7 / 30 / 365-day windows, totals, chart, recharge history.</summary>
public class UsagePage : AppPage
{
    readonly VerticalStackLayout _body = new() { Spacing = 14, Padding = new Thickness(16, 16, 16, 40) };
    int _window;   // 0 = 7 days, 1 = 30 days, 2 = year
    bool _showAllRecharges;   // history: 10 rows until "see more"

    public UsagePage()
    {
        Title = L.S("usage");
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
        return string.Join('|', m.Id, _window, _showAllRecharges, Store.Readings(m.Id).Count, snap?.SyncedAt.Ticks ?? 0, Store.Language);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (ShouldRender(StateToken())) Render();
    }

    void Select(int window)
    {
        _window = window;
        Invalidate();
        Render();
    }

    void Render()
    {
        _body.Children.Clear();
        _body.Add(UI.Title(L.S("usageTitle")));

        var meter = Store.Selected();
        if (meter == null) { _body.Add(UI.Hint(L.S("noMeters"))); return; }

        var snap = Store.Snapshot(meter.Id);
        var f = Forecast.Build(Store.Readings(meter.Id), snap);

        // What we can actually plot. ChartDays hides a trailing "today" row that is still
        // ৳0 (the utility hasn't reported today yet) so it doesn't draw as a zero stub.
        var daily = f?.ChartDays() ?? new List<DayRow>();
        var months = f?.CompleteMonths() ?? new List<MonthRow>();
        var hasDaily = daily.Count > 1;

        // The 7/30/Year pills only mean something when a daily series exists. NESCO
        // publishes months and nothing else, and the old build still drew all three
        // pills, quietly substituted the SAME six monthly bars into each, and then
        // captioned them with day words — "Total spend ৳8,230" over a 7-day tab, where
        // the 8,230 was half a year and the "highest day" was a month. Three tabs, one
        // chart, wrong labels. If there is no daily feed, say so once and show the
        // monthly view on its own terms.
        if (hasDaily)
            _body.Add(UI.Segmented(new[] { L.S("7days"), L.S("30days"), L.S("year") }, _window, Select));
        else
            _window = 2;

        List<DayRow> rows;
        UI.ChartLabels labels;

        if (_window == 2 || !hasDaily)
        {
            rows = months.TakeLast(12).Select(m => new DayRow { Date = m.Month + "-01", Taka = m.Taka }).ToList();
            labels = UI.ChartLabels.Month;
        }
        else
        {
            rows = daily.TakeLast(_window == 0 ? 7 : 30).ToList();
            labels = _window == 0 ? UI.ChartLabels.Weekday : UI.ChartLabels.DayNumber;
        }

        if (rows.Count == 0)
        {
            // No chart yet — but that must NOT hide the recharge history below.
            // (DESCO smart meters can return recharges with no consumption feed.)
            _body.Add(EmptyState(meter, f));
        }
        else
        {
            bool monthly = labels == UI.ChartLabels.Month;

            // Stats come from the REAL rows only — the padding slots below must not drag
            // the total, average, or highest down. For daily windows we compute BOTH
            // averages: the plain mean (allDaysAvg) and the active-day mean (activeAvg),
            // then let the UI decide whether they differ enough to warrant the split
            // display (see UI.StackedCell / the adaptive branch below). Monthly rows
            // stay on the plain mean — a zero-consumption month is a genuinely extreme
            // event, and there's nothing to disambiguate.
            var total = rows.Sum(r => r.Taka);
            double allDaysAvg;
            double? activeAvg = null;
            if (monthly)
                allDaysAvg = rows.Average(r => r.Taka);
            else
            {
                allDaysAvg = rows.Average(r => r.Taka);
                var active = rows.Where(r => r.Taka > 0).ToList();
                activeAvg = active.Count > 0 ? active.Average(r => r.Taka) : (double?)null;
            }
            var peak = rows.OrderByDescending(r => r.Taka).First();

            var totalCell = UI.Cell(monthly ? L.S("latest") : L.S("totalSpend"),
                                    "৳ " + L.N(monthly ? $"{rows[^1].Taka:#,0}" : $"{total:#,0}"));
            var peakCell = UI.Cell(monthly ? L.S("highest") : L.S("highestDay"),
                                   "৳ " + L.N($"{peak.Taka:#,0}"));

            View avgCell;
            var avgLabel = monthly ? L.S("average") : L.S("dailyAvg");
            if (!monthly && activeAvg is > 0 &&
                Math.Round(activeAvg.Value) != Math.Round(allDaysAvg))
            {
                // Two figures on screen actually differ → show both, active one green.
                avgCell = UI.StackedCell(avgLabel,
                    subALabel: L.S("avgAllDays"), valueA: "৳ " + L.N($"{allDaysAvg:#,0}"),
                    subBLabel: L.S("avgActive"), valueB: "৳ " + L.N($"{activeAvg:#,0}"),
                    highlightB: true);
            }
            else
            {
                // Same number under both labels would just be noise — collapse.
                var singleValue = activeAvg ?? allDaysAvg;
                avgCell = UI.Cell(avgLabel, "৳ " + L.N($"{singleValue:#,0}"));
            }

            _body.Add(UI.TripleStatMixed(totalCell, avgCell, peakCell));

            _body.Add(UI.Hint(L.N(monthly ? peak.Date[..7] : peak.Date)));

            // Daily views fill a fixed number of slots (7 or 30) so a handful of days read
            // as a chart filling up, not a couple of fat blocks. Days we don't have yet — and
            // days that are simply missing in the middle of the feed — draw as dashed
            // placeholders in their calendar position, so the weekday strip stays aligned to
            // the real days. Monthly (yearly) view pads the same way: two or three months of
            // history otherwise render as a couple of fat blocks filling the whole chart.
            // Padding to 12 slots keeps them at a sensible height with room for more history
            // to fill in on future syncs. (A lone in-progress month can no longer reach this
            // point — CompleteMonths excludes it, so the empty state handles that case and
            // the stats above can't degenerate into "Latest ৳549 / Average ৳549 / Highest ৳549".)
            var chartRows = monthly ? UI.PadLeft(rows, 12) : UI.LastNCalendarDays(rows, _window == 0 ? 7 : 30);

            var chart = UI.Card();
            chart.Content = UI.BarChart(chartRows, labels, height: 140);
            _body.Add(chart);

            // No daily feed. Two very different situations land here:
            //   • NESCO: portal design — no daily figure is ever published, and the
            //     ONE lever a NESCO user has is to log a balance reading. Two of
            //     those on separate days and the daily chart appears.
            //   • DESCO: the utility's daily endpoint either returned nothing or
            //     returned values our sanity check discarded as untrustworthy. The
            //     rate has fallen back to `currentMonthConsumption ÷ days`, and there
            //     is nothing the user can do about it — asking them to add readings
            //     would just make a DESCO account read like a NESCO account. Show
            //     the fallback hint instead and skip the button.
            if (!hasDaily)
            {
                if (meter.Utility == Utility.Nesco)
                {
                    _body.Add(UI.Hint(L.S("noDaily")));
                    var add = UI.Ghost("＋ " + L.S("addReading"));
                    add.Clicked += async (_, _) => { await AddReadingByHand(meter); };
                    _body.Add(add);
                }
                else
                {
                    _body.Add(UI.Hint(L.S("monthlyNoteFallback")));
                }
            }
        }

        if (snap is { Recharges.Count: > 0 })
        {
            _body.Add(UI.Section(L.S("rechargeHistory")));
            var card = UI.Card();
            var rs = new VerticalStackLayout { Spacing = 0 };

            var ordered = snap.Recharges
                .OrderByDescending(x => x.Date, StringComparer.Ordinal)
                .ThenByDescending(x => x.Time, StringComparer.Ordinal)
                .ToList();
            var shown = _showAllRecharges ? ordered : ordered.Take(10).ToList();

            foreach (var r in shown)
            {
                var row = r;   // capture per-iteration for the tap closure
                rs.Add(HomePage.RechargeRowView(row, showStatus: true,
                    state: Reconcile.StateOf(meter.Id, row),
                    onTap: () => _ = Modal.Details(Root, L.S("rechargeDetails"), HomePage.DetailRows(row))));
                rs.Add(UI.Rule());
            }
            card.Content = rs;
            _body.Add(card);

            if (ordered.Count > 10)
            {
                var toggle = UI.Ghost(_showAllRecharges
                    ? L.S("seeLess")
                    : L.S("seeMore") + $" ({ordered.Count - 10})");
                toggle.Clicked += (_, _) =>
                {
                    _showAllRecharges = !_showAllRecharges;
                    Invalidate();
                    Render();
                };
                _body.Add(toggle);
            }
        }
        else
        {
            _body.Add(UI.Section(L.S("rechargeHistory")));
            _body.Add(UI.Hint(L.S("noRecharges")));
        }
    }

    View EmptyState(Meter meter, Forecast? f)
    {
        var empty = UI.Card();
        var es = new VerticalStackLayout { Spacing = 10 };
        es.Add(new Label { Text = L.S("noDataYet"), TextColor = Theme.Text, FontSize = 16, FontAttributes = FontAttributes.Bold });
        es.Add(new Label
        {
            Text = meter.Utility == Utility.Nesco ? L.S("noDaily") : L.S("descoNoData"),
            TextColor = Theme.Muted, FontSize = 13
        });

        // Same NESCO/DESCO split as under the chart above: only NESCO users
        // have anything to gain from typing a reading in by hand.
        if (meter.Utility == Utility.Nesco)
        {
            var add = UI.Ghost("＋ " + L.S("addReading"));
            add.Clicked += async (_, _) => { await AddReadingByHand(meter); };
            es.Add(add);
        }

        empty.Content = es;
        return empty;
    }

    /// <summary>
    /// The manual reading. It is the only lever a NESCO customer has: the portal
    /// publishes no daily figure, so the daily chart can only be built out of balances
    /// the app has seen. Two readings on separate days and the chart appears.
    /// </summary>
    async Task AddReadingByHand(Meter meter)
    {
        // Same prompt as Home, from one place. This path used to ask for the balance only,
        // leaving Reading.Recharge at 0 — which made every interval containing a top-up
        // come out negative and get dropped from the daily series entirely.
        if (!await Modal.AskReading(Root, meter)) return;
        Invalidate();
        Render();
    }
}
