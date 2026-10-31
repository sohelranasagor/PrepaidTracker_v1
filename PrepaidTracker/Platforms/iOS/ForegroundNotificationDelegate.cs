using System;
using UserNotifications;

namespace PrepaidTracker;

/// <summary>
/// Without this, iOS silently swallows any notification whose trigger fires while the app
/// is in the FOREGROUND — which, on a sideloaded build, is almost always. The low-balance
/// alert is created 2s after a sync, and the only syncs a SideStore install reliably runs
/// are the ones the user triggers by opening the app, so the alert fires while the app is
/// on screen and never appears. Returning presentation options here lets the banner, list
/// entry, and sound show even with the app open. Must be set on
/// UNUserNotificationCenter.Current.Delegate before launch finishes.
/// </summary>
public sealed class ForegroundNotificationDelegate : UNUserNotificationCenterDelegate
{
    public override void WillPresentNotification(
        UNUserNotificationCenter center,
        UNNotification notification,
        Action<UNNotificationPresentationOptions> completionHandler) =>
        completionHandler(
            UNNotificationPresentationOptions.Banner |
            UNNotificationPresentationOptions.List |
            UNNotificationPresentationOptions.Sound);
}
