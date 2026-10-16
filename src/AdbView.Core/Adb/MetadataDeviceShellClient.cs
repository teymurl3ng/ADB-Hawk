namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using Serilog;

/// <summary>
/// Smallest direct-socket primitive for Phase 7 metadata:
/// connect → <c>host:transport:&lt;serial&gt;</c> → OKAY → fixed <c>shell:…</c> → OKAY → raw until EOF.
/// Does not use <see cref="AdbHostConnection.ExecuteOneShotAsync"/> (host hex4 framing is wrong here).
/// </summary>
internal interface IMetadataDeviceShellClient {
    Task<MetadataShellRawResult> ExecuteFixedShellAsync(
        string adbSerial,
        MetadataCommand command,
        CancellationToken ct);
}

internal enum MetadataShellRawKind {
    Ok,
    TransportFailed,
    ShellFailed,
    Timeout,
    Cancelled,
    Overflow,
    ProtocolError,
}

internal sealed record MetadataShellRawResult(
    MetadataShellRawKind Kind,
    ReadOnlyMemory<byte> Body,
    string? DiagnosticReason);

internal sealed class MetadataDeviceShellClient : IMetadataDeviceShellClient {
    /// <summary>Metadata-specific body cap (research note). Not the generic 1 MiB host frame cap.</summary>
    public const int MaxShellBodyBytes = 64 * 1024;

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    private const string BatteryService = "shell:dumpsys battery";
    private const string ExactModelService = "shell:getprop ro.product.model";
    private const string AndroidVersionService = "shell:getprop ro.build.version.release";
    private const string MarketingNameService = "shell:getprop ro.product.marketname";
    private const string DeviceNameService = "shell:settings get global device_name";
    private const string BrandService = "shell:getprop ro.product.brand";
    private const string ManufacturerService = "shell:getprop ro.product.manufacturer";

    private readonly EndPoint _endPoint;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxBodyBytes;
    private readonly int _maxFrameLength;

    public MetadataDeviceShellClient(
        EndPoint? endPoint = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null,
        int maxBodyBytes = MaxShellBodyBytes,
        int maxFrameLength = AdbFraming.MaxFrameLength) {
        _endPoint = endPoint ?? AdbHostConnection.DefaultEndPoint;
        _connectTimeout = connectTimeout ?? AdbHostConnection.DefaultConnectTimeout;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _maxBodyBytes = maxBodyBytes;
        _maxFrameLength = maxFrameLength;
    }

    /// <summary>Maps the closed command enum to compile-time shell service strings.</summary>
    internal static string ServiceName(MetadataCommand command) => command switch {
        MetadataCommand.Battery => BatteryService,
        MetadataCommand.ExactModel => ExactModelService,
        MetadataCommand.AndroidVersion => AndroidVersionService,
        MetadataCommand.MarketingName => MarketingNameService,
        MetadataCommand.DeviceName => DeviceNameService,
        MetadataCommand.Brand => BrandService,
        MetadataCommand.Manufacturer => ManufacturerService,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown metadata command."),
    };

    private static readonly string[] AllowedSuffixes = [
        "dumpsys battery",
        "getprop ro.product.model",
        "getprop ro.build.version.release",
        "getprop ro.product.marketname",
        "settings get global device_name",
        "getprop ro.product.brand",
        "getprop ro.product.manufacturer",
    ];

    /// <summary>Allowed shell suffixes after <c>shell:</c> (boundary / mutation proofs).</summary>
    internal static ReadOnlySpan<string> AllowedShellCommandSuffixes => AllowedSuffixes;

    public async Task<MetadataShellRawResult> ExecuteFixedShellAsync(
        string adbSerial,
        MetadataCommand command,
        CancellationToken ct) {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbSerial);

        string transport = "host:transport:" + adbSerial;
        string shell = ServiceName(command);
        // Observation seam for Phase 7 idle-cost / refresh proofs (no serial at Information).
        Log.Information("Metadata shell request {Service}", shell);

        using var timeoutCts = new CancellationTokenSource(_requestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        NetworkStream? stream = null;
        try {
#pragma warning disable CA2000
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) {
                NoDelay = true,
            };
#pragma warning restore CA2000
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            using (var connectCts = new CancellationTokenSource(_connectTimeout))
            using (var connectLinked = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, connectCts.Token)) {
                try {
                    await socket.ConnectAsync(_endPoint, connectLinked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (connectCts.IsCancellationRequested && !ct.IsCancellationRequested) {
                    socket.Dispose();
                    return new MetadataShellRawResult(MetadataShellRawKind.Timeout, default, "connect timeout");
                }
                catch (Exception) {
                    socket.Dispose();
                    throw;
                }
            }

            stream = new NetworkStream(socket, ownsSocket: true);

            await WriteRequestAsync(stream, transport, linkedCts.Token).ConfigureAwait(false);
            var transportStatus = await ReadStatusAsync(stream, linkedCts.Token).ConfigureAwait(false);
            if (transportStatus == AdbStatus.Fail) {
                string reason = await ReadFailReasonAsync(stream, linkedCts.Token).ConfigureAwait(false);
                return new MetadataShellRawResult(
                    MetadataShellRawKind.TransportFailed,
                    default,
                    TruncateDiag(reason));
            }

            await WriteRequestAsync(stream, shell, linkedCts.Token).ConfigureAwait(false);
            var shellStatus = await ReadStatusAsync(stream, linkedCts.Token).ConfigureAwait(false);
            if (shellStatus == AdbStatus.Fail) {
                string reason = await ReadFailReasonAsync(stream, linkedCts.Token).ConfigureAwait(false);
                return new MetadataShellRawResult(
                    MetadataShellRawKind.ShellFailed,
                    default,
                    TruncateDiag(reason));
            }

            var body = await ReadRawUntilEofBoundedAsync(stream, _maxBodyBytes, linkedCts.Token).ConfigureAwait(false);
            if (body is null) {
                return new MetadataShellRawResult(MetadataShellRawKind.Overflow, default, "shell body overflow");
            }

            return new MetadataShellRawResult(MetadataShellRawKind.Ok, body.Value, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            return new MetadataShellRawResult(MetadataShellRawKind.Timeout, default, "shell timeout");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return new MetadataShellRawResult(MetadataShellRawKind.Cancelled, default, "cancelled");
        }
        catch (AdbProtocolException ex) {
            return new MetadataShellRawResult(
                MetadataShellRawKind.ProtocolError,
                default,
                TruncateDiag(ex.Message));
        }
        catch (Exception ex) when (ex is SocketException or IOException) {
            return new MetadataShellRawResult(
                MetadataShellRawKind.ProtocolError,
                default,
                "transport error");
        }
        finally {
            if (stream != null) {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteRequestAsync(Stream stream, string service, CancellationToken ct) {
        byte[] request = AdbFraming.EncodeRequest(service);
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<AdbStatus> ReadStatusAsync(Stream stream, CancellationToken ct) {
        byte[] statusBuf = new byte[AdbFraming.HeaderLength];
        await ReadExactAsync(stream, statusBuf, ct).ConfigureAwait(false);
        return AdbFraming.ParseStatus(statusBuf);
    }

    private async Task<string> ReadFailReasonAsync(Stream stream, CancellationToken ct) {
        byte[] lenBuf = new byte[AdbFraming.HeaderLength];
        await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
        int len = AdbFraming.ParseHexLength(lenBuf, _maxFrameLength);
        if (len == 0) {
            return string.Empty;
        }

        byte[] body = new byte[len];
        await ReadExactAsync(stream, body, ct).ConfigureAwait(false);
        return Encoding.ASCII.GetString(body);
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadRawUntilEofBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken ct) {
        var buffer = new byte[Math.Min(4096, maxBytes)];
        var chunks = new List<byte>(capacity: Math.Min(4096, maxBytes));
        while (true) {
            ct.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0) {
                return chunks.ToArray();
            }

            if (chunks.Count + read > maxBytes) {
                return null;
            }

            for (int i = 0; i < read; i++) {
                chunks.Add(buffer[i]);
            }
        }
    }

    private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct) {
        int total = 0;
        while (total < buffer.Length) {
            int read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0) {
                throw new AdbProtocolException(
                    AdbErrorCode.UnexpectedEof,
                    $"Unexpected EOF after reading {total} of {buffer.Length} expected bytes.");
            }

            total += read;
        }
    }

    private static string TruncateDiag(string? reason) {
        if (string.IsNullOrEmpty(reason)) {
            return "failed";
        }

        return reason.Length <= 120 ? reason : reason[..120];
    }
}
