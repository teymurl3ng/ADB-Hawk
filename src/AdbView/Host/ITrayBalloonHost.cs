namespace AdbView.Host;

using System;
using AdbView.Notifications;

/// <summary>
/// Narrow delivery seam for tray balloons. Implemented by <see cref="TrayIcon"/>;
/// tests use a fake. No ADB / snapshot / policy knowledge.
/// </summary>
internal interface ITrayBalloonHost {
    void ShowBalloon(string title, string body, NotificationSeverity severity);

    event EventHandler? BalloonClicked;
}
