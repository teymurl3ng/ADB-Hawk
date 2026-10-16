namespace AdbView.Adb;

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Protocol;

internal enum HostConnectOutcome {
    /// <summary>Body reported a fresh connection ("connected to …").</summary>
    Connected,
    /// <summary>Body reported the device was already connected ("already connected to …").</summary>
    AlreadyConnected,
    /// <summary>The server answered with a failure message in the body (no FAIL frame —
    /// connect_service always writes SendProtocolString; research note §Findings 2).</summary>
    Failed,
    /// <summary>The host service does not exist on this server (pre-wireless-debugging server).</summary>
    Unsupported,
    /// <summary>Timeout, transport, or malformed framing.</summary>
    ProtocolError,
}

/// <summary>Result of one host:connect attempt. Reason text never contains secrets (host:port only).</summary>
internal sealed record HostConnectResult(HostConnectOutcome Outcome, string? Reason);

/// <summary>
/// One-shot <c>host:connect:&lt;host&gt;:&lt;port&gt;</c> client (decision 0009). The server
/// answers OKAY + hex-length + body on both success and failure, so the standard one-shot
/// exchange suffices; outcomes are classified by body prefix (research note §Findings 2).
/// No CLI fallback: the connect host service exists on every server that supports wireless
/// debugging pairing.
/// </summary>
internal interface IHostConnectClient {
    Task<HostConnectResult> ConnectAsync(string host, int port, CancellationToken ct = default);
}

internal sealed class HostConnectClient : IHostConnectClient {
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IAdbHostConnection _connection;
    private readonly TimeSpan _requestTimeout;

    public HostConnectClient(
        IAdbHostConnection? connection = null,
        TimeSpan? requestTimeout = null) {
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _connection = connection ?? new AdbHostConnection(requestTimeout: _requestTimeout);
    }

    public async Task<HostConnectResult> ConnectAsync(string host, int port, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) {
            return new HostConnectResult(HostConnectOutcome.Failed, "invalid port");
        }

        // IPv6 endpoints take [addr]:port form so the host:port split stays unambiguous.
        string wireHost = host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host;
        string service = "host:connect:" + wireHost + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var timeoutCts = new CancellationTokenSource(_requestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            ReadOnlyMemory<byte> body = await _connection
                .ExecuteOneShotAsync(service, linkedCts.Token)
                .ConfigureAwait(false);
            string text = Encoding.UTF8.GetString(body.Span);

            if (text.StartsWith("connected to", StringComparison.Ordinal)) {
                return new HostConnectResult(HostConnectOutcome.Connected, null);
            }

            if (text.StartsWith("already connected", StringComparison.Ordinal)) {
                return new HostConnectResult(HostConnectOutcome.AlreadyConnected, null);
            }

            return new HostConnectResult(HostConnectOutcome.Failed, CapReason(text));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            return new HostConnectResult(HostConnectOutcome.ProtocolError, "connect timeout");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return new HostConnectResult(HostConnectOutcome.ProtocolError, "cancelled");
        }
        catch (AdbProtocolException ex) when (ex.ErrorCode == AdbErrorCode.CommandFailed
            && ex.ServerReason is not null
            && AdbHostServiceDiagnostics.IsUnknownHostService(ex.ServerReason)) {
            return new HostConnectResult(HostConnectOutcome.Unsupported, ex.ServerReason);
        }
        catch (AdbProtocolException ex) {
            return new HostConnectResult(HostConnectOutcome.ProtocolError, CapReason(ex.Message));
        }
        catch (AdbConnectionException ex) {
            return new HostConnectResult(HostConnectOutcome.ProtocolError, ex.IsTimeout ? "connect timeout" : "transport error");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or System.IO.IOException) {
            return new HostConnectResult(HostConnectOutcome.ProtocolError, "transport error");
        }
    }

    private static string CapReason(string reason) =>
        reason.Length <= 200 ? reason : reason[..200];
}
