using System;
using AdbView.App;
using AdbView.Host;

namespace AdbView;

internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        var options = CommandLine.Parse(args);
        using var host = new AppHost(
            System.Diagnostics.Process.GetCurrentProcess().SessionId,
            options.FixtureSuffix,
            supervisorFactory: null,
            bridgeHost: null,
            headless: false,
            startupSilent: options.StartupSilent,
            nativeUi: options.NativeUi);
        host.Run();
        return 0;
    }
}
