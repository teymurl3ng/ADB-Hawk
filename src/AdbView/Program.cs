using System;
using AdbView.Host;

namespace AdbView;

/// <summary>
/// Phase 0 skeleton entry point. Writes one structured log line proving the process
/// starts, then exits. No tray icon, no window, no WebView2, no ADB, no background
/// service. Real composition begins in Phase 1 (see PHASES.md).
/// </summary>
internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        string fixtureSuffix = "";
        foreach (var arg in args) {
            if (arg.StartsWith("--test-fixture=", StringComparison.Ordinal)) {
                fixtureSuffix = string.Concat("?fixture=", arg.AsSpan("--test-fixture=".Length));
            }
        }
        using var host = new AppHost(System.Diagnostics.Process.GetCurrentProcess().SessionId, fixtureSuffix);
        host.Run();
        return 0;
    }
}
