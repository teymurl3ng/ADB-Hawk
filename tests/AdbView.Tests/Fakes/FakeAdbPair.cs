namespace AdbView.Tests.Fakes;

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;

/// <summary>
/// Fake host:pair scenarios. Captures sanitized request metadata only (never the raw code).
/// </summary>
internal static class FakeAdbPair {
    internal sealed record SanitizedPairRequest(string WireEndpoint, int CodeLength, string ServicePrefix);

    public static bool TryParsePairCommand(string cmd, out SanitizedPairRequest? request) {
        request = null;
        const string prefix = "host:pair:";
        if (!cmd.StartsWith(prefix, StringComparison.Ordinal)) {
            return false;
        }

        string rest = cmd[prefix.Length..];
        int colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == rest.Length - 1) {
            return false;
        }

        string code = rest[..colon];
        string endpoint = rest[(colon + 1)..];
        request = new SanitizedPairRequest(endpoint, code.Length, prefix);
        return true;
    }

    public static async Task SendServiceAcceptedAsync(Socket socket, CancellationToken ct = default) {
        await socket.SendAsync("OKAY"u8.ToArray(), SocketFlags.None, ct).ConfigureAwait(false);
    }

    public static async Task SendFailResultAsync(Socket socket, string reason, CancellationToken ct = default) {
        byte[] reasonBytes = Encoding.ASCII.GetBytes(reason);
        byte[] header = Encoding.ASCII.GetBytes($"FAIL{reasonBytes.Length.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)}");
        await socket.SendAsync(header, SocketFlags.None, ct).ConfigureAwait(false);
        await socket.SendAsync(reasonBytes, SocketFlags.None, ct).ConfigureAwait(false);
    }

    public static async Task SendSuccessResultAsync(Socket socket, string body, CancellationToken ct = default) {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        byte[] header = Encoding.ASCII.GetBytes(bodyBytes.Length.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        await socket.SendAsync(header, SocketFlags.None, ct).ConfigureAwait(false);
        await socket.SendAsync(bodyBytes, SocketFlags.None, ct).ConfigureAwait(false);
    }

    public static async Task SendUnknownServiceFailAsync(Socket socket, CancellationToken ct = default) {
        await SendFailResultAsync(socket, "unknown host service 'host:pair:***'", ct).ConfigureAwait(false);
    }
}

/// <summary>Injectable pair client for coordinator tests without sockets.</summary>
internal sealed class FakeHostPairClient : IHostPairClient {
    private readonly Func<PairingEndpoint, string, CancellationToken, Task<HostPairResult>> _handler;
    public int CallCount { get; private set; }
    public List<string> SeenCodes { get; } = new();
    public List<string> SeenEndpoints { get; } = new();

    public FakeHostPairClient(Func<PairingEndpoint, string, CancellationToken, Task<HostPairResult>> handler) {
        _handler = handler;
    }

    public FakeHostPairClient(HostPairResult result)
        : this((_, _, _) => Task.FromResult(result)) {
    }

    public async Task<HostPairResult> PairAsync(PairingEndpoint endpoint, string code, CancellationToken ct) {
        CallCount++;
        SeenCodes.Add(code);
        SeenEndpoints.Add(endpoint.WireEndpoint);
        return await _handler(endpoint, code, ct).ConfigureAwait(false);
    }
}

internal sealed class FakePairCliFallback : IPairCliFallback {
    public int CallCount { get; private set; }
    public List<string> ArgvJoined { get; } = new();

    private readonly Func<string, PairingEndpoint, string, CancellationToken, Task<HostPairResult>> _handler;

    public FakePairCliFallback(HostPairResult result)
        : this((_, _, _, _) => Task.FromResult(result)) {
    }

    public FakePairCliFallback(Func<string, PairingEndpoint, string, CancellationToken, Task<HostPairResult>> handler) {
        _handler = handler;
    }

    public async Task<HostPairResult> PairAsync(string executablePath, PairingEndpoint endpoint, string code, CancellationToken ct) {
        CallCount++;
        // Record argv shape without the code (pair <endpoint> only).
        ArgvJoined.Add($"pair {endpoint.WireEndpoint}");
        return await _handler(executablePath, endpoint, code, ct).ConfigureAwait(false);
    }
}

internal sealed class CapturingDiagnostics {
    private readonly object _gate = new();
    private readonly List<string> _lines = new();

    public void Write(string line) {
        lock (_gate) {
            _lines.Add(line);
        }
    }

    public IReadOnlyList<string> Snapshot() {
        lock (_gate) {
            return _lines.ToArray();
        }
    }

    public string Join() {
        lock (_gate) {
            return string.Join('\n', _lines);
        }
    }
}
