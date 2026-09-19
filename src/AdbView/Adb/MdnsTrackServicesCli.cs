namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AdbView.Adb.Protocol;

/// <summary>
/// Smallest process seam for <c>adb.exe mdns track-services --proto-text</c>.
/// Not a general command runner — fixed argv only; inherited environment unchanged.
/// </summary>
internal interface IMdnsTrackServicesCli {
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadProtoTextDocumentsAsync(
        string executablePath,
        CancellationToken ct);
}

internal sealed class MdnsTrackServicesCli : IMdnsTrackServicesCli {
    public const string Arguments = "mdns track-services --proto-text";

    /// <summary>
    /// After brace-depth returns to zero, wait this long for sibling groups from the same
    /// complete-list write before emitting the document.
    /// </summary>
    private readonly TimeSpan _documentIdle;

    public MdnsTrackServicesCli(TimeSpan? documentIdle = null) {
        _documentIdle = documentIdle ?? TimeSpan.FromMilliseconds(50);
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath) {
        ArgumentNullException.ThrowIfNull(executablePath);
        if (!Path.IsPathRooted(executablePath)) {
            throw new ArgumentException($"Executable path must be absolute: '{executablePath}'", nameof(executablePath));
        }

        return new ProcessStartInfo {
            FileName = executablePath,
            Arguments = Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Environment inherited unchanged (R-14).
        };
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadProtoTextDocumentsAsync(
        string executablePath,
        [EnumeratorCancellation] CancellationToken ct) {
        var psi = CreateStartInfo(executablePath);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) {
            throw new AdbConnectionException("Failed to start adb.exe for mDNS track-services.");
        }

        try {
            var stderrTask = DrainBoundedAsync(process.StandardError.BaseStream, maxBytes: 64 * 1024, ct);

            await foreach (var document in ReadDocumentsAsync(process.StandardOutput.BaseStream, ct).ConfigureAwait(false)) {
                yield return document;
            }

            try {
                await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            }
        }
        finally {
            TryKillProcess(process);
        }
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadDocumentsAsync(
        Stream stdout,
        [EnumeratorCancellation] CancellationToken ct) {
        var reader = new StreamReader(stdout, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var lines = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = true,
        });

        var pump = PumpLinesAsync(reader, lines.Writer, ct);

        var buffer = new StringBuilder();
        int depth = 0;
        bool inString = false;
        bool escape = false;
        bool sawOpenBrace = false;

        try {
            while (await lines.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) {
                while (lines.Reader.TryRead(out string? line)) {
                    if (line is null) {
                        if (buffer.Length > 0) {
                            yield return Encoding.UTF8.GetBytes(buffer.ToString());
                        }

                        yield break;
                    }

                    if (IsServicesDocumentStart(line) && buffer.Length > 0 && depth == 0) {
                        yield return Encoding.UTF8.GetBytes(buffer.ToString());
                        buffer.Clear();
                        sawOpenBrace = false;
                    }

                    if (buffer.Length > 0) {
                        buffer.Append('\n');
                    }

                    buffer.Append(line);
                    UpdateDepth(line, ref depth, ref inString, ref escape, ref sawOpenBrace);

                    if (sawOpenBrace && depth == 0) {
                        // Wait briefly for sibling top-level groups in the same write.
                        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        idleCts.CancelAfter(_documentIdle);
                        bool more;
                        try {
                            more = await lines.Reader.WaitToReadAsync(idleCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                            more = false;
                        }

                        if (!more) {
                            yield return Encoding.UTF8.GetBytes(buffer.ToString());
                            buffer.Clear();
                            sawOpenBrace = false;
                            depth = 0;
                            inString = false;
                            escape = false;
                        }
                    }
                }
            }
        }
        finally {
            lines.Writer.TryComplete();
            try {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
                // Pump observed stream teardown after writer completion.
            }

            reader.Dispose();
        }
    }

    private static async Task PumpLinesAsync(StreamReader reader, ChannelWriter<string?> writer, CancellationToken ct) {
        try {
            while (true) {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                await writer.WriteAsync(line, ct).ConfigureAwait(false);
                if (line is null) {
                    break;
                }
            }
        }
        catch (Exception ex) {
            writer.TryComplete(ex);
            throw;
        }
        finally {
            writer.TryComplete();
        }
    }

    private static void UpdateDepth(
        string line,
        ref int depth,
        ref bool inString,
        ref bool escape,
        ref bool sawOpenBrace) {
        for (int i = 0; i < line.Length; i++) {
            char c = line[i];
            if (inString) {
                if (escape) {
                    escape = false;
                    continue;
                }

                if (c == '\\') {
                    escape = true;
                    continue;
                }

                if (c == '"') {
                    inString = false;
                }

                continue;
            }

            if (c == '"') {
                inString = true;
                continue;
            }

            if (c == '{') {
                depth++;
                sawOpenBrace = true;
            }
            else if (c == '}') {
                depth--;
                if (depth < 0) {
                    throw new AdbProtocolException(
                        AdbErrorCode.MalformedLength,
                        "Unbalanced '}' in mDNS proto-text CLI stream.");
                }
            }
        }
    }

    private static bool IsServicesDocumentStart(string line) {
        ReadOnlySpan<char> trimmed = line.AsSpan().TrimStart();
        if (trimmed.StartsWith("Services:", StringComparison.Ordinal)) {
            return true;
        }

        if (trimmed.StartsWith("Services", StringComparison.Ordinal)) {
            ReadOnlySpan<char> rest = trimmed[8..].TrimStart();
            return rest.IsEmpty || rest[0] is '{' or ':';
        }

        return false;
    }

    private static async Task DrainBoundedAsync(Stream stream, int maxBytes, CancellationToken ct) {
        byte[] buffer = new byte[1024];
        int total = 0;
        while (true) {
            ct.ThrowIfCancellationRequested();
            int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n == 0) {
                return;
            }

            total += n;
            if (total >= maxBytes) {
                return;
            }
        }
    }

    private static void TryKillProcess(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
            // Best-effort kill of owned child only.
        }
    }
}
