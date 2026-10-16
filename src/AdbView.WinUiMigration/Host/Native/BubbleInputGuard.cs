namespace AdbView.WinUiMigration.Host.Native;

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

// The M02-proven three-part guard, confined to this dispatcher and owned bubble HWND.
internal sealed class BubbleInputGuard : IDisposable {
    private readonly HWND _window;
    private readonly WindowMessageHook _parent;
    private readonly WindowMessageHook? _child;
    private readonly HOOKPROC _procedure;
    private readonly HHOOK _hook;
    private bool _disposed;
    public int RejectedActivations { get; private set; }
    public int ForegroundActivations { get; private set; }
    public BubbleInputGuard(Window bubble) {
        _window = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(bubble));
        nint style = PInvoke.GetWindowLongPtr(_window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetLastError(0);
        nint prior = PInvoke.SetWindowLongPtr(_window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            style | (nint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE | (nint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
        if (prior == 0 && Marshal.GetLastPInvokeError() != 0) { throw new InvalidOperationException("Bubble: nonactivation style failed."); }
        if (!PInvoke.SetWindowPos(_window, default, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED
            | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE)) { throw new InvalidOperationException("Bubble: style refresh failed."); }
        _procedure = FilterActivation;
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_CBT, _procedure, new HINSTANCE(0), PInvoke.GetCurrentThreadId());
        if (_hook.IsNull) { throw new InvalidOperationException("Bubble: activation guard registration failed."); }
        try {
            _parent = new WindowMessageHook(_window, HandleMessage);
            HWND child = PInvoke.GetWindow(_window, GET_WINDOW_CMD.GW_CHILD);
            if (!child.IsNull) { _child = new WindowMessageHook(child, RejectMouseActivation); }
        }
        catch {
            if (!PInvoke.UnhookWindowsHookEx(_hook)) { Log.Warning("Bubble: failed guard construction cleanup"); }
            throw;
        }
    }
    private static LRESULT? RejectMouseActivation(uint message, WPARAM wParam, LPARAM lParam) =>
        message == PInvoke.WM_MOUSEACTIVATE ? new LRESULT((int)PInvoke.MA_NOACTIVATE) : null;
    private LRESULT? HandleMessage(uint message, WPARAM wParam, LPARAM lParam) {
        if (message == PInvoke.WM_ACTIVATE && (wParam.Value & 0xffff) != 0 && PInvoke.GetForegroundWindow() == _window) { ForegroundActivations++; }
        return RejectMouseActivation(message, wParam, lParam);
    }
    private LRESULT FilterActivation(int code, WPARAM wParam, LPARAM lParam) {
        try {
            if (code == PInvoke.HCBT_ACTIVATE && wParam.Value == (nuint)(nint)_window) {
                RejectedActivations++; return new LRESULT(1);
            }
            return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);
        }
        catch (Exception exception) {
            Log.Fatal(exception, "Bubble activation callback failed"); Console.Error.WriteLine(exception);
            Environment.Exit(1); throw;
        }
    }
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true; _child?.Dispose(); _parent.Dispose();
        if (!PInvoke.UnhookWindowsHookEx(_hook)) { Log.Warning("Bubble: activation guard removal failed"); }
    }
}
