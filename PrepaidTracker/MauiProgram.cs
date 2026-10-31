using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace PrepaidTracker;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        _ = Access.RefreshConfigAsync();
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if ANDROID
        // Teach Android's ScrollView to report a pull past the top edge, which MAUI
        // otherwise swallows: the native NestedScrollView clamps ScrollY at 0 and
        // paints an edge glow instead of telling anyone. We read the raw touch stream,
        // never consume it (Handled stays false), so ordinary scrolling is untouched.
        Microsoft.Maui.Handlers.ScrollViewHandler.Mapper.AppendToMapping(
            "PullToRefresh", (handler, view) =>
            {
                if (view is not PullScrollView pull || pull.Hooked) return;

                var native = handler.PlatformView;
                pull.Hooked = true;

                // Kill the Material edge glow. We're drawing the feedback ourselves,
                // and the two together look like a bug.
                native.OverScrollMode = Android.Views.OverScrollMode.Never;

                var density = native.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
                var startY = 0f;
                var tracking = false;

                native.Touch += (_, e) =>
                {
                    e.Handled = false;                      // never steal the scroll
                    if (e.Event is not { } ev) return;

                    switch (ev.ActionMasked)
                    {
                        case Android.Views.MotionEventActions.Down:
                            startY = ev.RawY;
                            tracking = false;
                            break;

                        case Android.Views.MotionEventActions.Move:
                            if (pull.CanPull?.Invoke() == false) break;

                            var dy = (ev.RawY - startY) / density;

                            // Only start tracking from a genuine top-edge overscroll —
                            // not from a fling that happens to end up at the top.
                            if (!tracking && native.ScrollY == 0 && dy > 0) tracking = true;
                            if (!tracking) break;

                            // Scrolled back into the list, or pulled upward: hand back.
                            if (native.ScrollY > 0 || dy <= 0)
                            {
                                tracking = false;
                                pull.Pulled?.Invoke(0);
                                break;
                            }

                            pull.Pulled?.Invoke(dy);
                            break;

                        case Android.Views.MotionEventActions.Up:
                        case Android.Views.MotionEventActions.Cancel:
                            if (!tracking) break;
                            tracking = false;
                            pull.Released?.Invoke();
                            break;
                    }
                };
            });
#endif

        return builder.Build();
    }
}
