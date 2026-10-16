namespace AdbView.Host;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Serilog;

// The snapshot's PairingPhase is the State projection (AppSnapshot.cs); the domain enum shares
// the name — disambiguate to the State one used by the snapshot.
using PairingPhase = AdbView.State.PairingPhase;

/// <summary>
/// Decision 0009: after OUR pairing flow succeeds, issue exactly one
/// <c>host:connect</c> toward the paired device when its <c>_adb-tls-connect._tcp</c> row is
/// visible — so arrival no longer depends on the ADB server's (possibly broken) browse.
/// Never scheduled; one attempt per host:port per waiting episode; the tracker remains the sole
/// source of device truth (a successful connect does not fabricate a DeviceView).
/// </summary>
internal sealed class PostPairConnectCoordinator : IDisposable {
    private readonly IHostConnectClient _connectClient;
    private readonly object _gate = new();
    private readonly HashSet<string> _attemptedEndpoints = new(StringComparer.Ordinal);
    private string? _armedHost;
    private string? _armedOffer;
    private long _episode;
    private ImmutableArray<(string Endpoint, string Host, int Port)> _candidates = [];
    private string? _successfulEndpoint;
    private int _inFlight;
    private CancellationTokenSource? _attemptCts;
    private bool _disposed;

    public PostPairConnectCoordinator(IHostConnectClient connectClient) {
        _connectClient = connectClient;
    }

    /// <summary>AppHost forwards each accepted snapshot with the current device state.</summary>
    public void OnSnapshotPublished(AppSnapshot snapshot, DeviceEngineState deviceState) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(deviceState);

        if (snapshot.Pairing is not { Phase: PairingPhase.PairedWaitingForDevice }) {
            Disarm();
            return;
        }

        string? host = deviceState.ActivePairingHost;
        if (string.IsNullOrWhiteSpace(host)) {
            return;
        }

        lock (_gate) {
            if (_disposed) {
                return;
            }
            if (_armedHost != host || _armedOffer != snapshot.Pairing.OfferId) {
                // New pairing episode (or new host): prior attempts do not carry over.
                _attemptedEndpoints.Clear();
                _armedHost = host;
                _armedOffer = snapshot.Pairing.OfferId;
                _successfulEndpoint = null;
                _episode++;
                _attemptCts?.Cancel();
            }
        }

        ImmutableArray<RawMdnsService> effective = State.MdnsEffectiveSet.Merge(
            deviceState.LatestMdnsServices,
            deviceState.LatestFallbackServices);

        // Decision 0011 guard: if the tracker already shows a Connected Wi-Fi device for this
        // host (any port), the phone is already reachable — a second transport is the exact
        // mechanism that produced the double-card defect.
        foreach (DeviceView device in snapshot.Devices) {
            if (device.Transport == TransportKind.WiFi
                && device.State == DeviceStateKind.Connected
                && !string.IsNullOrEmpty(device.Detail.Address)
                && AddressHostEquals(device.Detail.Address, host)) {
                Log.Information(
                    "Post-pair connect skipped: {Host} already tracked over Wi-Fi.",
                    host);
                return;
            }
        }

        var candidates = ImmutableArray.CreateBuilder<(string Endpoint, string Host, int Port)>();
        var presentEndpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawMdnsService service in effective) {
            if (service.Kind != MdnsServiceKind.Connect
                || (!string.Equals(service.Ipv4, host, StringComparison.OrdinalIgnoreCase)
                    && !service.Ipv6.Contains(host, StringComparer.OrdinalIgnoreCase))
                || service.Port is < 1 or > 65535) {
                continue;
            }

            string wireHost = host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host;
            string endpoint = wireHost + ":" + service.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            presentEndpoints.Add(endpoint);
            candidates.Add((endpoint, host, service.Port));
        }

        lock (_gate) {
            // Fresh-appearance re-arm (research note §Chosen approach 2): a row that vanished and
            // re-presented is attemptable again; a continuously present row is not.
            _attemptedEndpoints.RemoveWhere(endpoint => !presentEndpoints.Contains(endpoint));
            _candidates = candidates.ToImmutable();
            if (_successfulEndpoint is not null && !presentEndpoints.Contains(_successfulEndpoint)) {
                _successfulEndpoint = null;
            }
        }

        StartEligibleAttempt();
    }

    private void StartEligibleAttempt() {
        (string Endpoint, string Host, int Port)? candidate = null;
        CancellationTokenSource? cts = null;
        CancellationToken token = default;
        long episode;
        lock (_gate) {
            episode = _episode;
            if (_disposed || _armedHost is null || _successfulEndpoint is not null || _inFlight != 0) {
                return;
            }
            foreach (var row in _candidates) {
                if (_attemptedEndpoints.Add(row.Endpoint)) {
                    candidate = row;
                    _inFlight = 1;
                    _attemptCts = new CancellationTokenSource();
                    cts = _attemptCts;
                    token = cts.Token;
                    break;
                }
            }
        }

        if (candidate is { } chosen) {
            _ = RunConnectAsync(chosen.Endpoint, chosen.Host, chosen.Port, episode, cts!, token);
        }
    }

    private async Task RunConnectAsync(string endpoint, string host, int port, long episode,
        CancellationTokenSource cts, CancellationToken token) {
        try {
            Log.Information("Post-pair connect: connecting to {Endpoint}.", endpoint);
            HostConnectResult result = await _connectClient
                .ConnectAsync(host, port, token)
                .ConfigureAwait(false);
            if (result.Outcome is HostConnectOutcome.Connected or HostConnectOutcome.AlreadyConnected) {
                lock (_gate) {
                    if (episode == _episode && _candidates.Any(row => row.Endpoint == endpoint)) {
                        _successfulEndpoint = endpoint;
                    }
                }
            }
            switch (result.Outcome) {
                case HostConnectOutcome.Connected:
                    Log.Information("Post-pair connect: connected to {Endpoint}.", endpoint);
                    break;
                case HostConnectOutcome.AlreadyConnected:
                    Log.Information("Post-pair connect: {Endpoint} already connected.", endpoint);
                    break;
                case HostConnectOutcome.Failed:
                    Log.Warning(
                        "Post-pair connect: server reported failure for {Endpoint}: {Reason}",
                        endpoint,
                        result.Reason);
                    break;
                case HostConnectOutcome.Unsupported:
                    Log.Information(
                        "Post-pair connect: server does not support the connect service: {Reason}",
                        result.Reason);
                    break;
                default:
                    Log.Warning(
                        "Post-pair connect: protocol error for {Endpoint}: {Reason}",
                        endpoint,
                        result.Reason);
                    break;
            }
        }
        catch (OperationCanceledException) {
            // App shutdown; nothing to report.
        }
#pragma warning disable CA1031 // R-08: any unexpected failure is logged; the wait continues as before.
        catch (Exception ex) {
            Log.Warning(ex, "Post-pair connect: unexpected failure for {Endpoint}.", endpoint);
        }
#pragma warning restore CA1031
        finally {
            bool pending;
            lock (_gate) {
                _inFlight = 0;
                _attemptCts = null;
                pending = !_disposed && _armedHost is not null && _successfulEndpoint is null
                    && _candidates.Any(row => !_attemptedEndpoints.Contains(row.Endpoint));
            }
            cts.Dispose();
            if (pending) {
                _ = Task.Run(StartEligibleAttempt, CancellationToken.None);
            }
        }
    }

    private void Disarm() {
        lock (_gate) {
            _armedHost = null;
            _armedOffer = null;
            _episode++;
            _attemptedEndpoints.Clear();
            _candidates = [];
            _successfulEndpoint = null;
            _attemptCts?.Cancel();
        }
    }

    public void Dispose() {
        lock (_gate) {
            _disposed = true;
            Disarm();
            _attemptCts?.Dispose();
        }
    }

    /// <summary>Device detail addresses are "host:port" (or "[ipv6]:port"); compare hosts only.</summary>
    private static bool AddressHostEquals(string address, string host) {
        if (address.StartsWith('[')) {
            int close = address.IndexOf(']', StringComparison.Ordinal);
            if (close > 1) {
                return string.Equals(address[1..close], host, StringComparison.OrdinalIgnoreCase);
            }
        }

        int colon = address.LastIndexOf(':');
        string candidate = colon > 0 ? address[..colon] : address;
        return string.Equals(candidate, host, StringComparison.OrdinalIgnoreCase);
    }
}
