using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;
using ScrollView = Microsoft.Maui.Controls.ScrollView;

namespace PrepaidTracker;

/// <summary>
/// A ScrollView that reports overscroll at the top edge.
///
/// MAUI's ScrollView gives you nothing when the finger drags past the top — iOS
/// bounces and reports a negative ScrollY, Android just clamps at 0 and paints an
/// edge glow. So on Android we read the raw touch stream instead (see the handler
/// mapper in MauiProgram) and hand the distance to whoever is listening. On iOS
/// this is an ordinary ScrollView; the callbacks are never wired.
/// </summary>
public sealed class PullScrollView : ScrollView
{
    /// <summary>Live pull distance, in device-independent units, while the finger is down.</summary>
    public Action<double>? Pulled;

    /// <summary>Finger lifted (or the gesture was cancelled).</summary>
    public Action? Released;

    /// <summary>False while a refresh is already running — the pull is ignored.</summary>
    public Func<bool>? CanPull;

    /// <summary>Guards against the mapper double-subscribing if it runs twice.</summary>
    public bool Hooked;
}

/// <summary>
/// The spinner UIRefreshControl draws: twelve tapering spokes around a hub, lit one
/// by one as the pull deepens, then stepped round once a second while the sync runs.
/// Drawn rather than animated, because MAUI has no cross-platform equivalent and
/// Android's ActivityIndicator is a Material arc — a different thing entirely.
/// </summary>
public sealed class Spinner : GraphicsView
{
    const int Spokes = 12;

    readonly Dial _art = new();
    IDispatcherTimer? _timer;

    public Spinner()
    {
        Drawable = _art;
        WidthRequest = 22;
        HeightRequest = 22;
        InputTransparent = true;
    }

    /// <summary>0 → 1. Below 1, only that fraction of the spokes are drawn.</summary>
    public double Progress
    {
        get => _art.Progress;
        set { _art.Progress = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    public void Start()
    {
        if (_timer != null) return;

        _art.Progress = 1;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / Spokes);   // one turn per second
        _timer.Tick += (_, _) => { _art.Step = (_art.Step + 1) % Spokes; Invalidate(); };
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _art.Step = 0;
        Invalidate();
    }

    sealed class Dial : IDrawable
    {
        public double Progress;
        public int Step;

        public void Draw(ICanvas c, RectF r)
        {
            var cx = r.Center.X;
            var cy = r.Center.Y;
            var outer = Math.Min(r.Width, r.Height) / 2f - 1f;
            var inner = outer * 0.44f;

            c.StrokeLineCap = LineCap.Round;
            c.StrokeSize = outer * 0.24f;

            // Ceiling, so the first spoke appears the instant the pull starts.
            var lit = (int)Math.Ceiling(Progress * Spokes);

            for (var i = 0; i < lit && i < Spokes; i++)
            {
                // How far behind the leading spoke this one is. At rest Step is 0, so
                // the ramp is static and the thing reads as a fixed dial; once Start()
                // is stepping, the bright end chases round the circle.
                var age = ((i - Step) % Spokes + Spokes) % Spokes;
                var alpha = 0.18f + 0.82f * (age / (float)(Spokes - 1));

                var a = (float)(i * 2 * Math.PI / Spokes - Math.PI / 2);
                var dx = MathF.Cos(a);
                var dy = MathF.Sin(a);

                c.StrokeColor = Theme.Green.WithAlpha(alpha);
                c.DrawLine(cx + inner * dx, cy + inner * dy,
                           cx + outer * dx, cy + outer * dy);
            }
        }
    }
}

/// <summary>
/// Pull-to-refresh that behaves the same on both platforms: the content itself slides
/// down under the finger, the spinner is revealed in the gap it leaves, and the content
/// rests at Hold while the sync runs.
///
/// Android's RefreshView is a Material SwipeRefreshLayout — a puck that drops in over
/// stationary content. There is no property that turns it into a UIRefreshControl, so
/// on Android we don't use it at all. Same public surface as RefreshView (Content,
/// IsRefreshing, Refreshing, RefreshColor) so HomePage doesn't care which it got.
/// </summary>
public sealed class PullRefresh : Grid
{
    /// <summary>Translated units, i.e. after damping — not raw finger travel.</summary>
    const double Trigger = 52;   // past this, releasing commits the refresh
    const double Hold = 56;      // where the content parks while syncing
    const double Max = 92;       // hard stop, so it can't be dragged to the floor
    const double Damping = 0.55; // the pull gets heavier than the finger

    readonly Spinner _spinner = new() { Opacity = 0, VerticalOptions = LayoutOptions.Start, HorizontalOptions = LayoutOptions.Center };
    PullScrollView? _scroll;
    bool _busy;
    double _pull;

    public event EventHandler? Refreshing;

    /// <summary>Accepted and ignored. RefreshView has it; the spinner reads Theme.Green directly.</summary>
    public Color RefreshColor { get; set; } = Theme.Green;

    public bool IsRefreshing
    {
        get => _busy;
        set { if (!value && _busy) _ = End(); }
    }

    public View Content
    {
        set
        {
            _scroll = (PullScrollView)value;

            // Opaque, or the spinner sitting behind it would show through at rest.
            _scroll.BackgroundColor = Theme.Bg;
            _scroll.CanPull = () => !_busy;
            _scroll.Pulled = OnPull;
            _scroll.Released = OnRelease;

            Children.Clear();
            Add(_spinner);   // behind
            Add(_scroll);    // in front, and it's what moves
        }
    }

    void OnPull(double fingerDy)
    {
        if (_scroll == null) return;

        _pull = Math.Min(Max, Math.Max(0, fingerDy * Damping));

        _scroll.TranslationY = _pull;
        _spinner.TranslationY = Math.Max(0, _pull / 2 - 11);   // centred in the gap
        _spinner.Opacity = Math.Min(1, _pull / (Trigger * 0.6));
        _spinner.Progress = Math.Min(1, _pull / Trigger);
    }

    void OnRelease()
    {
        if (_scroll == null) return;
        if (_pull >= Trigger) _ = Begin();
        else _ = Spring();
    }

    async Task Begin()
    {
        _busy = true;
        _spinner.Opacity = 1;
        _spinner.Start();

        await _scroll!.TranslateTo(0, Hold, 160, Easing.CubicOut);
        _spinner.TranslationY = Hold / 2 - 11;

        // Fired only once the content has settled, so the page can rebuild under a
        // spinner that's already turning rather than one that pops in mid-animation.
        Refreshing?.Invoke(this, EventArgs.Empty);
    }

    async Task Spring()
    {
        _pull = 0;
        await Task.WhenAll(
            _scroll!.TranslateTo(0, 0, 180, Easing.CubicOut),
            _spinner.FadeTo(0, 140, Easing.CubicIn));
        _spinner.Progress = 0;
    }

    async Task End()
    {
        _pull = 0;
        await Task.WhenAll(
            _scroll!.TranslateTo(0, 0, 220, Easing.CubicIn),
            _spinner.FadeTo(0, 180, Easing.CubicIn));

        _spinner.Stop();
        _spinner.Progress = 0;
        _busy = false;
    }
}
