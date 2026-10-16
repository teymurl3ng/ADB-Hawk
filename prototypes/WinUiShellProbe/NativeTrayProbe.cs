namespace WinUiShellProbe;

using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

internal sealed class NativeTrayProbe : IDisposable {
    internal const uint CallbackMessage = 0x8000 + 37;
    // NIN_KEYSELECT = NIN_SELECT | NINF_KEY in shellapi.h; the compound macro
    // is not present in CsWin32 metadata 0.3.333.
    internal const uint KeyboardSelect = PInvoke.NIN_SELECT | 1;
    private readonly HWND _window;
    private readonly Action _toggle;
    private readonly Action _open;
    private readonly Action _exit;
    private NOTIFYICONDATAW _icon;
    private bool _disposed;

    public unsafe NativeTrayProbe(HWND window, Action toggle, Action open, Action exit) {
        _window = window;
        _toggle = toggle;
        _open = open;
        _exit = exit;
        TaskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");
        if (TaskbarCreatedMessage == 0) {
            throw new InvalidOperationException("Probe TaskbarCreated registration failed.");
        }
        _icon = new NOTIFYICONDATAW {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = window,
            uID = 1,
            guidItem = new Guid("9ab31928-8989-48aa-ac1d-99ac6976288f"),
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
                | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP | NOTIFY_ICON_DATA_FLAGS.NIF_GUID,
            uCallbackMessage = CallbackMessage,
            hIcon = PInvoke.LoadIcon(default, new PCWSTR((char*)32512)), // Shared stock icon, not owned.
        };
        if (_icon.hIcon.IsNull) {
            throw new InvalidOperationException("Probe stock icon load failed.");
        }
        _icon.szTip = "ADB View — isolated feasibility probe";
        Add();
    }

    public uint TaskbarCreatedMessage { get; }
    public int AddCount { get; private set; }
    public int SelectionCount { get; private set; }
    public int MenuCount { get; private set; }
    public bool MenuIsOpen { get; private set; }

    public LRESULT? Handle(uint message, WPARAM wParam, LPARAM lParam) {
        if (message == TaskbarCreatedMessage) {
            // A real Explorer restart removes the old registration. The test driver deletes
            // only this icon first, then sends the event to this HWND rather than broadcasting.
            Add();
            return new LRESULT(0);
        }
        if (message != CallbackMessage) {
            return null;
        }
        uint notification = (uint)(lParam.Value & 0xffff);
        uint iconId = (uint)((lParam.Value >> 16) & 0xffff);
        if (iconId != 1) {
            return new LRESULT(0);
        }
        if (notification is PInvoke.NIN_SELECT or KeyboardSelect) {
            SelectionCount++;
            _toggle();
        }
        else if (notification == PInvoke.WM_CONTEXTMENU) {
            int x = (short)(wParam.Value & 0xffff);
            int y = (short)((wParam.Value >> 16) & 0xffff);
            ShowMenu(x, y);
        }
        return new LRESULT(0);
    }

    public bool IsPointerOverIcon() {
        var identifier = new NOTIFYICONIDENTIFIER {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _window,
            uID = 1,
            guidItem = _icon.guidItem,
        };
        if (PInvoke.Shell_NotifyIconGetRect(in identifier, out var rect).Failed) {
            ProbeDiagnostics.Stage("Probe tray bounds unavailable during activation change.");
            return false;
        }
        if (!PInvoke.GetCursorPos(out var point)) {
            throw new InvalidOperationException("Probe cursor query failed.");
        }
        return point.X >= rect.left && point.X < rect.right && point.Y >= rect.top && point.Y < rect.bottom;
    }

    public void RemoveForRecreationTest() {
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in _icon)) {
            throw new InvalidOperationException("Probe icon removal for recreation test failed.");
        }
    }

    public void Dispose() {
        if (!_disposed) {
            _disposed = true;
            if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in _icon)) {
                ProbeDiagnostics.Stage("Probe tray icon deletion failed.");
            }
        }
    }

    private void Add() {
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in _icon)) {
            throw new InvalidOperationException("Probe tray icon registration failed.");
        }
        _icon.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in _icon)) {
            Dispose();
            throw new InvalidOperationException("Probe tray version negotiation failed.");
        }
        AddCount++;
    }

    private unsafe void ShowMenu(int x, int y) {
        var menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull) {
            throw new InvalidOperationException("Probe native menu creation failed.");
        }
        MenuIsOpen = true;
        try {
            fixed (char* openText = "Open probe", exitText = "Exit probe") {
                if (!PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, 1, new PCWSTR(openText))
                    || !PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, 2, new PCWSTR(exitText))) {
                    throw new InvalidOperationException("Probe native menu population failed.");
                }
            }
            if (!PInvoke.SetForegroundWindow(_window)) {
                ProbeDiagnostics.Stage("Probe menu foreground request was refused by Windows.");
            }
            int selection = PInvoke.TrackPopupMenuEx(menu, (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD
                | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON), x, y, _window, null).Value;
            MenuCount++;
            if (!PInvoke.PostMessage(_window, PInvoke.WM_NULL, default, default)) {
                throw new InvalidOperationException("Probe menu follow-up dispatch failed.");
            }
            if (selection == 1) {
                _open();
            }
            else if (selection == 2) {
                _exit();
            }
        }
        finally {
            MenuIsOpen = false;
            if (!PInvoke.DestroyMenu(menu)) {
                ProbeDiagnostics.Stage("Probe native menu destruction failed.");
            }
        }
    }
}
