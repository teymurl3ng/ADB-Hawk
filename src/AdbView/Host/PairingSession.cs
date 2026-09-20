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

        _coordinator.Cancel(offerId);
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
