namespace AdbView.Host;

using System;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Serilog;
using DomainPhase = AdbView.Adb.Model.PairingPhase;

/// <summary>
/// Application adapter: bridge pair actions → PairingCoordinator → PairingProgressedEvent.
/// Holds no AppSnapshot mutation authority.
/// </summary>
internal sealed class PairingSession : IDisposable {
    private readonly PairingCoordinator _coordinator;
    private readonly Action<AdbViewEvent> _post;
    private readonly Func<string, PairingOfferBinding?> _resolveOffer;
    private readonly object _gate = new();
    private int _disposed;

    public PairingSession(
        Action<AdbViewEvent> post,
        Func<string, PairingOfferBinding?> resolveOffer,
        PairingCoordinator? coordinator = null) {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(resolveOffer);
        _post = post;
        _resolveOffer = resolveOffer;
        _coordinator = coordinator ?? new PairingCoordinator();
        _coordinator.Progressed += OnProgressed;
    }

    internal PairingCoordinator Coordinator => _coordinator;

    public void Start(string offerId) {
        if (Volatile.Read(ref _disposed) != 0) {
            // Shutdown — safe no-op; no product UI required.
            return;
        }

        PairingOfferBinding? binding = _resolveOffer(offerId);
        if (binding is null) {
            // USER-ACTION REJECTED: stale / unknown offer — observable Failed.
            Log.Information("pairStart rejected: unknown or stale offer");
            PostFailed(offerId, null);
            return;
        }

        if (!PairingEndpoint.TryCreate(binding.Host, binding.Port, out PairingEndpoint? endpoint)
            || endpoint is null) {
            Log.Information("pairStart rejected: invalid offer endpoint");
            PostFailed(offerId, binding.DisplayName);
            return;
        }

        // Idempotent: already awaiting this offer.
        if (string.Equals(_coordinator.ActiveOfferId, offerId, StringComparison.Ordinal)
            && _coordinator.CurrentPhase == DomainPhase.AwaitingCode) {
            return;
        }

        // Decision 0017: a parked operation must not block a new pairing forever.
        //  - PairedWaitingForDevice has already achieved its purpose (the phone paired; the
        //    wait only looks for the device to appear), and
        //  - a Failed operation for a DIFFERENT offer can never be retried (TryRetry requires
        //    the same id, and QR sessions mint a fresh instance name every time).
        // A genuinely in-flight operation (AwaitingCode / Pairing) is never preempted.
        DomainPhase phase = _coordinator.CurrentPhase;
        bool parked = phase is DomainPhase.PairedWaitingForDevice
            || (phase is DomainPhase.Failed
                && !string.Equals(_coordinator.ActiveOfferId, offerId, StringComparison.Ordinal));
        if (parked) {
            Log.Information("pairStart preempting parked operation (phase={Phase})", phase);
            _coordinator.ReleaseActive();
        }

        bool started = _coordinator.TryStart(offerId, endpoint)
            || _coordinator.TryRetry(offerId, endpoint);
        if (!started) {
            // USER-ACTION REJECTED: another operation active.
            Log.Information("pairStart rejected: pairing operation already active");
            PostFailed(offerId, binding.DisplayName);
        }
    }

    public void Submit(string offerId, string code) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        // Fire-and-forget; coordinator owns validation / rate limit / network.
        _ = SubmitCoreAsync(offerId, code);
    }

    private async Task SubmitCoreAsync(string offerId, string code) {
        try {
            await _coordinator.SubmitAsync(offerId, code).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            Log.Warning(ex, "pairSubmit failed unexpectedly");
            PostFailed(offerId, _resolveOffer(offerId)?.DisplayName);
        }
    }

    public void Cancel(string offerId) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        // Decision 0017: clear whatever is active rather than requiring the clicked id to match
        // the parked operation. Cancel used to be silently inert in exactly that case.
        if (!_coordinator.CancelActive()) {
            _coordinator.Cancel(offerId);
        }
    }

    /// <summary>True when the coordinator currently holds an operation for this offer
    /// (used by the QR handoff to verify its start was accepted — decision 0017).</summary>
    public bool IsActive(string offerId) {
        ArgumentNullException.ThrowIfNull(offerId);

        return string.Equals(_coordinator.ActiveOfferId, offerId, StringComparison.Ordinal)
            && _coordinator.CurrentPhase != DomainPhase.Cleared;
    }

    /// <summary>
    /// Decision 0017: the reducer clears the operation once the pairing wait is fulfilled (the
    /// paired device appeared) or that wait expires — but only the coordinator knows the slot is
    /// still held, and a held slot rejects every later pairStart ("already active"). Release it
    /// on the snapshot that reflects completion.
    /// </summary>
    public void ReconcileWithSnapshot(AppSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        if (_coordinator.CurrentPhase is not DomainPhase.PairedWaitingForDevice) {
            return;
        }

        // Wait fulfilled: the reducer no longer carries the operation (device matched or expired).
        if (snapshot.Pairing is null) {
            Log.Information("pairing operation released: pairing wait completed");
            _coordinator.ReleaseActive();
            return;
        }

        // Belt and braces: the paired device is already connected over Wi-Fi to that endpoint.
        PairingEndpoint? endpoint = _coordinator.ActiveEndpoint;
        if (endpoint is null) {
            return;
        }

        foreach (DeviceView device in snapshot.Devices) {
            if (device.Transport == TransportKind.WiFi
                && device.State == DeviceStateKind.Connected
                && HostMatches(device.Detail.Address, endpoint.Host)) {
                Log.Information("pairing operation released: paired device connected");
                _coordinator.ReleaseActive();
                return;
            }
        }
    }

    private static bool HostMatches(string? address, string host) {
        if (string.IsNullOrEmpty(address)) {
            return false;
        }

        int colon = address.LastIndexOf(':');
        string addressHost = colon > 0 ? address[..colon] : address;
        return string.Equals(addressHost, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Releases the active operation when its offer binding no longer exists
    /// (the offer was withdrawn from discovery). The reducer already clears the
    /// snapshot card on withdrawal; without this the coordinator stays parked on
    /// the dead offer and every later pairStart is rejected (ENGINEERING.md §6.8).
    /// Decision 0014: withdrawal AFTER pairing success is expected phone behavior
    /// (Android closes the pairing advertisement on completion) — the coordinator
    /// must survive it so the in-flight result lands and the post-pair connect
    /// can fire from the preserved host.
    /// </summary>
    public void CancelIfOfferWithdrawn() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        if (_coordinator.CurrentPhase is DomainPhase.Pairing or DomainPhase.PairedWaitingForDevice) {
            Log.Information("pairing offer withdrawn while pairing/waiting; operation kept in flight");
            return;
        }

        string? active = _coordinator.ActiveOfferId;
        if (active is null || _resolveOffer(active) is not null) {
            return;
        }

        Log.Information("pairing operation released: offer withdrawn from discovery");
        _coordinator.Cancel(active);
    }

    private void OnProgressed(PairingProgressed progressed) {
        string? displayName = _resolveOffer(progressed.OfferId)?.DisplayName;
        _post(new PairingProgressedEvent(
            progressed.OfferId,
            progressed.Phase,
            progressed.FailureReason,
            progressed.FailureKind,
            displayName));
    }

    private void PostFailed(string offerId, string? displayName) {
        _post(new PairingProgressedEvent(
            offerId,
            DomainPhase.Failed,
            "pairing request rejected",
            PairingFailureKind.InvalidRequest,
            displayName));
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        lock (_gate) {
            _coordinator.Progressed -= OnProgressed;
            string? active = _coordinator.ActiveOfferId;
            if (active is not null) {
                _coordinator.Cancel(active);
            }
        }
    }
}
