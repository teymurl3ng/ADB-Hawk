namespace AdbView.Adb;

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;

/// <summary>Result of a single host:pair / CLI pair attempt. Never includes the code.</summary>
internal sealed record HostPairResult(
    bool Success,
    PairingFailureKind FailureKind,
    string? DiagnosticReason);

/// <summary>
/// Dedicated <c>host:pair</c> client (AOSP <c>services.cpp</c> / <c>commandline.cpp</c> @ 1cf2f017…).
/// Does not use <see cref="AdbHostConnection.ExecuteOneShotAsync"/> because failure after the
/// service ACK is <c>FAIL</c>+length+reason, not a hex-length payload.
/// </summary>
internal interface IHostPairClient {
    Task<HostPairResult> PairAsync(PairingEndpoint endpoint, string code, CancellationToken ct);
}

internal sealed class HostPairClient : IHostPairClient {
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly EndPoint _endPoint;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxFrameLength;

    public HostPairClient(
        EndPoint? endPoint = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null,
        int maxFrameLength = AdbFraming.MaxFrameLength) {
        _endPoint = endPoint ?? AdbHostConnection.DefaultEndPoint;
        _connectTimeout = connectTimeout ?? AdbHostConnection.DefaultConnectTimeout;
        _requestTimeout = requestTimeout ?? DefaultTimeout;
        _maxFrameLength = maxFrameLength;
    }

    /// <summary>Builds the host service name. Caller must pass a validated six-digit code.</summary>
    internal static string BuildServiceName(string code, PairingEndpoint endpoint) =>
        "host:pair:" + code + ":" + endpoint.WireEndpoint;

    /// <summary>Diagnostic label that never includes the pairing code.</summary>
    internal static string DiagnosticServiceName(PairingEndpoint endpoint) =>
        "host:pair:***:" + endpoint.WireEndpoint;

    public async Task<HostPairResult> PairAsync(PairingEndpoint endpoint, string code, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!PairingCode.IsValid(code)) {
            return new HostPairResult(false, PairingFailureKind.InvalidRequest, "invalid pairing code");
        }

        string service = BuildServiceName(code, endpoint);
        string diag = DiagnosticServiceName(endpoint);

        using var timeoutCts = new CancellationTokenSource(_requestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        NetworkStream? stream = null;
        try {
            // CA2000: ownership transfers to NetworkStream(ownsSocket: true) after connect;
            // connect failures dispose the socket explicitly (same pattern as AdbHostConnection).
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
                    return new HostPairResult(false, PairingFailureKind.Timeout, "connect timeout");
                }
                catch (Exception) {
                    socket.Dispose();
                    throw;
                }
            }

            stream = new NetworkStream(socket, ownsSocket: true);

            byte[] request = AdbFraming.EncodeRequest(service);
            await stream.WriteAsync(request, linkedCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(linkedCts.Token).ConfigureAwait(false);

            byte[] statusBuf = new byte[AdbFraming.HeaderLength];
            await ReadExactAsync(stream, statusBuf, linkedCts.Token).ConfigureAwait(false);
            var status = AdbFraming.ParseStatus(statusBuf);

            if (status == AdbStatus.Fail) {
                string reason = await ReadFailReasonAsync(stream, linkedCts.Token).ConfigureAwait(false);
                var kind = AdbHostServiceDiagnostics.IsUnknownHostService(reason)
                    ? PairingFailureKind.ProtocolFailure
                    : PairingFailureKind.Rejected;
                // Unknown host service is distinguished via ServerReason on a thrown path for coordinator fallback.
                if (AdbHostServiceDiagnostics.IsUnknownHostService(reason)) {
                    throw new AdbProtocolException(
                        AdbErrorCode.CommandFailed,
                        $"ADB command '{diag}' failed.",
                        serverReason: reason);
                }

                return new HostPairResult(false, kind, SanitizeReason(reason));
            }

            // Service accepted (OKAY). Final result is either FAIL+len+reason or hex-len+body.
            byte[] next = new byte[AdbFraming.HeaderLength];
            await ReadExactAsync(stream, next, linkedCts.Token).ConfigureAwait(false);

            if (IsFailHeader(next)) {
                string reason = await ReadFailReasonAsync(stream, linkedCts.Token).ConfigureAwait(false);
                return new HostPairResult(false, ClassifyFailReason(reason), SanitizeReason(reason));
            }

            int payloadLength = AdbFraming.ParseHexLength(next, _maxFrameLength);
            if (payloadLength < 0 || payloadLength > _maxFrameLength) {
                return new HostPairResult(false, PairingFailureKind.MalformedResponse, "invalid pair payload length");
            }

            if (payloadLength == 0) {
                return new HostPairResult(false, PairingFailureKind.MalformedResponse, "empty pair success body");
            }

            byte[] body = new byte[payloadLength];
            await ReadExactAsync(stream, body, linkedCts.Token).ConfigureAwait(false);
            string text = Encoding.UTF8.GetString(body);
            if (text.StartsWith("Successful", StringComparison.Ordinal)) {
                return new HostPairResult(true, PairingFailureKind.None, null);
            }

            return new HostPairResult(false, PairingFailureKind.MalformedResponse, SanitizeReason(text));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            return new HostPairResult(false, PairingFailureKind.Timeout, "pair timeout");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return new HostPairResult(false, PairingFailureKind.Cancelled, "cancelled");
        }
        catch (AdbProtocolException ex) when (ex.ErrorCode == AdbErrorCode.CommandFailed
            && AdbHostServiceDiagnostics.IsUnknownHostService(ex.ServerReason)) {
            throw;
        }
        catch (AdbProtocolException ex) {
            return new HostPairResult(
                false,
                ex.ErrorCode is AdbErrorCode.MalformedLength or AdbErrorCode.InvalidStatus or AdbErrorCode.UnexpectedEof
                    ? PairingFailureKind.MalformedResponse
                    : PairingFailureKind.ProtocolFailure,
                SanitizeReason(ex.Message));
        }
        catch (Exception ex) when (ex is SocketException or IOException) {
            return new HostPairResult(false, PairingFailureKind.ProtocolFailure, "transport error");
        }
        finally {
            if (stream != null) {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsFailHeader(ReadOnlySpan<byte> header) =>
        header.Length == 4
        && header[0] == (byte)'F'
        && header[1] == (byte)'A'
        && header[2] == (byte)'I'
        && header[3] == (byte)'L';

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

    private static PairingFailureKind ClassifyFailReason(string reason) {
        if (reason.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("connection was dropped", StringComparison.OrdinalIgnoreCase)) {
            return PairingFailureKind.Rejected;
        }

        return PairingFailureKind.Rejected;
    }

    /// <summary>Strip anything that could echo a six-digit code from diagnostic text.</summary>
    internal static string SanitizeReason(string? reason) {
        if (string.IsNullOrEmpty(reason)) {
            return "pair failed";
        }

        // Drop host:pair:*** fragments if present; never pass through raw service lines.
        if (reason.Contains("host:pair:", StringComparison.Ordinal)) {
            return "pair failed";
        }

        // Cap length for logs.
        return reason.Length <= 200 ? reason : reason[..200];
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
}
