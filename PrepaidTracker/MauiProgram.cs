using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace PrepaidTracker;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if IOS
        // MUST run before anything touches SecureStorage.
        //
        // DESCO's rebuilt portal requires a login, so the app now keeps a password and a
        // session cookie in the Keychain. MAUI's default accessibility class is
        // kSecAttrAccessibleWhenUnlocked, which means those items are UNREADABLE while
        // the phone is locked — and a BGAppRefreshTask fires precisely when the phone is
        // sitting locked in someone's pocket. The sync would find no credential, report
        // the session as gone, and post "sign in to DESCO again" at 3am to a user whose
        // session was perfectly fine.
        //
        // AfterFirstUnlock keeps the item readable from the first unlock after boot
        // onward, which is what a background task needs. ThisDeviceOnly keeps it out of
        // iCloud Keychain backups: a utility password has no business syncing to the
        // user's other devices, and this app's whole privacy story is that credentials
        // stay on the phone that entered them.
        //
        // Android is unaffected — Keystore has no equivalent lock-state restriction — so
        // without this the app would work on Android and silently misbehave on iOS only,
        // which is the hardest kind of bug to hear about from users.
        //
        // VERIFY the property name against your MAUI version: this API has moved between
        // releases. If it doesn't resolve, the equivalent is setting
        // SecAccessible.AfterFirstUnlockThisDeviceOnly on the platform SecureStorage
        // implementation. Existing Keychain items keep whatever class they were written
        // with, so users who signed in before this shipped will be asked once more.
        if (SecureStorage.Default is SecureStorageImplementation impl)
            impl.DefaultAccessible = Security.SecAccessible.AfterFirstUnlockThisDeviceOnly;
#endif

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