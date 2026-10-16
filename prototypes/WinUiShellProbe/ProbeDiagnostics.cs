namespace WinUiShellProbe;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

internal static class ProbeDiagnostics {
    private static string? _resultPath;
    private static string? _tracePath;

    public static void Start() {
        var argument = Environment.GetCommandLineArgs()
            .FirstOrDefault(value => value.StartsWith("--verify=", StringComparison.Ordinal));
        _resultPath = argument?["--verify=".Length..];
        _tracePath = (_resultPath ?? Path.Combine(AppContext.BaseDirectory, "probe")) + ".trace.txt";
        File.WriteAllText(_tracePath, "Prototype managed entry point" + Environment.NewLine);
    }

    public static void Stage(string value) {
        if (_tracePath is not null) {
            File.AppendAllText(_tracePath, $"Qpc={Stopwatch.GetTimestamp()} {value}" + Environment.NewLine);
        }
    }

    public static void Fail(Exception exception) {
        ArgumentNullException.ThrowIfNull(exception);
        Stage(exception.ToString());
        if (_resultPath is not null) {
            File.WriteAllText(_resultPath, JsonSerializer.Serialize(new {
                Passed = false,
                Stage = "Prototype startup failure",
                Error = exception.ToString(),
            }));
        }
        Environment.ExitCode = 1;
    }

}
