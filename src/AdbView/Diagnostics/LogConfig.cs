using System;
using System.Globalization;
using System.IO;
using Serilog;

namespace AdbView.Diagnostics;

internal static class LogConfig {
    public static void Initialize() {
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
    }
}
