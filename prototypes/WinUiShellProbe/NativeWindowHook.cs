namespace WinUiShellProbe;

using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

internal sealed class NativeWindowHook : IDisposable {
    private readonly HWND _window;
    private readonly SUBCLASSPROC _procedure;
    private readonly Func<uint, WPARAM, LPARAM, LRESULT?> _handler;
    private bool _disposed;

    public NativeWindowHook(HWND window, Func<uint, WPARAM, LPARAM, LRESULT?> handler) {
        _window = window;
        _handler = handler;
        _procedure = Dispatch;
        if (!PInvoke.SetWindowSubclass(window, _procedure, 1, 0)) {
            throw new InvalidOperationException("Probe native subclass registration failed.");
        }
    }

    public void Dispose() {
        if (!_disposed) {
            _disposed = true;
            if (!PInvoke.RemoveWindowSubclass(_window, _procedure, 1)) {
                ProbeDiagnostics.Stage("Probe native subclass removal failed.");
            }
        }
    }

    private LRESULT Dispatch(HWND window, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data) {
        // Never unwind a managed exception across the native callback boundary.
        try {
            return _handler(message, wParam, lParam) ?? PInvoke.DefSubclassProc(window, message, wParam, lParam);
        }
        catch (Exception exception) {
            ProbeDiagnostics.Fail(exception);
            Environment.Exit(1);
            // Exit is terminal. Retain the throw fallback rather than ever treating
            // an unhandled callback failure as a successful native result.
            throw;
        }
    }
}
