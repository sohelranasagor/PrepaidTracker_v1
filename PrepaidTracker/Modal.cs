using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Graphics;
using System;

// iOSSpecific also defines a ScrollView (platform-config statics); alias the control.
using ScrollView = Microsoft.Maui.Controls.ScrollView;

namespace PrepaidTracker;

/// <summary>
/// Base page: a root Grid so modals can be layered over the content, plus render
/// gating so switching tabs doesn't tear down and rebuild the whole tree (which
/// is what made the screen flash).
/// </summary>
public abstract class AppPage : ContentPage
{
    public Grid Root { get; }
    string _lastToken = "\u0000";

    protected AppPage()
    {
        BackgroundColor = Theme.Bg;
        // Keep content clear of the status bar. HomePage opts out, because its
        // green header is meant to run behind it.
        On<iOS>().SetUseSafeArea(true);
        Content = Root;
    }

    protected void SetBody(View body)
    {
        if (Root.Children.Count == 0) Root.Add(body);
    }

    /// <summary>True only when something the UI depends on has actually changed.</summary>
    protected bool ShouldRender(string token)
    {
        if (token == _lastToken) return false;
        _lastToken = token;
        return true;
    }

    protected void Invalidate() => _lastToken = "\u0000";
}

/// <summary>
/// Dialogs drawn inside the app rather than handed to iOS. The native
/// DisplayAlert / DisplayPromptAsync / DisplayActionSheet look nothing like the
/// rest of this app, and DisplayPromptAsync in particular can't be styled at all.
/// </summary>
public static class Modal
{
    static readonly Color Scrim = Color.FromArgb("#CC050807");

    // ---------- public API ----------

    public static Task<bool> Confirm(Grid root, string title, string message,
        string confirmText, string cancelText, bool destructive = false)
    {
        var tcs = new TaskCompletionSource<bool>();
        var stack = Body(title, message);

        var confirm = UI.Primary(confirmText);
        if (destructive) { confirm.BackgroundColor = Theme.Red; confirm.TextColor = Colors.White; }
        var cancel = UI.Ghost(cancelText);

        Grid? overlay = null;
        confirm.Clicked += async (_, _) => { await Close(root, overlay!); tcs.TrySetResult(true); };
        cancel.Clicked += async (_, _) => { await Close(root, overlay!); tcs.TrySetResult(false); };

        stack.Add(confirm);
        stack.Add(cancel);

        overlay = Present(root, stack, () => tcs.TrySetResult(false));
        return tcs.Task;
    }

    /// <summary>
    /// Everything the utility reported for one recharge. Key sits ABOVE the value —
    /// side-by-side columns collided as soon as a Bengali header or a 20-digit
    /// token needed to wrap. The rows scroll inside the card; the Close button is
    /// pinned below them, so a long NESCO list can never push it out of reach.
    /// </summary>
    public static Task Details(Grid root, string title, IEnumerable<KeyValuePair<string, string>> rows)
    {
        var tcs = new TaskCompletionSource<bool>();
        var stack = Body(title, null);

        var list = new VerticalStackLayout { Spacing = 0 };
        foreach (var kv in rows)
        {
            // The token is the one value here that has to leave the screen: it gets
            // typed into the meter keypad, twenty digits, standing at the wall. Mono
            // so the groups line up, and tap-to-copy so it need not be typed at all.
            // Every row copies, because the account and meter numbers get retyped too
            // and a row that copies silently is cheaper than a row with a button on it.
            var value = new Label
            {
                Text = kv.Value, TextColor = Theme.Text, FontSize = 14,
                LineBreakMode = LineBreakMode.WordWrap,
                FontFamily = kv.Value.Any(char.IsDigit) && !kv.Value.Any(char.IsLetter) ? "Menlo" : null
            };

            var row = new VerticalStackLayout
            {
                Spacing = 2, Padding = new Thickness(0, 8),
                Children =
                {
                    new Label { Text = kv.Key, TextColor = Theme.Muted, FontSize = 11, LineBreakMode = LineBreakMode.WordWrap },
                    value
                }
            };

            var copy = new TapGestureRecognizer();
            var text = kv.Value;
            var label = kv.Key;
            copy.Tapped += async (_, _) =>
            {
                // Copy the digits, not the grouping — a meter keypad has no "-" key.
                await Clipboard.SetTextAsync(text.Replace("-", "").Replace(" ", ""));
                var was = value.TextColor;
                value.TextColor = Theme.Green;
                value.Text = label + " " + L.S("copied").ToLowerInvariant();
                await Task.Delay(900);
                value.Text = text;
                value.TextColor = was;
            };
            row.GestureRecognizers.Add(copy);

            list.Add(row);
            list.Add(UI.Rule());
        }

        // The rows scroll; the Close button does not. It goes to Present as a footer,
        // pinned under the scroll region, so a long NESCO recharge can't push it off.
        stack.Add(list);

        var ok = UI.Primary(L.S("close"));
        Grid? overlay = null;
        ok.Clicked += async (_, _) => { await Close(root, overlay!); tcs.TrySetResult(true); };

        overlay = Present(root, stack, () => tcs.TrySetResult(true), footer: ok);
        return tcs.Task;
    }

    public static Task Info(Grid root, string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var stack = Body(title, message);

        var ok = UI.Primary("OK");
        Grid? overlay = null;
        ok.Clicked += async (_, _) => { await Close(root, overlay!); tcs.TrySetResult(true); };
        stack.Add(ok);

        overlay = Present(root, stack, () => tcs.TrySetResult(true));
        return tcs.Task;
    }

    /// <summary>Returns the entered text, or null if dismissed.</summary>
    /// <summary>
    /// The manual reading, asked ONE way.
    ///
    /// There were two copies of this prompt and they had already drifted: Home asked for
    /// the balance AND anything recharged since the last reading, Usage asked only for the
    /// balance and left Reading.Recharge at 0. That difference is not cosmetic. The derived
    /// daily series computes a day's spend as
    ///
    ///     prevBalance + thisReading.Recharge − thisBalance
    ///
    /// so a reading entered from Usage after a ৳500 top-up produces a NEGATIVE spend,
    /// Forecast hits its "topped up off-book; skip" guard, and every day in that interval
    /// is silently dropped. On NESCO — where manual readings are the only source of a daily
    /// series at all, and where people recharge every week or two — that quietly deleted
    /// exactly the intervals that contained a recharge.
    ///
    /// Returns true when a reading was stored.
    /// </summary>
    public static async Task<bool> AskReading(Grid root, Meter meter)
    {
        var balStr = await Prompt(root, L.S("currentBalance"), L.S("balancePrompt"),
            placeholder: "412.50", keyboard: Keyboard.Numeric);
        if (string.IsNullOrWhiteSpace(balStr)) return false;
        if (!double.TryParse(balStr.Replace(",", ""), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var bal) || bal < 0)
            return false;

        var recStr = await Prompt(root, L.S("recharge"), L.S("rechargedSince"), "0",
            keyboard: Keyboard.Numeric);
        double.TryParse((recStr ?? "0").Replace(",", ""), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var rec);

        Store.AddReading(meter.Id, new Reading
        {
            Date = DateTime.Now.ToString("yyyy-MM-dd"),
            Balance = bal,
            Recharge = Math.Max(0, rec)
        });
        return true;
    }

    /// <summary>
    /// The per-account notification panel. Master switch at the top, then one expandable
    /// row per meter. Each row shows the meter's own effective ৳ and days thresholds
    /// alongside a switch — tap the switch to mute the meter, tap the row itself to
    /// reveal its threshold editors.
    ///
    /// Every change writes as it happens; there is no "save". A meter that gets turned
    /// off immediately loses any reminder the OS was already holding for it (via
    /// Notifier.CancelFor) — otherwise a user who flips the switch off is still ambushed
    /// by tomorrow morning's already-armed 9am banner. Turning a meter back on doesn't
    /// re-arm anything; the next sync will do that from a fresh forecast, which is safer
    /// than arming from stale numbers.
    ///
    /// When the master is off, per-account switches and editors are all dimmed —
    /// interacting with them would be theatre while the whole channel is muted. Turning
    /// the master ON asks the OS for notification permission and shows the "notifications
    /// are blocked" info modal if the OS refuses.
    /// </summary>
    public static Task NotificationSettings(Grid root)
    {
        var tcs = new TaskCompletionSource<bool>();
        var stack = Body(L.S("notification"), null);

        // --- master switch card ---
        var master = new Switch { IsToggled = Store.AlertsEnabled, OnColor = Theme.Green, ThumbColor = Colors.White };
        var masterCard = UI.Card();
        masterCard.Content = MakeSwitchRow(L.S("notifAllOn"), L.S("notifAllHint"), master);
        stack.Add(masterCard);

        // --- per-account list ---
        stack.Add(UI.Eyebrow(L.S("notifPerAccount")));
        var hint = new Label
        {
            Text = L.S("notifPerHint"),
            TextColor = Theme.Muted, FontSize = 11, Margin = new Thickness(4, -6, 4, 4),
            LineBreakMode = LineBreakMode.WordWrap
        };
        stack.Add(hint);

        var meters = Store.Meters();
        var listCard = UI.Card();
        listCard.Padding = new Thickness(0, 4);
        var list = new VerticalStackLayout { Spacing = 0 };

        Grid? overlayRef = null;
        var rowStates = new List<PerAcctRow>();

        for (int i = 0; i < meters.Count; i++)
        {
            var m = meters[i];
            var state = BuildAcctRow(root, m);
            rowStates.Add(state);
            list.Add(state.Container);
            if (i < meters.Count - 1) list.Add(UI.Rule());
        }

        if (meters.Count == 0)
        {
            list.Add(new Label
            {
                Text = L.S("addAccount"),
                TextColor = Theme.Muted, FontSize = 13,
                Padding = new Thickness(14, 14)
            });
        }
        listCard.Content = list;
        stack.Add(listCard);

        // Master OFF ⇒ dim everyone and disable their switches. Master ON ⇒ restore.
        void ApplyMaster(bool on)
        {
            foreach (var r in rowStates)
            {
                r.Switch.IsEnabled = on;
                r.Header.Opacity = on ? 1 : 0.45;
                if (r.Editor != null) r.Editor.Opacity = on ? 1 : 0.45;
            }
        }
        ApplyMaster(master.IsToggled);

        master.Toggled += (_, e) =>
        {
            if (Store.AlertsEnabled == e.Value) return;
            Store.AlertsEnabled = e.Value;
            ApplyMaster(e.Value);

            if (!e.Value)
            {
                // Global off: clear every meter's queue. A subsequent language switch or
                // threshold change would otherwise write into a stale queue.
                Notifier.CancelAllScheduled();
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    var granted = await Notifier.RequestPermissionAsync();
                    if (!granted)
                        await MainThread.InvokeOnMainThreadAsync(() =>
                            Info(root, L.S("notifBlockedTitle"), L.S("notifBlockedBody")));
                });
            }
        };

        // --- footer ---
        var done = UI.Primary(L.S("done"));
        done.Clicked += async (_, _) => { await Close(root, overlayRef!); tcs.TrySetResult(true); };

        overlayRef = Present(root, stack, () => tcs.TrySetResult(true), footer: done);
        return tcs.Task;
    }

    /// <summary>Wraps everything about one meter's row so the master toggle can dim it.</summary>
    class PerAcctRow
    {
        public required VerticalStackLayout Container;   // header + optional editor
        public required Grid Header;                     // badge + name + summary + switch
        public required Switch Switch;
        public Grid? Editor;                             // the expandable ৳/days panel
    }

    static PerAcctRow BuildAcctRow(Grid root, Meter m)
    {
        var snap = Store.Snapshot(m.Id);
        var name = snap?.CustomerName is { Length: > 0 } cn
            ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cn.ToLowerInvariant())
            : (m.Label.Length > 0 ? m.Label : m.Account);

        var sw = new Switch { IsToggled = m.NotificationsEnabled, OnColor = Theme.Green, ThumbColor = Colors.White };

        // Summary label updates when the user edits — bind it via a factory closure so
        // the editor callbacks below can call SetSummary() without hunting for the label.
        var summary = new Label
        {
            TextColor = Theme.Faint, FontSize = 12, VerticalOptions = LayoutOptions.Center
        };
        void RefreshSummary()
        {
            // Show the EFFECTIVE value (override if set, otherwise the app default) so the
            // user sees the number that will actually fire, not the zero-means-inherit
            // sentinel stored on disk.
            // Use NBn (not N) so digits render as Bengali numerals in Bengali mode —
            // this text reads as prose ("৳২০০ · ৩ দিন"), not as a copyable token like
            // an account number, so mixed-script "৳ 200 · 3দ" looks half-translated.
            // Full word "দিন" (not the one-letter "দ") reads as natural Bangla; add a
            // space before it since it's a word, not a suffix.
            summary.Text = $"৳ {L.NBn(m.EffectiveLowTaka.ToString())} · " +
                           $"{L.NBn(m.EffectiveLowDays.ToString())}{(L.Bn ? " দিন" : "d")}";
        }
        RefreshSummary();

        // ---- header row ----
        // Layout: [badge] [name / (account · summary)] [switch]
        // The summary was previously in its own column next to the switch, which stole
        // 60–80px from the name column and truncated "Md. Golam Warich" to "Md. Golam…".
        // Moving it to the meta line (right-aligned, next to the account number) lets
        // the name span the full remaining width.
        var header = new Grid
        {
            Padding = new Thickness(14, 12), ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(36)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            }
        };

        var badge = UI.Card(Color.FromArgb("#17342A"), 8);
        badge.Padding = 0;
        badge.HeightRequest = 34;
        badge.Stroke = Colors.Transparent;
        badge.Content = new Label
        {
            Text = "⚡", TextColor = Theme.Amber, FontSize = 14,
            HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center
        };
        header.Add(badge, 0, 0);

        // Meta line: account info on the left, threshold summary on the right.
        var metaRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            }
        };
        metaRow.Add(new Label
        {
            Text = $"{m.Utility.ToString().ToUpperInvariant()} · A/C {L.N(m.Account)}",
            TextColor = Theme.Faint, FontSize = 10, FontFamily = "Menlo",
            LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1,
            VerticalTextAlignment = TextAlignment.Center
        }, 0, 0);
        summary.LineBreakMode = LineBreakMode.TailTruncation;
        summary.MaxLines = 1;
        summary.FontSize = 11;
        metaRow.Add(summary, 1, 0);

        header.Add(new VerticalStackLayout
        {
            Spacing = 2, VerticalOptions = LayoutOptions.Center,
            Children =
            {
                // Force single-line truncation. Without this, "Md. Golam Warich" wraps
                // to two lines and — worse — iOS treats the "/" in "A/C" as a soft
                // break, so the account number splits across lines as "A/" + "C 14024780".
                new Label
                {
                    Text = name, TextColor = Theme.Text, FontSize = 14,
                    LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1
                },
                metaRow
            }
        }, 1, 0);
        header.Add(sw, 2, 0);

        // ---- expandable editor (hidden by default) ----
        var editor = new Grid
        {
            IsVisible = false,
            Margin = new Thickness(14, 0, 14, 12),
            Padding = new Thickness(12, 10),
            BackgroundColor = Color.FromArgb("#17251E"),
            ColumnSpacing = 10, RowSpacing = 4,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(1)),
                new ColumnDefinition(GridLength.Star)
            },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) }
        };
        editor.Add(new Label { Text = L.S("alertBelow"), TextColor = Theme.Muted, FontSize = 10 }, 0, 0);
        var takaVal = new Label
        {
            Text = $"৳ {L.N(m.EffectiveLowTaka.ToString())}",
            TextColor = Theme.Text, FontSize = 15, FontAttributes = FontAttributes.Bold
        };
        editor.Add(takaVal, 0, 1);
        var divider = new BoxView { Color = Theme.Border, WidthRequest = 1 };
        editor.Add(divider, 1, 0);
        Grid.SetRowSpan(divider, 2);
        editor.Add(new Label { Text = L.S("orBelow"), TextColor = Theme.Muted, FontSize = 10 }, 2, 0);
        var daysVal = new Label
        {
            Text = $"{L.N(m.EffectiveLowDays.ToString())} {L.S("daysLeft")}",
            TextColor = Theme.Text, FontSize = 15, FontAttributes = FontAttributes.Bold
        };
        editor.Add(daysVal, 2, 1);

        // Persist a threshold edit, refresh the summary, and re-arm the queue with the
        // new figure. Re-arming here is critical: the OS is still holding yesterday's
        // reminder timed against the OLD threshold, and unless we cancel + re-schedule,
        // the user won't feel their change take effect until the next background sync.
        async Task<bool> EditTaka()
        {
            var input = await Prompt(root, name, L.S("alertBelowTakaPrompt"),
                m.EffectiveLowTaka.ToString(), keyboard: Keyboard.Numeric);
            if (input == null || !int.TryParse(input, out var v) || v <= 0) return false;
            var all = Store.Meters();
            var live = all.FirstOrDefault(x => x.Id == m.Id);
            if (live == null) return false;
            live.LowTaka = v;
            Store.SaveMeters(all);
            m.LowTaka = v;
            takaVal.Text = $"৳ {L.N(v.ToString())}";
            RefreshSummary();
            ReArm(m);
            return true;
        }
        async Task<bool> EditDays()
        {
            var input = await Prompt(root, name, L.S("alertBelowDaysPrompt"),
                m.EffectiveLowDays.ToString(), keyboard: Keyboard.Numeric);
            if (input == null || !int.TryParse(input, out var v) || v <= 0 || v > 30) return false;
            var all = Store.Meters();
            var live = all.FirstOrDefault(x => x.Id == m.Id);
            if (live == null) return false;
            live.LowDays = v;
            Store.SaveMeters(all);
            m.LowDays = v;
            daysVal.Text = $"{L.N(v.ToString())} {L.S("daysLeft")}";
            RefreshSummary();
            ReArm(m);
            return true;
        }

        var takaTap = new TapGestureRecognizer();
        takaTap.Tapped += async (_, _) => { if (sw.IsEnabled) await EditTaka(); };
        takaVal.GestureRecognizers.Add(takaTap);
        var daysTap = new TapGestureRecognizer();
        daysTap.Tapped += async (_, _) => { if (sw.IsEnabled) await EditDays(); };
        daysVal.GestureRecognizers.Add(daysTap);

        // ---- expand/collapse on header tap ----
        // The switch has its own hit area (MAUI's Switch swallows taps), so this tap
        // fires only when the user hits the badge / name / summary — which is the
        // interaction the affordance ("row expands") is meant to convey.
        var headerTap = new TapGestureRecognizer();
        headerTap.Tapped += (_, _) =>
        {
            if (!sw.IsEnabled) return;   // master off — nothing to expand into
            editor.IsVisible = !editor.IsVisible;
        };
        header.GestureRecognizers.Add(headerTap);

        // ---- switch handler ----
        // Idempotent, and applies the OS-side cancellation immediately for OFF so a
        // queued 9am banner doesn't ring tomorrow anyway.
        var id = m.Id;
        sw.Toggled += (_, e) =>
        {
            var all = Store.Meters();
            var live = all.FirstOrDefault(x => x.Id == id);
            if (live == null) return;
            if (live.NotificationsEnabled == e.Value) return;
            live.NotificationsEnabled = e.Value;
            Store.SaveMeters(all);
            m.NotificationsEnabled = e.Value;

            header.Opacity = e.Value ? 1 : 0.55;
            if (!e.Value)
            {
                Notifier.CancelFor(id);
                editor.IsVisible = false;   // collapse — editing is pointless when muted
            }
        };
        header.Opacity = m.NotificationsEnabled ? 1 : 0.55;

        var container = new VerticalStackLayout { Spacing = 0 };
        container.Add(header);
        container.Add(editor);

        return new PerAcctRow
        {
            Container = container,
            Header = header,
            Switch = sw,
            Editor = editor
        };
    }

    /// <summary>Cancel + re-arm this meter's queue against its current forecast + thresholds.</summary>
    static void ReArm(Meter m)
    {
        Notifier.CancelFor(m.Id);
        var snap = Store.Snapshot(m.Id);
        if (snap == null) return;
        // Recompute from stored readings — same call the HomePage refresh uses, so the
        // reminder we arm here matches the number the user is looking at. Not worth
        // blocking the UI on a network hit; the next background sync will refresh anyway.
        var f = Forecast.Build(Store.Readings(m.Id), snap);
        Notifier.ScheduleLowBalance(m, f, snap.CustomerName);
    }

    /// <summary>Two-line label + right-aligned switch, sized to sit inside a card.</summary>
    static Grid MakeSwitchRow(string title, string subtitle, Switch sw)
    {
        var g = new Grid
        {
            Padding = new Thickness(4, 6), ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        g.Add(new VerticalStackLayout
        {
            Spacing = 2, VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = title, TextColor = Theme.Text, FontSize = 15 },
                new Label { Text = subtitle, TextColor = Theme.Muted, FontSize = 11, LineBreakMode = LineBreakMode.WordWrap }
            }
        }, 0, 0);
        g.Add(sw, 1, 0);
        return g;
    }

    public static Task<string?> Prompt(Grid root, string title, string? message = null,
        string? initial = null, string? placeholder = null, Keyboard? keyboard = null)
    {
        var tcs = new TaskCompletionSource<string?>();
        var stack = Body(title, message);

        var entry = UI.Field(placeholder ?? "", initial, keyboard);
        var box = UI.Card(Theme.Card2, 12);
        box.Padding = new Thickness(14, 4);
        box.Content = entry;
        stack.Add(box);

        var save = UI.Primary(L.S("save"));
        var cancel = UI.Ghost(L.S("cancel"));

        Grid? overlay = null;
        async void Commit() { await Close(root, overlay!); tcs.TrySetResult(entry.Text); }
        save.Clicked += (_, _) => Commit();
        entry.Completed += (_, _) => Commit();
        cancel.Clicked += async (_, _) => { await Close(root, overlay!); tcs.TrySetResult(null); };

        stack.Add(save);
        stack.Add(cancel);

        overlay = Present(root, stack, () => tcs.TrySetResult(null));

        // Focus only once the overlay has laid out, or the keyboard misses it.
        _ = Task.Delay(200).ContinueWith(_ =>
            MainThread.BeginInvokeOnMainThread(() => entry.Focus()));

        return tcs.Task;
    }

    /// <summary>Returns the chosen index, or null if dismissed.</summary>
    public static Task<int?> Choose(Grid root, string title, IList<string> options, int selected = -1)
    {
        var tcs = new TaskCompletionSource<int?>();
        var stack = Body(title, null);

        Grid? overlay = null;
        for (int i = 0; i < options.Count; i++)
        {
            bool on = i == selected;
            var b = new Button
            {
                Text = options[i],
                HeightRequest = 50,
                CornerRadius = 12,
                FontSize = 15,
                BackgroundColor = on ? Theme.Green : Theme.Card2,
                TextColor = on ? Theme.Ink : Theme.Text,
                BorderColor = Theme.Border,
                BorderWidth = on ? 0 : 1
            };
            var idx = i;
            b.Clicked += async (_, _) => { await Close(root, overlay!); tcs.TrySetResult(idx); };
            stack.Add(b);
        }

        var cancel = UI.Ghost(L.S("cancel"));
        cancel.Clicked += async (_, _) => { await Close(root, overlay!); tcs.TrySetResult(null); };
        stack.Add(cancel);

        overlay = Present(root, stack, () => tcs.TrySetResult(null));
        return tcs.Task;
    }

    /// <summary>Utility + account + optional label, in one card instead of three popups.</summary>
    public static Task<Meter?> AddAccount(Grid root)
    {
        var tcs = new TaskCompletionSource<Meter?>();
        var stack = Body(L.S("addAccount"), L.S("enterAccountSub"));

        var utility = Utility.Desco;
        var toggle = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) } };
        Button desco = new() { Text = "DESCO", HeightRequest = 48, CornerRadius = 12, FontAttributes = FontAttributes.Bold, BorderColor = Theme.Border, BorderWidth = 1 };
        Button nesco = new() { Text = "NESCO", HeightRequest = 48, CornerRadius = 12, FontAttributes = FontAttributes.Bold, BorderColor = Theme.Border, BorderWidth = 1 };
        void Paint()
        {
            desco.BackgroundColor = utility == Utility.Desco ? Theme.Green : Theme.Card2;
            desco.TextColor = utility == Utility.Desco ? Theme.Ink : Theme.Muted;
            nesco.BackgroundColor = utility == Utility.Nesco ? Theme.Green : Theme.Card2;
            nesco.TextColor = utility == Utility.Nesco ? Theme.Ink : Theme.Muted;
        }
        desco.Clicked += (_, _) => { utility = Utility.Desco; Paint(); };
        nesco.Clicked += (_, _) => { utility = Utility.Nesco; Paint(); };
        Paint();
        toggle.Add(desco, 0, 0);
        toggle.Add(nesco, 1, 0);
        stack.Add(toggle);

        var account = UI.Field(L.S("egAccount"), null, Keyboard.Numeric);
        var accountBox = UI.Card(Theme.Card2, 12);
        accountBox.Padding = new Thickness(14, 4);
        accountBox.Content = account;
        stack.Add(accountBox);

        var label = UI.Field(L.S("label"));
        var labelBox = UI.Card(Theme.Card2, 12);
        labelBox.Padding = new Thickness(14, 4);
        labelBox.Content = label;
        stack.Add(labelBox);

        var error = new Label { TextColor = Theme.Red, FontSize = 12, IsVisible = false };
        stack.Add(error);

        var save = UI.Primary(L.S("save"));
        var cancel = UI.Ghost(L.S("cancel"));

        Grid? overlay = null;
        save.Clicked += async (_, _) =>
        {
            var acct = (account.Text ?? "").Trim();
            if (acct.Length < 6 || !acct.All(char.IsDigit))
            {
                error.Text = "⚠︎ " + L.S("invalidAccount");
                error.IsVisible = true;
                accountBox.Stroke = Theme.Red;
                return;
            }
            await Close(root, overlay!);
            tcs.TrySetResult(new Meter { Account = acct, Label = (label.Text ?? "").Trim(), Utility = utility });
        };
        cancel.Clicked += async (_, _) => { await Close(root, overlay!); tcs.TrySetResult(null); };

        stack.Add(save);
        stack.Add(cancel);

        overlay = Present(root, stack, () => tcs.TrySetResult(null));
        return tcs.Task;
    }

    // ---------- plumbing ----------

    static VerticalStackLayout Body(string title, string? message)
    {
        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Add(new Label { Text = title, TextColor = Theme.Text, FontSize = 18, FontAttributes = FontAttributes.Bold });
        if (!string.IsNullOrWhiteSpace(message))
            stack.Add(new Label { Text = message, TextColor = Theme.Muted, FontSize = 13 });
        return stack;
    }

    static Grid Present(Grid root, View content, Action onDismiss, View? footer = null)
    {
        // The scroller lives INSIDE the card, which is what iOS wants: a Border sizes
        // itself to its content's desired height, iOS's ScrollView shrink-wraps to the
        // stack inside it, so the card hugs the content and Center actually centres it.
        var scroller = new ScrollView
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Default
        };

        var display = DeviceDisplay.MainDisplayInfo;
        var cap = display.Height / display.Density * (footer == null ? 0.68 : 0.58);

        // Android's ScrollView asks for every pixel of height it is offered, so a Border
        // wrapped round one grows to the whole screen and Center has nothing to centre.
        // iOS shrink-wraps and is fine. So we do the measuring ourselves and pin the
        // scroller to the answer.
        //
        // Note we MEASURE the stack rather than read its Height: MAUI's Android scroll
        // view stretches its content to fill the viewport, so content.Height comes back
        // as the full screen and every dialog ends up exactly `cap` tall — which is the
        // bug this replaces. Measuring against an infinite height constraint asks the
        // stack how tall it actually wants to be, which is the number we need.
        void Fit()
        {
            var w = scroller.Width;
            if (w <= 0) return;

            var natural = ((IView)content).Measure(w, double.PositiveInfinity).Height;
            var want = Math.Min(natural, cap);
            if (want <= 0) return;

            // Guarded: assigning HeightRequest relaunches layout, and an unguarded
            // assignment here would chase its own tail.
            if (Math.Abs(scroller.HeightRequest - want) > 0.5) scroller.HeightRequest = want;
        }

        scroller.SizeChanged += (_, _) => Fit();
        content.SizeChanged += (_, _) => Fit();

        // A footer is pinned below the scroll region rather than dropped into it, so a
        // long recharge can't push its own Close button out of reach.
        View inner = scroller;
        if (footer != null)
        {
            var split = new Grid
            {
                RowSpacing = 12,
                RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) }
            };
            split.Add(scroller, 0, 0);
            split.Add(footer, 0, 1);
            inner = split;
        }

        var card = new Border
        {
            BackgroundColor = Theme.Card,
            Stroke = Theme.Border,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 20 },
            Padding = 20,
            Margin = new Thickness(24, 0),
            VerticalOptions = LayoutOptions.Center,
            Content = inner,
            Opacity = 0,
            Scale = 0.96
        };

        var overlay = new Grid { BackgroundColor = Scrim, Opacity = 0 };
        overlay.Add(card);

        // Tapping the scrim dismisses; taps on the card must not bubble to it.
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => { await Close(root, overlay); onDismiss(); };
        overlay.GestureRecognizers.Add(tap);
        card.GestureRecognizers.Add(new TapGestureRecognizer());   // swallow

        root.Add(overlay);

        _ = Task.WhenAll(
            overlay.FadeTo(1, 140, Easing.CubicOut),
            card.FadeTo(1, 160, Easing.CubicOut),
            card.ScaleTo(1, 160, Easing.CubicOut));

        return overlay;
    }

    static async Task Close(Grid root, Grid overlay)
    {
        if (!root.Children.Contains(overlay)) return;

        // A second tap during the 110ms fade would run a second Close (and, for Choose,
        // pick a second account) on an overlay that's already on its way out.
        overlay.InputTransparent = true;

        await overlay.FadeTo(0, 110, Easing.CubicIn);
        root.Remove(overlay);

        // Hand the UI thread one clean frame before the caller resumes. Callers await
        // Choose()/Prompt() and then immediately Refresh() — a full page rebuild, on
        // this thread. Without the yield, Remove() and that rebuild land in the same
        // turn, so iOS never paints the removal: the part-faded card just hangs there
        // over the new content until the rebuild is done.
        await Task.Yield();
    }
}
