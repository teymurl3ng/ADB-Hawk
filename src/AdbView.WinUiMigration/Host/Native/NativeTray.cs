namespace AdbView.WinUiMigration.Host.Native;

using System.Runtime.InteropServices;
using AdbView.Host;
using AdbView.Notifications;
using AdbView.State;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

internal sealed class NativeTray : IDisposable {
    public const uint CallbackMessage = 0x8000 + 81;
    public const uint KeyboardSelect = PInvoke.NIN_SELECT | 1;
    private readonly NativeReceiver _receiver;
    private readonly Action _toggle;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly Dictionary<TrayStatusKind, HICON> _icons = [];
    private NOTIFYICONDATAW _data;
    private bool _registered;
    private bool _disposed;
    public bool Notifications { get; private set; } = true;
    public bool BubbleEnabled { get; private set; }
    public bool StartupEnabled { get; private set; }
    public ThemePreference Theme { get; private set; }
    public uint TaskbarCreatedMessage { get; }
    public int AddCount { get; private set; }
    public int VersionCount { get; private set; }
    public int ModifyCount { get; private set; }
    public int SelectionCount { get; private set; }
    public bool MenuIsOpen { get; private set; }
    public bool Registered => _registered;
    public string Tooltip => _data.szTip.ToString();
    public event Action? PreferencesChanged;
    public event Action? BalloonClicked;
    public NativeTray(NativeReceiver receiver, Guid identity, Action toggle, Action open, Action exit) {
        _receiver = receiver; _toggle = toggle; _open = open; _exit = exit;
        TaskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");
        if (TaskbarCreatedMessage == 0) { throw new InvalidOperationException("Native tray: TaskbarCreated registration failed."); }
        _data = new NOTIFYICONDATAW {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = receiver.Handle,
            uID = 1,
            guidItem = identity,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP
                | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP | NOTIFY_ICON_DATA_FLAGS.NIF_GUID,
            uCallbackMessage = CallbackMessage,
            hIcon = LoadIcon(TrayStatusKind.Idle),
        };
        _data.szTip = "ADB View";
        _receiver.Message += Handle;
        try { Add(); }
        catch { Dispose(); throw; }
    }
    private unsafe HICON LoadIcon(TrayStatusKind status) {
        if (_icons.TryGetValue(status, out HICON icon)) { return icon; }
        string name = status switch {
            TrayStatusKind.Connected => "connected",
            TrayStatusKind.Attention => "attention",
            TrayStatusKind.PairingAvailable => "pairing",
            TrayStatusKind.Failure => "error",
            TrayStatusKind.Special => "special",
            _ => "idle",
        };
        using Stream stream = typeof(NativeTray).Assembly.GetManifestResourceStream("AdbView.WinUiMigration.Resources.aperture-link-" + name + ".ico")
            ?? throw new InvalidOperationException("Native tray: icon resource missing.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); byte[] bytes = memory.ToArray();
        if (bytes.Length < 22 || BitConverter.ToUInt16(bytes, 2) != 1 || BitConverter.ToUInt16(bytes, 4) == 0) { throw new InvalidOperationException("Native tray: invalid icon directory."); }
        int length = BitConverter.ToInt32(bytes, 14), offset = BitConverter.ToInt32(bytes, 18);
        if (offset < 0 || length <= 0 || offset > bytes.Length - length) { throw new InvalidOperationException("Native tray: invalid icon resource."); }
        byte[] image = bytes.AsSpan(offset, length).ToArray(); // RT_ICON requires DWORD-aligned storage, unlike the .ico directory offset.
        fixed (byte* buffer = image) { icon = PInvoke.CreateIconFromResourceEx(buffer, (uint)length, true, 0x00030000, 0, 0, 0); }
        if (icon.IsNull) { throw new InvalidOperationException("Native tray: icon creation failed."); }
        _icons.Add(status, icon); return icon;
    }
    private void Add() {
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in _data)) { throw new InvalidOperationException("Native tray: icon registration failed."); }
        _registered = true; AddCount++;
        _data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in _data)) { throw new InvalidOperationException("Native tray: version negotiation failed."); }
        VersionCount++;
    }
    public void Apply(AppSnapshot snapshot) {
        if (_disposed) { return; }
        _data.hIcon = LoadIcon(snapshot.Health.Tray);
        _data.szTip = snapshot.Health.TrayTooltip.Length > 127 ? snapshot.Health.TrayTooltip[..127] : snapshot.Health.TrayTooltip;
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in _data)) { Log.Warning("Native tray: icon modification failed"); return; }
        ModifyCount++;
    }
    public void ShowBalloon(NotificationIntent intent) {
        if (_disposed) { return; }
        NOTIFYICONDATAW balloon = BuildBalloon(intent);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in balloon)) { Log.Warning("Native tray: balloon delivery failed"); }
    }
    public NOTIFYICONDATAW BuildBalloon(NotificationIntent intent) {
        NOTIFYICONDATAW balloon = _data;
        balloon.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_GUID | NOTIFY_ICON_DATA_FLAGS.NIF_INFO;
        balloon.szInfoTitle = BalloonText.TruncateTitle(intent.Title);
        balloon.szInfo = BalloonText.TruncateBody(intent.Body);
        balloon.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_RESPECT_QUIET_TIME | NOTIFY_ICON_INFOTIP_FLAGS.NIIF_NOSOUND | (intent.Severity switch {
            NotificationSeverity.Warning => NOTIFY_ICON_INFOTIP_FLAGS.NIIF_WARNING,
            NotificationSeverity.Error => NOTIFY_ICON_INFOTIP_FLAGS.NIIF_ERROR,
            _ => NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO,
        });
        return balloon;
    }
    public LRESULT? Handle(uint message, WPARAM wParam, LPARAM lParam) {
        if (_disposed) { return null; }
        if (message == TaskbarCreatedMessage) { _registered = false; Add(); return new LRESULT(0); }
        if (message != CallbackMessage) { return null; }
        if ((uint)((lParam.Value >> 16) & 0xffff) != 1) { return new LRESULT(0); }
        uint notification = (uint)(lParam.Value & 0xffff);
        if (notification is PInvoke.NIN_SELECT or KeyboardSelect) { SelectionCount++; _toggle(); }
        else if (notification == PInvoke.WM_CONTEXTMENU) { ShowMenu((short)(wParam.Value & 0xffff), (short)((wParam.Value >> 16) & 0xffff)); }
        else if (notification == PInvoke.NIN_BALLOONUSERCLICK) { BalloonClicked?.Invoke(); }
        return new LRESULT(0);
    }
    public unsafe HMENU CreateMenu() {
        HMENU menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull) { throw new InvalidOperationException("Native tray: menu creation failed."); }
        try {
            Append(menu, 1, "Open ADB View"); Separator(menu); Append(menu, 2, "Start with Windows", StartupEnabled);
            Append(menu, 3, "Notifications", Notifications); Append(menu, 4, "Status bubble", BubbleEnabled);
            HMENU theme = PInvoke.CreatePopupMenu();
            if (theme.IsNull) { throw new InvalidOperationException("Native tray: theme menu creation failed."); }
            try {
                Append(theme, 10, "System", Theme == ThemePreference.System); Append(theme, 11, "Light", Theme == ThemePreference.Light); Append(theme, 12, "Dark", Theme == ThemePreference.Dark);
                fixed (char* text = "Theme") {
                    if (!PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_POPUP, (nuint)(nint)theme, new PCWSTR(text))) { throw new InvalidOperationException("Native tray: theme attachment failed."); }
                }
            }
            catch { if (!PInvoke.DestroyMenu(theme)) { Log.Warning("Native tray: unattached theme cleanup failed"); } throw; }
            Separator(menu); Append(menu, 5, "Exit");
            if (!PInvoke.SetMenuDefaultItem(menu, 1, 0)) { throw new InvalidOperationException("Native tray: default menu item failed."); }
            return menu;
        }
        catch { if (!PInvoke.DestroyMenu(menu)) { Log.Warning("Native tray: failed menu cleanup failed"); } throw; }
    }
    private static unsafe void Append(HMENU menu, nuint id, string text, bool check = false) {
        fixed (char* value = text) {
            if (!PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | (check ? MENU_ITEM_FLAGS.MF_CHECKED : 0), id, new PCWSTR(value))) { throw new InvalidOperationException("Native tray: menu item creation failed."); }
        }
    }
    private static unsafe void Separator(HMENU menu) {
        if (!PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, new PCWSTR(null))) { throw new InvalidOperationException("Native tray: separator creation failed."); }
    }
    public void Select(uint command) {
        if (_disposed) { return; }
        switch (command) {
            case 0: return;
            case 1: _open(); return;
            case 2: StartupEnabled = !StartupEnabled; break;
            case 3: Notifications = !Notifications; break;
            case 4: BubbleEnabled = !BubbleEnabled; break;
            case 5: _exit(); return;
            case 10: Theme = ThemePreference.System; break;
            case 11: Theme = ThemePreference.Light; break;
            case 12: Theme = ThemePreference.Dark; break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        PreferencesChanged?.Invoke();
    }
    private unsafe void ShowMenu(int x, int y) {
        HMENU menu = CreateMenu(); MenuIsOpen = true;
        try {
            if (!PInvoke.SetForegroundWindow(_receiver.Handle)) { Log.Debug("Native tray: menu activation refused by Windows"); }
            int selection = PInvoke.TrackPopupMenuEx(menu, (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON), x, y, _receiver.Handle, null).Value;
            if (!PInvoke.PostMessage(_receiver.Handle, PInvoke.WM_NULL, default, default)) { Log.Warning("Native tray: menu follow-up dispatch failed"); }
            Select((uint)selection);
            if (selection == 0 && !PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETFOCUS, in _data)) { Log.Debug("Native tray: return-focus request refused"); }
        }
        finally { MenuIsOpen = false; if (!PInvoke.DestroyMenu(menu)) { Log.Warning("Native tray: menu disposal failed"); } }
    }
    public void RemoveForRecreationTest() {
        if (!_registered || !PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in _data)) { throw new InvalidOperationException("Native tray: fixture removal failed."); }
        _registered = false;
    }
    public bool IsPointerOverIcon() {
        var identifier = new NOTIFYICONIDENTIFIER {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _receiver.Handle,
            uID = 1,
            guidItem = _data.guidItem
        };
        if (PInvoke.Shell_NotifyIconGetRect(in identifier, out var rect).Failed) {
            Log.Debug("Native tray: icon bounds unavailable during activation change"); return false;
        }
        if (!PInvoke.GetCursorPos(out var point)) { throw new InvalidOperationException("Native tray: cursor query failed."); }
        return point.X >= rect.left && point.X < rect.right && point.Y >= rect.top && point.Y < rect.bottom;
    }
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true; _receiver.Message -= Handle;
        if (_registered && !PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in _data)) { Log.Warning("Native tray: icon deletion failed"); }
        _registered = false;
        foreach (HICON icon in _icons.Values) { if (!PInvoke.DestroyIcon(icon)) { Log.Warning("Native tray: icon resource destruction failed"); } }
        _icons.Clear();
    }
}
