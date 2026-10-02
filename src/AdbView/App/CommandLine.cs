namespace AdbView.App;

using System;

/// <summary>
/// Minimal argv parsing. ENGINEERING.md: --startup, --test-fixture, --verbose (future).
/// </summary>
internal sealed record LaunchOptions(
    bool StartupSilent,
    string FixtureSuffix,
    bool HeadlessSelfTest,
    bool NativeUi);

internal static class CommandLine {
    public static LaunchOptions Parse(string[] args) {
        ArgumentNullException.ThrowIfNull(args);

        bool startupSilent = false;
        string fixtureSuffix = "";
        bool headlessSelfTest = false;
        bool nativeUi = false;

        foreach (string arg in args) {
            if (string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)) {
                startupSilent = true;
                continue;
            }

            if (string.Equals(arg, "--exit-after-startup-selftest", StringComparison.OrdinalIgnoreCase)) {
                headlessSelfTest = true;
                continue;
            }

            if (string.Equals(arg, "--ui=native", StringComparison.OrdinalIgnoreCase)) {
                nativeUi = true;
                continue;
            }

            if (arg.StartsWith("--test-fixture=", StringComparison.OrdinalIgnoreCase)) {
                fixtureSuffix = string.Concat("?fixture=", arg.AsSpan("--test-fixture=".Length));
            }
        }

        return new LaunchOptions(startupSilent, fixtureSuffix, headlessSelfTest, nativeUi);
    }
}
