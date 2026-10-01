namespace AdbView.Host;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
internal sealed class PostPairConnectCoordinator {
    private readonly IHostConnectClient _connectClient;
    private readonly object _gate = new();
    private readonly HashSet<string> _attemptedEndpoints = new(StringComparer.Ordinal);
    private string? _armedHost;
    private int _inFlight;

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
            if (_armedHost != host) {
                // New pairing episode (or new host): prior attempts do not carry over.
                _attemptedEndpoints.Clear();
                _armedHost = host;
            }
        }

        ImmutableArray<RawMdnsService> effective =
            !deviceState.LatestMdnsServices.IsDefaultOrEmpty
                ? deviceState.LatestMdnsServices
                : deviceState.LatestFallbackServices;

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

        string? candidate = null;
        int candidatePort = 0;
        var presentEndpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawMdnsService service in effective) {
            if (service.Kind != MdnsServiceKind.Connect
                || string.IsNullOrWhiteSpace(service.Ipv4)
                || !string.Equals(service.Ipv4, host, StringComparison.Ordinal)
                || service.Port is < 1 or > 65535) {
                continue;
            }

            string endpoint = service.Ipv4 + ":" + service.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            presentEndpoints.Add(endpoint);
            candidate ??= endpoint;
            candidatePort = service.Port;
        }

        lock (_gate) {
            // Fresh-appearance re-arm (research note §Chosen approach 2): a row that vanished and
            // re-presented is attemptable again; a continuously present row is not.
            _attemptedEndpoints.RemoveWhere(endpoint => !presentEndpoints.Contains(endpoint));
        }

        if (candidate is null) {
            return;
        }

        bool added;
        lock (_gate) {
            added = _attemptedEndpoints.Add(candidate);
        }

        if (added) {
            _ = RunConnectAsync(candidate, host, candidatePort);
        }
    }

    private async Task RunConnectAsync(string endpoint, string host, int port) {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) {
            // A previous attempt is still running; leave this endpoint unattempted so the next
            // publish can retry it once the flight clears.
            lock (_gate) {
                _attemptedEndpoints.Remove(endpoint);
            }

            return;
        }

        try {
            Log.Information("Post-pair connect: connecting to {Endpoint}.", endpoint);
            HostConnectResult result = await _connectClient
                .ConnectAsync(host, port)
                .ConfigureAwait(false);
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
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    private void Disarm() {
        lock (_gate) {
            _armedHost = null;
            _attemptedEndpoints.Clear();
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
