namespace AdbView.Adb;

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Protocol;

internal sealed class AdbCapabilities {
    private readonly Func<IAdbHostConnection> _connectionFactory;

    public AdbCapabilities(Func<IAdbHostConnection> connectionFactory) {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public AdbCapabilities(EndPoint? endPoint = null)
        : this(() => new AdbHostConnection(endPoint)) {
    }

    public async Task<int> GetVersionAsync(CancellationToken ct = default) {
        var connection = _connectionFactory();
        await using (connection.ConfigureAwait(false)) {
            return await GetVersionAsync(connection, ct).ConfigureAwait(false);
        }
    }

    public async Task<AdbServerStatus> GetServerStatusAsync(CancellationToken ct = default) {
        var connection = _connectionFactory();
        await using (connection.ConfigureAwait(false)) {
            return await GetServerStatusAsync(connection, ct).ConfigureAwait(false);
        }
    }

    public static async Task<int> GetVersionAsync(IAdbHostConnection connection, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(connection);
        var payload = await connection.ExecuteOneShotAsync("host:version", ct).ConfigureAwait(false);
        return ParseVersion(payload.Span);
    }

    public static async Task<AdbServerStatus> GetServerStatusAsync(IAdbHostConnection connection, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(connection);
        var payload = await connection.ExecuteOneShotAsync("host:server-status", ct).ConfigureAwait(false);
        return AdbServerStatus.Parse(payload.Span);
    }

    public static int ParseVersion(ReadOnlySpan<byte> payload) {
        // host:version payload must be EXACTLY 4 ASCII hex characters representing the protocol version integer (e.g. "0029" -> 41).
        if (payload.Length != 4) {
            throw new AdbProtocolException(
                AdbErrorCode.MalformedLength,
                $"Expected exactly 4 ASCII hex characters for host:version, received {payload.Length} bytes.");
        }

        int version = 0;
        for (int i = 0; i < 4; i++) {
            byte b = payload[i];
            int digit = b switch {
                >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                _ => -1
            };

            if (digit < 0) {
                throw new AdbProtocolException(
                    AdbErrorCode.MalformedLength,
                    $"Malformed host:version response contains non-hex character '{(char)b}'.");
            }

            version = (version << 4) | digit;
        }

        if (version <= 0) {
            throw new AdbProtocolException(
                AdbErrorCode.MalformedLength,
                $"Invalid protocol version: {version}. Expected positive integer.");
        }

        return version;
    }
}
