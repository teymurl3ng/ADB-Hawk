namespace AdbView.Adb;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;

/// <summary>
/// Smallest CLI fallback: <c>adb.exe pair &lt;endpoint&gt;</c> with the code on stdin
/// (AOSP commandline.cpp when argc==2). Never places the code on argv.
/// </summary>
internal interface IPairCliFallback {
    Task<HostPairResult> PairAsync(string executablePath, PairingEndpoint endpoint, string code, CancellationToken ct);
}

internal sealed class PairCliFallback : IPairCliFallback {
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _timeout;

    public PairCliFallback(TimeSpan? timeout = null) {
        _timeout = timeout ?? DefaultTimeout;
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, PairingEndpoint endpoint) {
        ArgumentNullException.ThrowIfNull(executablePath);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Path.IsPathRooted(executablePath)) {
            throw new ArgumentException($"Executable path must be absolute: '{executablePath}'", nameof(executablePath));
        }

        return new ProcessStartInfo {
            FileName = executablePath,
            ArgumentList = { "pair", endpoint.WireEndpoint },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Environment inherited unchanged (R-14).
        };
    }

    public async Task<HostPairResult> PairAsync(
        string executablePath,
        PairingEndpoint endpoint,
        string code,
        CancellationToken ct) {
        if (!PairingCode.IsValid(code)) {
            return new HostPairResult(false, PairingFailureKind.InvalidRequest, "invalid pairing code");
        }

        var psi = CreateStartInfo(executablePath, endpoint);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) {
            return new HostPairResult(false, PairingFailureKind.ProtocolFailure, "failed to start adb.exe pair");
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            // Code on stdin only — never argv (ENGINEERING.md §6.2 / R-13).
            await process.StandardInput.WriteLineAsync(code.AsMemory(), linkedCts.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(linkedCts.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            var stdoutTask = DrainBoundedAsync(process.StandardOutput.BaseStream, 64 * 1024, linkedCts.Token);
            var stderrTask = DrainBoundedAsync(process.StandardError.BaseStream, 64 * 1024, linkedCts.Token);

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            string stdout = Encoding.UTF8.GetString(await stdoutTask.ConfigureAwait(false));
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode == 0 && stdout.Contains("Successfully paired", StringComparison.Ordinal)) {
                return new HostPairResult(true, PairingFailureKind.None, null);
            }

            return new HostPairResult(
                false,
                PairingFailureKind.Rejected,
                HostPairClient.SanitizeReason("pair cli failed"));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            TryKill(process);
            return new HostPairResult(false, PairingFailureKind.Timeout, "pair timeout");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            TryKill(process);
            return new HostPairResult(false, PairingFailureKind.Cancelled, "cancelled");
        }
        finally {
            TryKill(process);
        }
    }

    private static async Task<byte[]> DrainBoundedAsync(Stream stream, int maxBytes, CancellationToken ct) {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[1024];
        while (true) {
            ct.ThrowIfCancellationRequested();
            int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n == 0) {
                break;
            }

            int retained = Math.Min(n, maxBytes - (int)ms.Length);
            if (retained > 0) {
                await ms.WriteAsync(buffer.AsMemory(0, retained), ct).ConfigureAwait(false);
            }
        }

        return ms.ToArray();
    }

    private static void TryKill(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
            // Process already exited or cannot be killed; nothing further to do.
        }
    }
}
