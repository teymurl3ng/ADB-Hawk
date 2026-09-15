using System.Globalization;
using Serilog;

namespace AdbView;

/// <summary>
/// Phase 0 skeleton entry point. Writes one structured log line proving the process
/// starts, then exits. No tray icon, no window, no WebView2, no ADB, no background
/// service. Real composition begins in Phase 1 (see PHASES.md).
/// </summary>
internal static class Program {
    private const string StartupEvent = "app.start.phase0";

    [STAThread]
    private static int Main() {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ADBView",
            "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDirectory, "adbview-.log"),
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 4_194_304,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("ADB View phase 0 skeleton started {EventId}", StartupEvent);
        Log.CloseAndFlush();
        return 0;
    }
}
