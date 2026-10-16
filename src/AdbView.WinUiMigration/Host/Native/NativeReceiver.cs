namespace AdbView.WinUiMigration.Host.Native;

using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

/// <summary>Hidden top-level HWND on the existing WinUI dispatcher, not HWND_MESSAGE.</summary>
internal sealed class NativeReceiver : IDisposable {
    private readonly string _className = "AdbView.Candidate.Receiver." + Guid.NewGuid().ToString("N");
    private readonly WNDPROC _procedure;
    private readonly HINSTANCE _instance;
    private bool _disposed;
    public HWND Handle { get; private set; }
    public event Func<uint, WPARAM, LPARAM, LRESULT?>? Message;
    public unsafe NativeReceiver() {
        _procedure = Dispatch; _instance = new HINSTANCE(PInvoke.GetModuleHandle(new PCWSTR(null)).Value);
        if (_instance.IsNull) { throw new InvalidOperationException("Native receiver: executable module query failed."); }
        fixed (char* name = _className, title = "ADB View candidate receiver") {
            var windowClass = new WNDCLASSW { lpfnWndProc = _procedure, hInstance = _instance, lpszClassName = new PCWSTR(name) };
            if (PInvoke.RegisterClass(in windowClass) == 0) { throw new InvalidOperationException("Native receiver: class registration failed."); }
            Handle = PInvoke.CreateWindowEx(0, new PCWSTR(name), new PCWSTR(title), WINDOW_STYLE.WS_OVERLAPPED,
                0, 0, 0, 0, default, default, _instance, null);
        }
        if (Handle.IsNull) { Dispose(); throw new InvalidOperationException("Native receiver: hidden top-level window creation failed."); }
    }
    private LRESULT Dispatch(HWND window, uint message, WPARAM wParam, LPARAM lParam) {
        try {
            if (!_disposed && Message is { } handlers) {
                foreach (Func<uint, WPARAM, LPARAM, LRESULT?> handler in handlers.GetInvocationList()) {
                    if (handler(message, wParam, lParam) is { } result) { return result; }
                }
            }
            return PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception exception) {
            Log.Fatal(exception, "Native receiver callback failed");
            Console.Error.WriteLine("Native receiver callback failed: " + exception);
            Environment.Exit(1); throw;
        }
    }
    public unsafe void Dispose() {
        if (_disposed) { return; }
        _disposed = true; Message = null;
        if (!Handle.IsNull && !PInvoke.DestroyWindow(Handle)) { Log.Warning("Native receiver: window destruction failed"); }
        Handle = default;
        fixed (char* name = _className) {
            if (!PInvoke.UnregisterClass(new PCWSTR(name), _instance)) { Log.Warning("Native receiver: class removal failed"); }
        }
    }
}
