using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace PrepaidTracker;

public class App : Application
{
    public App()
    {
        // The whole app is dark; without this, native transition surfaces
        // follow the system theme and can flash white between tabs.
        UserAppTheme = AppTheme.Dark;
        Resources = new ResourceDictionary();

        // Installs made before AddMeter deduped by account number can be carrying two or
        // three rows for the same meter, each with its own alert slot and its own set of
        // 9am reminders. That is the duplicate-notification bug; this clears it out once.
        // Before Gate.Root(), so the first frame already draws the cleaned-up list rather
        // than showing the same account twice until something forces a refresh.
        Store.DedupeMeters();

        // Then sweep any OS-held reminders whose meter is no longer in the store. Without
        // this, deleting or replacing an account (e.g. A/C 14024778 swapped for 14024780)
        // leaves a queue of 9am reminders that keep firing under the old name for days,
        // and any banners already on the lock screen keep sitting there. Runs on every
        // launch, so the ghost queue clears itself the next time the app opens.
        Notifier.SweepOrphans();

        // Not `new AppShell()` any more: a device with no username yet, or one the owner
        // has blocked, never gets as far as the tabs. See Access.cs.
        MainPage = Gate.Root();

        Notifier.RequestPermission();
        Access.StartWatch();
    }

    // The 5-second poll only makes sense while someone is looking at the screen. iOS
    // suspends the process shortly after OnSleep anyway; leaving the timer running just
    // burns battery and wakes the server for nothing.
    protected override void OnResume() { base.OnResume(); Access.StartWatch(); }
    protected override void OnSleep()  { base.OnSleep();  Access.StopWatch(); }
}

public class AppShell : Shell
{
    public AppShell()
    {
        BackgroundColor = Theme.Bg;   // the container behind page transitions
        SetBackgroundColor(this, Theme.Bg);
        SetTitleColor(this, Theme.Text);
        SetForegroundColor(this, Theme.Text);
        SetTabBarBackgroundColor(this, Theme.TabBar);
        SetTabBarTitleColor(this, Theme.Green);
        SetTabBarUnselectedColor(this, Theme.Muted);
        SetNavBarIsVisible(this, false);

        var tabs = new TabBar();
        tabs.Items.Add(Tab(L.S("home"),     "home",     "tab_home.png",     () => new HomePage()));
        tabs.Items.Add(Tab(L.S("usage"),    "usage",    "tab_usage.png",    () => new UsagePage()));
        tabs.Items.Add(Tab(L.S("insights"), "insights", "tab_insights.png", () => new InsightsPage()));
        tabs.Items.Add(Tab(L.S("profile"),  "profile",  "tab_profile.png",  () => new ProfilePage()));
        Items.Add(tabs);
    }

    // Resizetizer turns Resources/Images/*.svg into these PNGs at build time.
    static ShellContent Tab(string title, string route, string icon, Func<Page> page) => new()
    {
        Title = title,
        Route = route,
        Icon = ImageSource.FromFile(icon),
        ContentTemplate = new DataTemplate(page)
    };

    /// <summary>Rebuild the root (language change). Goes through the gate, so a blocked
    /// device that changes a setting doesn't get restarted straight into the tabs.</summary>
    public static void Restart() => Application.Current!.MainPage = Gate.Root();
}

public static class Theme
{
    public static readonly Color Bg      = Color.FromArgb("#0C1210");
    public static readonly Color Card    = Color.FromArgb("#141C19");
    public static readonly Color Card2   = Color.FromArgb("#1B2622");
    public static readonly Color TabBar  = Color.FromArgb("#101614");
    public static readonly Color Border  = Color.FromArgb("#233029");
    public static readonly Color Text    = Color.FromArgb("#E8F0EC");
    public static readonly Color Muted   = Color.FromArgb("#8B9A93");
    public static readonly Color Faint   = Color.FromArgb("#5F6E67");
    public static readonly Color Green   = Color.FromArgb("#2FBD7E");
    public static readonly Color Hero    = Color.FromArgb("#1E7A54");
    public static readonly Color Amber   = Color.FromArgb("#F5C451");
    public static readonly Color Red     = Color.FromArgb("#FF6B5E");
    public static readonly Color Ink     = Color.FromArgb("#08130D");
}

public static class UI
{
    public static Border Card(Color? bg = null, int radius = 16) => new()
    {
        BackgroundColor = bg ?? Theme.Card,
        Stroke = Theme.Border,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = radius },
        Padding = 16
    };

    public static Label Title(string t) => new()
    { Text = t, TextColor = Theme.Text, FontSize = 24, FontAttributes = FontAttributes.Bold };

    public static Label Section(string t) => new()
    { Text = t, TextColor = Theme.Text, FontSize = 16, FontAttributes = FontAttributes.Bold };

    public static Label Eyebrow(string t) => new()
    { Text = t, TextColor = Theme.Muted, FontSize = 12 };

    public static Label Hint(string t) => new() { Text = t, TextColor = Theme.Faint, FontSize = 11 };

    public static Label Money(double? v, double size = 18, Color? color = null) => new()
    {
        Text = v == null ? "—" : "৳ " + L.N($"{v:#,0.##}"),
        TextColor = color ?? Theme.Text,
        FontSize = size,
        FontAttributes = FontAttributes.Bold,
        FontFamily = "Menlo"
    };

    public static Button Primary(string text) => new()
    {
        Text = text, BackgroundColor = Theme.Green, TextColor = Theme.Ink,
        FontAttributes = FontAttributes.Bold, CornerRadius = 12, HeightRequest = 50
    };

    public static Button Accent(string text) => new()
    {
        Text = text, BackgroundColor = Theme.Amber, TextColor = Theme.Ink,
        FontAttributes = FontAttributes.Bold, CornerRadius = 12, HeightRequest = 52, FontSize = 16
    };

    public static Button Ghost(string text) => new()
    {
        Text = text, BackgroundColor = Theme.Card2, TextColor = Theme.Muted,
        BorderColor = Theme.Border, BorderWidth = 1, CornerRadius = 12, HeightRequest = 44, FontSize = 13
    };

    public static Entry Field(string placeholder, string? value = null, Keyboard? kb = null) => new()
    {
        Text = value ?? "", Placeholder = placeholder, Keyboard = kb ?? Keyboard.Default,
        TextColor = Theme.Text, PlaceholderColor = Theme.Faint, BackgroundColor = Colors.Transparent, FontSize = 17
    };

    public static BoxView Rule() => new() { HeightRequest = 1, Color = Theme.Border };

    /// <summary>Three metrics side by side, divided — the pattern used all over PrepaidPro.</summary>
    public static Border TripleStat(params (string Label, string Value)[] cells)
    {
        var views = new View[cells.Length];
        for (int i = 0; i < cells.Length; i++) views[i] = Cell(cells[i].Label, cells[i].Value);
        return TripleStatMixed(views);
    }

    /// <summary>
    /// A single stat cell — label on top, value below. The plain building block
    /// TripleStat's tuple overload wraps around every entry.
    /// </summary>
    public static View Cell(string label, string value) => new VerticalStackLayout
    {
        Spacing = 4,
        HorizontalOptions = LayoutOptions.Center,
        Children =
        {
            new Label { Text = label, TextColor = Theme.Muted, FontSize = 11, HorizontalTextAlignment = TextAlignment.Center },
            new Label { Text = value, TextColor = Theme.Text, FontSize = 16, FontAttributes = FontAttributes.Bold, FontFamily = "Menlo", HorizontalTextAlignment = TextAlignment.Center }
        }
    };

    /// <summary>
    /// A cell that shows two mini-values under one shared label, each with its own
    /// sub-caption to disambiguate. Used for the "সব দিন / সক্রিয় দিন" split on the
    /// monthly summary and the 7-day chart's TripleStat when the two averages
    /// diverge (which they only do when the window contains a zero-consumption day).
    /// When they don't diverge, callers pass <see cref="Cell"/> instead — one number,
    /// no visual clutter, exact same look as the pre-split design. See HomePage for
    /// the adaptive branch that picks between the two.
    /// </summary>
    public static View StackedCell(string label,
                                   string subALabel, string valueA,
                                   string subBLabel, string valueB,
                                   bool highlightB = false)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 2,
            HorizontalOptions = LayoutOptions.Center
        };
        stack.Add(new Label { Text = label, TextColor = Theme.Muted, FontSize = 11, HorizontalTextAlignment = TextAlignment.Center });

        // Each mini-row is `<tiny sub-label>  <bold value>` on a single line. Rendered
        // centred as a HorizontalStackLayout so short labels don't drift left-aligned.
        View MiniRow(string sub, string value, bool highlight)
        {
            var color = highlight ? Theme.Green : Theme.Text;
            var subColor = highlight ? Theme.Green : Theme.Faint;
            return new HorizontalStackLayout
            {
                Spacing = 6,
                HorizontalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label { Text = sub, TextColor = subColor, FontSize = 9, VerticalOptions = LayoutOptions.Center },
                    new Label { Text = value, TextColor = color, FontSize = 14, FontAttributes = FontAttributes.Bold, FontFamily = "Menlo", VerticalOptions = LayoutOptions.Center }
                }
            };
        }

        stack.Add(MiniRow(subALabel, valueA, highlight: false));
        stack.Add(MiniRow(subBLabel, valueB, highlight: highlightB));
        return stack;
    }

    /// <summary>
    /// Three arbitrary cell views side by side, with vertical dividers between them.
    /// Same visual as <see cref="TripleStat"/>, but the caller decides what each cell
    /// contains — <see cref="Cell"/> for a plain label+value, <see cref="StackedCell"/>
    /// for the adaptive split.
    /// </summary>
    public static Border TripleStatMixed(params View[] cells)
    {
        var card = Card();
        var grid = new Grid();
        for (int i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            if (i < cells.Length - 1) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
        }
        for (int i = 0; i < cells.Length; i++)
        {
            grid.Add(cells[i], i * 2, 0);
            if (i < cells.Length - 1)
                grid.Add(new BoxView { Color = Theme.Border, WidthRequest = 1 }, i * 2 + 1, 0);
        }
        card.Content = grid;
        return card;
    }

    public enum ChartLabels { Weekday, Month, DayNumber }

    /// <summary>
    /// Bar chart: gradient bars, weekday/month labels, and a callout over the selected
    /// bar. The newest bar is selected on open; tapping any bar moves the callout to it.
    ///
    /// Painted on a canvas rather than laid out. The previous version built each bar as
    /// a contentless Border in a Star-sized Grid column, and a Border with no Content
    /// desires 0 x 0 — so when Android measured the grid with an unconstrained width the
    /// Star columns collapsed to that desired width and every bar arranged about a pixel
    /// wide. iOS never took that measure path, which is why it only showed on device.
    /// Arithmetic has no measure pass to lose, and it is what makes hit-testing a tap
    /// back to a bar index a two-line job rather than a gesture per Border.
    /// </summary>
    /// <summary>
    /// Right-align the days we have inside a fixed number of slots, filling the empty
    /// left with dashed placeholders. Two real days in a 7-slot chart draw as five empty
    /// outlines then two bars, so the chart keeps a constant width and visibly fills in as
    /// history accumulates instead of stretching a couple of fat bars across the card.
    /// </summary>
    public static List<DayRow> PadLeft(List<DayRow> days, int slots)
    {
        if (days.Count >= slots) return days.TakeLast(slots).ToList();
        var padded = new List<DayRow>(slots);
        for (int i = 0; i < slots - days.Count; i++) padded.Add(new DayRow { Placeholder = true });
        padded.AddRange(days);
        return padded;
    }

    /// <summary>
    /// Turn a daily series into exactly N slots ending on the most recent date it
    /// carries, with dashed Placeholder rows inserted for calendar days the series
    /// doesn't cover. Fixes a chart bug <see cref="PadLeft"/> couldn't: PadLeft only
    /// pads the LEADING edge, so a seven-row series whose dates spanned nine calendar
    /// days (a feed with two missing days in the middle) drew seven consecutive bars
    /// as if the week had no gaps. The weekday strip then read wrong ("F S S W T F S"
    /// where a real week ending Saturday would show "S M T W T F S") and the "7-day"
    /// window silently represented however many real days those rows actually spanned.
    ///
    /// Use this — not <see cref="PadLeft"/> — for any daily chart that means to show
    /// a fixed calendar window (7 or 30 days). PadLeft is still right for MONTHLY
    /// charts and for series whose slot count is decoupled from calendar spacing.
    /// </summary>
    public static List<DayRow> LastNCalendarDays(List<DayRow> days, int n)
    {
        if (n <= 0) return new List<DayRow>();
        var real = days.Where(d => !d.Placeholder && d.Date.Length >= 10).ToList();
        if (real.Count == 0) return PadLeft(new List<DayRow>(), n);   // nothing yet — leading dashes

        var byDate = new Dictionary<string, DayRow>(real.Count);
        foreach (var r in real) byDate[r.Date] = r;                    // last-wins on any dupes

        var last = Forecast.Date(real[^1].Date);
        var result = new List<DayRow>(n);
        for (int i = n - 1; i >= 0; i--)
        {
            var day = last.AddDays(-i).ToString("yyyy-MM-dd");
            result.Add(byDate.TryGetValue(day, out var row)
                ? row
                : new DayRow { Date = day, Placeholder = true });
        }
        return result;
    }

    public static View BarChart(List<DayRow> days, ChartLabels mode = ChartLabels.Weekday,
                                double height = 130, bool showAverage = false)
    {
        if (days.Count == 0) return new Label { Text = "—", TextColor = Theme.Faint };

        var drawable = new BarsDrawable(days, mode, (float)height, showAverage);
        var view = new GraphicsView
        {
            Drawable = drawable,
            HeightRequest = height + BarsDrawable.BubbleH + BarsDrawable.LabelStrip + 6,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Start
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, e) =>
        {
            // Hit-test on x alone. Insisting the tap land inside the drawn bar would make
            // a short bar (a cheap day) nearly untappable, and the column is unambiguous.
            var p = e.GetPosition(view);
            if (p == null || view.Width <= 0) return;
            if (drawable.SelectAt((float)p.Value.X, (float)view.Width)) view.Invalidate();
        };
        view.GestureRecognizers.Add(tap);

        return view;
    }

    sealed class BarsDrawable : IDrawable
    {
        public const float BubbleH   = 48f;   // callout above the plot
        public const float LabelStrip = 18f;  // weekday / month row under the plot
        const float MinBarH = 14f;            // a near-zero day still reads as a bar, not a line

        readonly List<DayRow> _days;
        readonly ChartLabels _mode;
        readonly float _plotH;
        readonly bool _showAverage;
        int _selected;                        // newest bar on open

        // Tighter spacing (and less corner rounding) as the bar count climbs, so a 30-slot
        // chart reads as a dense row rather than slivers marooned in fat gaps.
        float Gap    => _days.Count > 20 ? 3f : _days.Count > 10 ? 5f : 8f;
        float Radius => _days.Count > 20 ? 3f : _days.Count > 10 ? 5f : 7f;

        public BarsDrawable(List<DayRow> days, ChartLabels mode, float plotH, bool showAverage)
        {
            _days = days;
            _mode = mode;
            _plotH = plotH;
            _showAverage = showAverage;
            // Open on the newest REAL bar — placeholders sit on the left, so the last
            // index is normally real, but guard in case the list is all placeholders.
            _selected = days.FindLastIndex(d => !d.Placeholder);
            if (_selected < 0) _selected = days.Count - 1;
        }

        int FirstReal() { var i = _days.FindIndex(d => !d.Placeholder); return i < 0 ? 0 : i; }

        /// <summary>Tap → bar index. Returns true when the selection actually moved.</summary>
        public bool SelectAt(float x, float width)
        {
            var n = _days.Count;
            var barW = Math.Max(3f, (width - Gap * (n - 1)) / n);
            var i = (int)Math.Floor(x / (barW + Gap));
            i = Math.Clamp(i, FirstReal(), n - 1);   // never land on an empty slot
            if (i == _selected) return false;
            _selected = i;
            return true;
        }

        static string Money(DayRow d) => "৳ " + $"{d.Taka:#,0}";

        /// <summary>
        /// A monthly bar carries a synthetic day ("2026-06-01"); printing "2026-06" made
        /// the reader parse a number back into a month, so name it. A daily bar is a real
        /// day and keeps the full date — "June 2026" would lose which day it was.
        /// </summary>
        string Caption(DayRow d) => _mode == ChartLabels.Month
            ? Forecast.Date(d.Date).ToString("MMMM yyyy")
            : d.Date;

        public void Draw(ICanvas g, RectF rect)
        {
            if (rect.Width <= 0 || _days.Count == 0) return;

            var n    = _days.Count;
            var real = _days.Where(d => !d.Placeholder).ToList();
            var max  = Math.Max(real.Count > 0 ? real.Max(d => d.Taka) : 0, 0.01);
            var mean = real.Count > 0 ? real.Average(d => d.Taka) : 0;

            var plotTop    = rect.Y + BubbleH;
            var plotBottom = plotTop + _plotH;
            var usableH    = _plotH - 8f;
            var barW       = Math.Max(3f, (rect.Width - Gap * (n - 1)) / n);

            // ---- bars ----
            for (int i = 0; i < n; i++)
            {
                var slot = new RectF(rect.X + i * (barW + Gap), 0, barW, 0);

                // Empty slot: a dashed outline sitting on the baseline, no fill. Marks a
                // day we simply don't have yet, so the chart reads as "filling up".
                if (_days[i].Placeholder)
                {
                    var stubH = Math.Max(MinBarH, usableH * 0.16f);
                    var ph = new RectF(slot.X, plotBottom - stubH, barW, stubH);
                    g.SaveState();
                    g.StrokeColor = Theme.Border;
                    g.StrokeSize = 1.4f;
                    g.StrokeDashPattern = new float[] { 3, 3 };
                    g.DrawRoundedRectangle(ph, Radius);
                    g.RestoreState();
                    continue;
                }

                bool on = i == _selected;
                var h = (float)Math.Max(MinBarH, _days[i].Taka / max * usableH);
                var bar = new RectF(slot.X, plotBottom - h, barW, h);

                // Lighter at the top, deeper at the base — reads as volume, not a block.
                // The selected bar runs a shade brighter, so the callout has an anchor.
                g.SetFillPaint(new LinearGradientPaint
                {
                    StartColor = Color.FromArgb(on ? "#5FE0A6" : "#37AE7B"),
                    EndColor   = Color.FromArgb(on ? "#33BE85" : "#2A8C63"),
                    StartPoint = new Microsoft.Maui.Graphics.Point(0, 0),
                    EndPoint   = new Microsoft.Maui.Graphics.Point(0, 1)
                }, bar);
                g.FillRoundedRectangle(bar, Radius);
            }

            // ---- optional dashed mean line ----
            if (_showAverage)
            {
                var meanY = plotBottom - (float)(mean / max * usableH);
                g.SaveState();
                g.StrokeColor = Theme.Faint.WithAlpha(0.55f);
                g.StrokeSize = 1;
                g.StrokeDashPattern = new float[] { 4, 4 };
                g.DrawLine(rect.X, meanY, rect.Right - 34f, meanY);
                g.RestoreState();

                g.FontSize = 9;
                g.FontColor = Theme.Faint;
                g.DrawString(L.S("average"), rect.Right - 32f, meanY - 6f, 32f, 12f,
                    HorizontalAlignment.Right, VerticalAlignment.Center);
            }

            // ---- callout, centred over the plot ----
            var sel     = _days[_selected];
            var money   = Money(sel);
            var caption = Caption(sel);

            // Width is measured across EVERY row, not the selected one: sizing it to the
            // current label made the pill grow and shrink on each tap — "May 2026" narrow,
            // "January 2026" wide. Measure once, size for the worst case, hold still.
            var widest = 0;
            for (int i = 0; i < n; i++)
            {
                if (_days[i].Placeholder) continue;
                widest = Math.Max(widest, Math.Max(Money(_days[i]).Length, Caption(_days[i]).Length));
            }

            var bubbleW = Math.Min(rect.Width, Math.Max(90f, widest * 8.2f + 24f));
            var bubble  = new RectF(rect.X + (rect.Width - bubbleW) / 2f, rect.Y, bubbleW, BubbleH - 6f);

            g.FillColor = Theme.Hero;
            g.FillRoundedRectangle(bubble, 10);

            // No Font.DefaultBold anywhere near this. Assigning it silently painted
            // nothing on the Android canvas — the amount vanished while the caption, drawn
            // with Font.Default, came out fine. Size and colour carry the emphasis instead.
            g.Font = Microsoft.Maui.Graphics.Font.Default;

            // TextFlow.OverflowBounds, and a rect with room to spare. DrawString defaults
            // to ClipBounds: a 15px line in an 18px box is within a hair of its own line
            // height, so the amount was being clipped away to nothing while the 10px
            // caption in a 14px box had room and survived. That is why the bubble showed a
            // month and no money, whatever font it was drawn with.
            g.FontSize = 15;
            g.FontColor = Colors.White;
            g.DrawString(money, bubble.X, bubble.Y + 2f, bubble.Width, 24f,
                HorizontalAlignment.Center, VerticalAlignment.Center, TextFlow.OverflowBounds);

            g.FontSize = 10;
            g.FontColor = Color.FromArgb("#BFE6D4");
            g.DrawString(caption, bubble.X, bubble.Y + 24f, bubble.Width, 16f,
                HorizontalAlignment.Center, VerticalAlignment.Center, TextFlow.OverflowBounds);

            // ---- x labels ----
            g.FontSize = 10;
            for (int i = 0; i < n; i++)
            {
                // Old rule: skip labels on every placeholder. That was right when the
                // only source of placeholders was PadLeft (dateless leading slots), but
                // LastNCalendarDays now stamps gap-day placeholders with their real
                // date — so a missing Monday should still print "M" under its dashed
                // outline. Skip only the dateless (leading-pad) case.
                if (_days[i].Placeholder && _days[i].Date.Length < 10) continue;
                var d = Forecast.Date(_days[i].Date);
                var text = _mode switch
                {
                    ChartLabels.Month => d.ToString("MMM"),
                    ChartLabels.Weekday when n <= 10 => d.ToString("ddd")[..1],
                    _ => i % Math.Max(1, n / 6) == 0 ? d.Day.ToString() : ""
                };
                if (text.Length == 0) continue;

                // The selected label lifts out of the muted row, so a tap is acknowledged
                // even when the callout is clamped away from the bar it belongs to.
                g.FontColor = i == _selected ? Theme.Text : Theme.Faint;
                g.DrawString(text, rect.X + i * (barW + Gap), plotBottom + 4f, barW, LabelStrip,
                    HorizontalAlignment.Center, VerticalAlignment.Center);
            }
        }
    }

    /// <summary>Pill-style segmented control (7 days / 30 days / Year).</summary>
    public static View Segmented(string[] options, int selected, Action<int> onSelect)
    {
        var outer = Card(Theme.Card, 24);
        outer.Padding = 5;
        var grid = new Grid { ColumnSpacing = 4 };
        for (int i = 0; i < options.Length; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int i = 0; i < options.Length; i++)
        {
            bool on = i == selected;
            var b = new Button
            {
                Text = options[i], FontSize = 14, HeightRequest = 40, CornerRadius = 20,
                BackgroundColor = on ? Theme.Green : Colors.Transparent,
                TextColor = on ? Theme.Ink : Theme.Muted,
                FontAttributes = on ? FontAttributes.Bold : FontAttributes.None
            };
            var idx = i;
            b.Clicked += (_, _) => onSelect(idx);
            grid.Add(b, i, 0);
        }
        outer.Content = grid;
        return outer;
    }

    public static Border Pill(string text, Color bg, Color fg)
    {
        var b = Card(bg, 14);
        b.Padding = new Thickness(12, 6);
        b.Stroke = Colors.Transparent;
        b.HorizontalOptions = LayoutOptions.Start;
        b.VerticalOptions = LayoutOptions.Center;
        b.Content = new Label
        {
            Text = text, TextColor = fg, FontSize = 13, FontAttributes = FontAttributes.Bold,
            VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.NoWrap
        };
        return b;
    }

    /// <summary>
    /// Pill with a drawn status dot. The "●" text glyph is a full-height
    /// character that inflates the line box and sits off-baseline, which made
    /// the pill's padding look uneven — a 7px ellipse centres exactly.
    /// </summary>
    public static Border StatusPill(string text, Color bg, Color fg)
    {
        var b = Card(bg, 14);
        b.Padding = new Thickness(10, 6);
        b.Stroke = Colors.Transparent;
        b.HorizontalOptions = LayoutOptions.Start;
        b.VerticalOptions = LayoutOptions.Center;
        b.Content = new HorizontalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new Microsoft.Maui.Controls.Shapes.Ellipse
                {
                    WidthRequest = 7, HeightRequest = 7,
                    Fill = new SolidColorBrush(fg),
                    VerticalOptions = LayoutOptions.Center
                },
                new Label
                {
                    Text = text, TextColor = fg, FontSize = 13, FontAttributes = FontAttributes.Bold,
                    VerticalOptions = LayoutOptions.Center, VerticalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.NoWrap
                }
            }
        };
        return b;
    }
}
