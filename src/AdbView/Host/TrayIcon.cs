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
    private readonly IUserPreferences _preferences;
    private readonly IStartupRegistration _startup;
    private readonly ContextMenuStrip _contextMenu;
    private TrayStatusKind _currentStatus = TrayStatusKind.Idle;
    private Icon? _ownedIcon;
    private Font? _openBoldFont;
    private bool _disposed;

    public event EventHandler? OnToggleRequested;
    public event EventHandler? OnOpenRequested;
    public event EventHandler? OnExitRequested;
    public event EventHandler? BalloonClicked;
    public event EventHandler? PreferencesChanged;

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

    public TrayIcon()
        : this(new UserPreferences(), new StartupRegistration()) {
    }

    internal TrayIcon(IUserPreferences preferences, IStartupRegistration startup) {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));

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

        var openItem = new ToolStripMenuItem("Open ADB View", null, OnOpenClicked);
        _openBoldFont = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold);
        openItem.Font = _openBoldFont;

        var startupItem = new ToolStripMenuItem("Start with Windows", null, OnStartupClicked);
        var notificationsItem = new ToolStripMenuItem("Notifications", null, OnNotificationsClicked);
        var statusBubbleItem = new ToolStripMenuItem("Status bubble", null, OnStatusBubbleClicked);
        var themeSystemItem = new ToolStripMenuItem("System", null, (_, _) => OnThemeClicked(ThemePreference.System));
        var themeLightItem = new ToolStripMenuItem("Light", null, (_, _) => OnThemeClicked(ThemePreference.Light));
        var themeDarkItem = new ToolStripMenuItem("Dark", null, (_, _) => OnThemeClicked(ThemePreference.Dark));
        var themeItem = new ToolStripMenuItem("Theme");
        themeItem.DropDownItems.Add(themeSystemItem);
        themeItem.DropDownItems.Add(themeLightItem);
        themeItem.DropDownItems.Add(themeDarkItem);
        var exitItem = new ToolStripMenuItem("Exit", null, OnExitClicked);

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(startupItem);
        _contextMenu.Items.Add(notificationsItem);
        _contextMenu.Items.Add(statusBubbleItem);
        _contextMenu.Items.Add(themeItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);
        _contextMenu.Opening += OnContextMenuOpening;
        _notifyIcon.ContextMenuStrip = _contextMenu;
#pragma warning restore CA1303

        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
        RefreshMenuChecks();
    }

    /// <summary>Test seam: top-level context menu captions (separators as empty).</summary>
    internal string[] ContextMenuItemTexts {
        get {
            var items = _contextMenu.Items;
            var texts = new string[items.Count];
            for (int i = 0; i < items.Count; i++) {
                texts[i] = items[i] is ToolStripSeparator ? string.Empty : (items[i].Text ?? string.Empty);
            }

            return texts;
        }
    }

    /// <summary>Test seam: Theme submenu captions.</summary>
    internal string[] ThemeMenuItemTexts {
        get {
            var theme = RequireThemeItem();
            var items = theme.DropDownItems;
            var texts = new string[items.Count];
            for (int i = 0; i < items.Count; i++) {
                texts[i] = items[i].Text ?? string.Empty;
            }

            return texts;
        }
    }

    internal bool StartupChecked => RequireItem("Start with Windows").Checked;
    internal bool NotificationsChecked => RequireItem("Notifications").Checked;
    internal bool StatusBubbleChecked => RequireItem("Status bubble").Checked;
    internal bool ThemeSystemChecked => RequireThemeChild("System").Checked;
    internal bool ThemeLightChecked => RequireThemeChild("Light").Checked;
    internal bool ThemeDarkChecked => RequireThemeChild("Dark").Checked;

    /// <summary>Test seam: force Opening refresh without showing UI.</summary>
    internal void RefreshMenuChecksForTests() => RefreshMenuChecks();

    private ToolStripMenuItem RequireItem(string text) {
        foreach (ToolStripItem item in _contextMenu.Items) {
            if (item is ToolStripMenuItem mi && string.Equals(mi.Text, text, StringComparison.Ordinal)) {
                return mi;
            }
        }

        throw new InvalidOperationException("Missing menu item: " + text);
    }

    private ToolStripMenuItem RequireThemeItem() => RequireItem("Theme");

    private ToolStripMenuItem RequireThemeChild(string text) {
        foreach (ToolStripItem item in RequireThemeItem().DropDownItems) {
            if (item is ToolStripMenuItem mi && string.Equals(mi.Text, text, StringComparison.Ordinal)) {
                return mi;
            }
        }

        throw new InvalidOperationException("Missing theme item: " + text);
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e) =>
        RefreshMenuChecks();

    private void RefreshMenuChecks() {
        try {
            RequireItem("Start with Windows").Checked = _startup.GetState().IsRegistered;
            RequireItem("Notifications").Checked = _preferences.NotificationsEnabled;
            RequireItem("Status bubble").Checked = _preferences.StatusBubbleEnabled;
            ThemePreference theme = _preferences.ThemePreference;
            RequireThemeChild("System").Checked = theme == ThemePreference.System;
            RequireThemeChild("Light").Checked = theme == ThemePreference.Light;
            RequireThemeChild("Dark").Checked = theme == ThemePreference.Dark;
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "TrayIcon: failed to refresh menu checks");
        }
#pragma warning restore CA1031
    }

    private void OnOpenClicked(object? sender, EventArgs e) =>
        OnOpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitClicked(object? sender, EventArgs e) =>
        OnExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnStartupClicked(object? sender, EventArgs e) {
        try {
            bool currently = _startup.GetState().IsRegistered;
            if (currently) {
                _ = _startup.Disable();
            }
            else {
                _ = _startup.Enable();
            }
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "TrayIcon: Start with Windows toggle failed");
        }
#pragma warning restore CA1031

        RefreshMenuChecks();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNotificationsClicked(object? sender, EventArgs e) {
        try {
            bool next = !_preferences.NotificationsEnabled;
            _ = _preferences.SetNotificationsEnabled(next);
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "TrayIcon: Notifications toggle failed");
        }
#pragma warning restore CA1031

        RefreshMenuChecks();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStatusBubbleClicked(object? sender, EventArgs e) {
        try {
            bool next = !_preferences.StatusBubbleEnabled;
            _ = _preferences.SetStatusBubbleEnabled(next);
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "TrayIcon: Status bubble toggle failed");
        }
#pragma warning restore CA1031

        RefreshMenuChecks();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnThemeClicked(ThemePreference preference) {
        try {
            _ = _preferences.SetThemePreference(preference);
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "TrayIcon: Theme set failed {Theme}", preference);
        }
#pragma warning restore CA1031

        RefreshMenuChecks();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e) {
        BalloonClicked?.Invoke(this, EventArgs.Empty);
    }

    public void ShowBalloon(string title, string body, NotificationSeverity severity) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        // WinForms NotifyIcon.ShowBalloonTip rejects empty tip text (ArgumentException).
        // Policy may use an empty disconnect body; delivery requires a non-empty tip.
        if (body.Length == 0) {
            body = " ";
        }

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
        _contextMenu.Opening -= OnContextMenuOpening;
        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.BalloonTipClicked -= OnBalloonTipClicked;
        _notifyIcon.ContextMenuStrip = null;
        _contextMenu.Dispose();
        _openBoldFont?.Dispose();
        _openBoldFont = null;
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
        _ownedIcon = null;
        _messageSink.DestroyHandle();
    }
}
