using System;
using System.Drawing;
using System.Windows.Forms;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AdbView.Host;

internal sealed class TrayIcon : IDisposable {
    private readonly NotifyIcon _notifyIcon;
    private readonly uint _taskbarCreatedMessageId;
    private readonly MessageSink _messageSink;

    public event EventHandler? OnToggleRequested;
    public event EventHandler? OnExitRequested;

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
                    return (new Rectangle(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top), edge);
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "Failed to get real tray icon rect. Using fallback.");
        }
#pragma warning restore CA1031

        // Fallback: Project cursor onto the detected taskbar edge
        var cursor = Cursor.Position;
        return (new Rectangle(cursor.X, cursor.Y, 1, 1), edge);
    }

    public void Dispose() {
        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.Dispose();
        _messageSink.DestroyHandle();
    }
}
