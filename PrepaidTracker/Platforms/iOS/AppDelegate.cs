using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using UIKit;
using UserNotifications;

namespace PrepaidTracker;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication app, NSDictionary options)
    {
        // Must happen before launching finishes, or iOS refuses the identifier.
        BackgroundSync.Register();

        // Present notifications that fire while the app is in the foreground. Apple
        // requires the delegate to be set before launch finishes, and without it every
        // in-foreground alert is silently dropped — the usual "why no notifications".
        UNUserNotificationCenter.Current.Delegate = new ForegroundNotificationDelegate();

        var ok = base.FinishedLaunching(app, options);
        BackgroundSync.Schedule();
        return ok;
    }

    // Queue the next wake as we leave, so there's always one pending.
    public override void DidEnterBackground(UIApplication application) => BackgroundSync.Schedule();
}
