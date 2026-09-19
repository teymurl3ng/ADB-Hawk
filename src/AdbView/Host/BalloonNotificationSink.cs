namespace AdbView.Host;

using System;
using System.Windows.Forms;
using AdbView.Notifications;
using Serilog;

/// <summary>
/// Delivers <see cref="NotificationIntent"/> via <see cref="ITrayBalloonHost"/>
/// (<c>NotifyIcon.ShowBalloonTip</c>). No policy, snapshot, or ADB knowledge.
/// </summary>
internal sealed class BalloonNotificationSink : INotificationSink, IDisposable {
    private readonly ITrayBalloonHost _host;
    private readonly Action<Action> _marshalToUi;
    private readonly Action? _showFlyout;
    private readonly object _gate = new();
    private bool _disposed;

    /// <param name="host">Tray balloon delivery surface.</param>
    /// <param name="marshalToUi">Runs work on the WinForms UI thread (identity when already on it).</param>
    /// <param name="showFlyout">
    /// Balloon click handler: must <em>show</em> the flyout, never toggle-to-hide.
    /// Null means click is ignored (tests / smoke without a flyout).
    /// </param>
    public BalloonNotificationSink(
        ITrayBalloonHost host,
        Action<Action> marshalToUi,
        Action? showFlyout = null) {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _marshalToUi = marshalToUi ?? throw new ArgumentNullException(nameof(marshalToUi));
        _showFlyout = showFlyout;
        _host.BalloonClicked += OnBalloonClicked;
    }

    public void Emit(NotificationIntent intent) {
        ArgumentNullException.ThrowIfNull(intent);

        lock (_gate) {
            if (_disposed) {
                Log.Debug("BalloonNotificationSink.Emit ignored: disposed");
                return;
            }
        }

        string title = BalloonText.TruncateTitle(intent.Title ?? string.Empty);
        string body = BalloonText.TruncateBody(intent.Body ?? string.Empty);
        NotificationSeverity severity = intent.Severity;

        try {
            _marshalToUi(() => DeliverOnUi(title, body, severity));
        }
#pragma warning disable CA1031 // Delivery boundary: never escape to callers / StateEngine
        catch (Exception ex) {
            Log.Warning(ex, "host.tray balloon marshal failed");
        }
#pragma warning restore CA1031
    }

    private void DeliverOnUi(string title, string body, NotificationSeverity severity) {
        lock (_gate) {
            if (_disposed) {
                return;
            }
        }

        try {
            _host.ShowBalloon(title, body, severity);
            Log.Information("host.tray balloon delivered ({Severity})", severity);
        }
#pragma warning disable CA1031
        catch (ObjectDisposedException ex) {
            Log.Debug(ex, "host.tray balloon skipped: tray disposed");
        }
        catch (InvalidOperationException ex) {
            Log.Warning(ex, "host.tray balloon failed");
        }
        catch (Exception ex) {
            Log.Warning(ex, "host.tray balloon failed");
        }
#pragma warning restore CA1031
    }

    private void OnBalloonClicked(object? sender, EventArgs e) {
        lock (_gate) {
            if (_disposed || _showFlyout is null) {
                return;
            }
        }

        try {
            _marshalToUi(() => {
                lock (_gate) {
                    if (_disposed) {
                        return;
                    }
                }

                _showFlyout();
            });
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "host.tray balloon click show-flyout failed");
        }
#pragma warning restore CA1031
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }

            _disposed = true;
        }

        _host.BalloonClicked -= OnBalloonClicked;
    }
}
