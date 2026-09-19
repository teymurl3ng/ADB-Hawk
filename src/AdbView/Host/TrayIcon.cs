using System;
using System.Drawing;
using System.Windows.Forms;
using AdbView.Notifications;
using AdbView.State;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AdbView.Host;

internal sealed class TrayIcon : ITrayBalloonHost, IDisposable {
    private readonly NotifyIcon _notifyIcon;
    private readonly uint _taskbarCreatedMessageId;
    private readonly MessageSink _messageSink;
    private TrayStatusKind _currentStatus = TrayStatusKind.Idle;
    private Icon? _ownedIcon;
    private bool _disposed;

    public event EventHandler? OnToggleRequested;
    public event EventHandler? OnExitRequested;
    public event EventHandler? BalloonClicked;

    private sealed class MessageSink : NativeWindow {
        private readonly TrayIcon _parent;

        public MessageSink(TrayIcon parent) {
            _parent = parent;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m) {
            if (m.Msg == _parent._taskbarCreatedMessageId) {
                _parent.HandleTaskbarCreated();
            }
            base.WndProc(ref m);
        }
    }

    public TrayIcon() {
        _taskbarCreatedMessageId = PInvoke.RegisterWindowMessage("TaskbarCreated");
        _messageSink = new MessageSink(this);

        var assembly = typeof(TrayIcon).Assembly;
        var iconStream = assembly.GetManifestResourceStream("AdbView.Resources.aperture-link-idle.ico");

#pragma warning disable CA1303 // Do not pass literals as localized parameters
        _notifyIcon = new NotifyIcon {
            Text = "ADB View",
            Icon = iconStream != null ? new Icon(iconStream) : SystemIcons.Application,
            Visible = true
        };
#pragma warning restore CA1303

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Exit", null, (s, e) => OnExitRequested?.Invoke(this, EventArgs.Empty));
        _notifyIcon.ContextMenuStrip = contextMenu;

        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
    }

    /// <summary>Test seam: context menu captions (Exit only).</summary>
    internal string[] ContextMenuItemTexts {
        get {
            if (_notifyIcon.ContextMenuStrip is null) {
                return Array.Empty<string>();
            }

            var items = _notifyIcon.ContextMenuStrip.Items;
            var texts = new string[items.Count];
            for (int i = 0; i < items.Count; i++) {
                texts[i] = items[i].Text ?? string.Empty;
            }

            return texts;
        }
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e) {
        BalloonClicked?.Invoke(this, EventArgs.Empty);
    }

    public void ShowBalloon(string title, string body, NotificationSeverity severity) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        ToolTipIcon tipIcon = severity switch {
            NotificationSeverity.Info => ToolTipIcon.Info,
            NotificationSeverity.Warning => ToolTipIcon.Warning,
            NotificationSeverity.Error => ToolTipIcon.Error,
            _ => ToolTipIcon.Info,
        };

        // Timeout is ignored on Vista+ (system accessibility timeout); value is required by API.
        _notifyIcon.ShowBalloonTip(10_000, title, body, tipIcon);
    }

    private void OnMouseClick(object? sender, MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            OnToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void HandleTaskbarCreated() {
        Log.Information("Explorer restart detected. Re-adding tray icon.");
        _notifyIcon.Visible = false;
        _notifyIcon.Visible = true;
    }

    /// <summary>
    /// Applies Pass-F tray glyph + tooltip from aggregate snapshot health (PHASES.md Phase 5).
    /// </summary>
    public void Apply(TrayStatusKind status, string tooltip) {
        ArgumentNullException.ThrowIfNull(tooltip);

        string text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
        if (!string.Equals(_notifyIcon.Text, text, StringComparison.Ordinal)) {
            _notifyIcon.Text = text;
        }

        if (_currentStatus == status && _ownedIcon is not null) {
            return;
        }

        string resource = status switch {
            TrayStatusKind.Connected => "AdbView.Resources.aperture-link-connected.ico",
            TrayStatusKind.Attention => "AdbView.Resources.aperture-link-attention.ico",
            TrayStatusKind.Failure => "AdbView.Resources.aperture-link-error.ico",
            TrayStatusKind.Special => "AdbView.Resources.aperture-link-special.ico",
            TrayStatusKind.PairingAvailable => "AdbView.Resources.aperture-link-pairing.ico",
            _ => "AdbView.Resources.aperture-link-idle.ico",
        };

        var assembly = typeof(TrayIcon).Assembly;
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null) {
            Log.Warning("Tray icon resource missing: {Resource}", resource);
            return;
        }

        var previous = _ownedIcon;
        _ownedIcon = new Icon(stream);
        _notifyIcon.Icon = _ownedIcon;
        _currentStatus = status;
        previous?.Dispose();
    }

    public (Rectangle Rect, FlyoutPlacement.TaskbarEdge Edge) GetPlacementInfo() {
        var edge = FlyoutPlacement.TaskbarEdge.Bottom;
        var hwnd = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (!hwnd.IsNull) {
            var abd = new Windows.Win32.UI.Shell.APPBARDATA {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Windows.Win32.UI.Shell.APPBARDATA>(),
                hWnd = hwnd
            };
            if (PInvoke.SHAppBarMessage((uint)PInvoke.ABM_GETTASKBARPOS, ref abd) != 0) {
                edge = (FlyoutPlacement.TaskbarEdge)abd.uEdge;
            }
        }

        try {
            var idField = typeof(NotifyIcon).GetField("id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var windowField = typeof(NotifyIcon).GetField("window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (idField != null && windowField != null) {
                var id = (int)idField.GetValue(_notifyIcon)!;
                var window = (NativeWindow)windowField.GetValue(_notifyIcon)!;

                var identifier = new Windows.Win32.UI.Shell.NOTIFYICONIDENTIFIER {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Windows.Win32.UI.Shell.NOTIFYICONIDENTIFIER>(),
                    hWnd = new Windows.Win32.Foundation.HWND(window.Handle),
                    uID = (uint)id
                };

                if (PInvoke.Shell_NotifyIconGetRect(in identifier, out var rect).Value == 0) // S_OK
                {
                    var iconRect = new Rectangle(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
                    if (iconRect.Width > 0 && iconRect.Height > 0) {
                        return (iconRect, edge);
                    }
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "Failed to get real tray icon rect. Using fallback.");
        }
#pragma warning restore CA1031

        return (Rectangle.Empty, edge);
    }

    public static Screen GetTaskbarScreen() {
        var hwnd = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (!hwnd.IsNull) {
            return Screen.FromHandle((IntPtr)hwnd);
        }
        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.BalloonTipClicked -= OnBalloonTipClicked;
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
        _ownedIcon = null;
        _messageSink.DestroyHandle();
    }
}
