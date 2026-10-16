namespace AdbView.Adb;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

internal interface IAdbProcessRunner {
    Task<ProcessResult> StartServerAsync(string executablePath, CancellationToken ct = default);

    /// <summary>
    /// Decision 0008 amendment: the single sanctioned kill — user-initiated ADB restart from the
    /// discovery-health card. Never called automatically; the supervisor owns recovery afterwards.
    /// </summary>
    Task<ProcessResult> KillServerAsync(string executablePath, CancellationToken ct = default);
}

internal sealed class AdbProcessRunner : IAdbProcessRunner {
    public const int DefaultTimeoutSeconds = 20;

    internal static ProcessStartInfo CreateStartInfo(string executablePath) {
        return CreateStartInfo(executablePath, "start-server");
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, string arguments) {
        ArgumentNullException.ThrowIfNull(executablePath);
        if (!Path.IsPathRooted(executablePath)) {
            throw new ArgumentException($"Executable path must be absolute: '{executablePath}'", nameof(executablePath));
        }

        return new ProcessStartInfo {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
            // Environment is inherited unchanged; do not touch psi.Environment or psi.EnvironmentVariables (R-14, §6.3).
        };
    }

    public async Task<ProcessResult> StartServerAsync(string executablePath, CancellationToken ct = default) {
        return await RunAsync(CreateStartInfo(executablePath, "start-server"), ct).ConfigureAwait(false);
    }

    public Task<ProcessResult> KillServerAsync(string executablePath, CancellationToken ct = default) {
        return RunAsync(CreateStartInfo(executablePath, "kill-server"), ct);
    }

    private static async Task<ProcessResult> RunAsync(ProcessStartInfo psi, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = psi };
        try {
            if (!process.Start()) {
                return new ProcessResult(-1, string.Empty, "Failed to start process", false);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) {
            Serilog.Log.Warning(ex, "ADB process launch failed.");
            return new ProcessResult(-1, string.Empty, "ADB process launch failed", false);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(DefaultTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            return new ProcessResult(process.ExitCode, stdout, stderr, false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            // Process timed out after 20s
            TryKillProcess(process);
            return new ProcessResult(-1, string.Empty, "Process timed out after 20 seconds", true);
        }
        catch (OperationCanceledException) {
            // Cancellation requested by caller
            TryKillProcess(process);
            throw;
        }
    }

    private static void TryKillProcess(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
            Serilog.Log.Debug(ex, "ADB owned-child cleanup could not complete.");
        }
    }
}
