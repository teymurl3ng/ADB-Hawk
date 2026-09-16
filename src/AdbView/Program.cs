using System.IO;
using AdbView.Host;
using Serilog;

namespace AdbView;

/// <summary>
/// Phase 0 skeleton entry point. Writes one structured log line proving the process
/// starts, then exits. No tray icon, no window, no WebView2, no ADB, no background
/// service. Real composition begins in Phase 1 (see PHASES.md).
/// </summary>
internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        using var host = new AppHost(System.Diagnostics.Process.GetCurrentProcess().SessionId);
        host.Run();
        return 0;
    }
}
