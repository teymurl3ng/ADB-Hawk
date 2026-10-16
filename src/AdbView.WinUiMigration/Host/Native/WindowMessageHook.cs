namespace AdbView.WinUiMigration.Host.Native;

using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

internal sealed class WindowMessageHook : IDisposable {
    private readonly HWND _window;
    private readonly SUBCLASSPROC _procedure;
    private readonly Func<uint, WPARAM, LPARAM, LRESULT?> _handler;
    private bool _disposed;
    public WindowMessageHook(HWND window, Func<uint, WPARAM, LPARAM, LRESULT?> handler) {
        _window = window; _handler = handler; _procedure = Dispatch;
        if (!PInvoke.SetWindowSubclass(window, _procedure, 8, 0)) { throw new InvalidOperationException("Native window: subclass registration failed."); }
    }
    private LRESULT Dispatch(HWND window, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data) {
        try { return _handler(message, wParam, lParam) ?? PInvoke.DefSubclassProc(window, message, wParam, lParam); }
        catch (Exception exception) {
            Log.Fatal(exception, "Native window callback failed"); Console.Error.WriteLine("Native window callback failed: " + exception);
            Environment.Exit(1); throw;
        }
    }
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        if (!PInvoke.RemoveWindowSubclass(_window, _procedure, 8)) { Log.Warning("Native window: subclass removal failed"); }
    }
}
