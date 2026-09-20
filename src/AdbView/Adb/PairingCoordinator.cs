namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.App;

/// <summary>
/// Isolated pairing-domain coordinator (ENGINEERING.md §6.8).
/// Emits <see cref="PairingProgressed"/> only — no State, UI, or device fabrication.
/// </summary>
internal sealed class PairingCoordinator {
    public const int MaxSubmitsPerOfferPerMinute = 5;

    private readonly IHostPairClient _pairClient;
    private readonly IPairCliFallback _cliFallback;
    private readonly IAdbExecutableLocator _executableLocator;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly TimeSpan _pairTimeout;
    private readonly object _gate = new();

    private string? _activeOfferId;
    private PairingEndpoint? _activeEndpoint;
    private PairingPhase _phase = PairingPhase.Cleared;
    private int _operationGeneration;
    private CancellationTokenSource? _pairCts;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _submitTimes = new(StringComparer.Ordinal);

    public PairingCoordinator(
        IHostPairClient? pairClient = null,
        IPairCliFallback? cliFallback = null,
        IAdbExecutableLocator? executableLocator = null,
        Func<DateTimeOffset>? getUtcNow = null,
        TimeSpan? pairTimeout = null) {
        _pairClient = pairClient ?? new HostPairClient();
        _cliFallback = cliFallback ?? new PairCliFallback();
        _executableLocator = executableLocator ?? new AdbExecutableLocator();
        _getUtcNow = getUtcNow ?? (() => SystemClock.UtcNow);
        _pairTimeout = pairTimeout ?? HostPairClient.DefaultTimeout;
    }

    public event Action<PairingProgressed>? Progressed;

    public PairingPhase CurrentPhase {
        get {
            lock (_gate) {
                return _phase;
            }
        }
    }

    public string? ActiveOfferId {
        get {
            lock (_gate) {
                return _activeOfferId;
            }
        }
    }

    /// <summary>
    /// Begin awaiting a code for a pairing-kind endpoint. Ignored while another operation is active.
    /// </summary>
    public bool TryStart(string offerId, PairingEndpoint endpoint) {
        ArgumentNullException.ThrowIfNull(offerId);
        ArgumentNullException.ThrowIfNull(endpoint);

        lock (_gate) {
            if (_phase is PairingPhase.AwaitingCode or PairingPhase.Pairing or PairingPhase.Failed
                or PairingPhase.PairedWaitingForDevice) {
                // At most one operation; pairStart ignored while active (ENGINEERING.md §5.5).
                return false;
            }

            _activeOfferId = offerId;
            _activeEndpoint = endpoint;
            _phase = PairingPhase.AwaitingCode;
            _operationGeneration++;
        }

        Emit(offerId, PairingPhase.AwaitingCode, null, PairingFailureKind.None);
        return true;
    }

    /// <summary>
    /// Retry after Failed (DESIGN.md immediate retry). Same as Start when Failed.
    /// </summary>
    public bool TryRetry(string offerId, PairingEndpoint endpoint) {
        lock (_gate) {
            if (_phase == PairingPhase.Failed
                && string.Equals(_activeOfferId, offerId, StringComparison.Ordinal)) {
                _phase = PairingPhase.Cleared;
                _activeOfferId = null;
                _activeEndpoint = null;
            }
        }

        return TryStart(offerId, endpoint);
    }

    public void Cancel(string offerId) {
        int gen;
        CancellationTokenSource? toCancel;
        lock (_gate) {
            if (!string.Equals(_activeOfferId, offerId, StringComparison.Ordinal)) {
                return;
            }

            if (_phase is PairingPhase.Cleared) {
                return;
            }

            toCancel = _pairCts;
            _pairCts = null;
            gen = ++_operationGeneration;
            _phase = PairingPhase.Cleared;
            _activeOfferId = null;
            _activeEndpoint = null;
            _ = gen;
        }

        try {
            toCancel?.Cancel();
        }
        catch (ObjectDisposedException) {
        }

        toCancel?.Dispose();
        Emit(offerId, PairingPhase.Cleared, "cancelled", PairingFailureKind.Cancelled);
    }

    /// <summary>
    /// Submit a six-digit code. Invalid codes never call ADB and do not consume rate quota.
    /// Duplicate submit while Pairing is ignored (single in-flight request).
    /// </summary>
    public async Task SubmitAsync(string offerId, string code, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(offerId);

        if (!PairingCode.IsValid(code)) {
            bool rejectAsUserAction;
            lock (_gate) {
                if (string.Equals(_activeOfferId, offerId, StringComparison.Ordinal)
                    && _phase == PairingPhase.AwaitingCode) {
                    _phase = PairingPhase.Failed;
                    rejectAsUserAction = true;
                }
                else {
                    // USER-ACTION REJECTED: wrong offer/phase.
                    rejectAsUserAction = true;
                }
            }

            if (rejectAsUserAction) {
                Emit(offerId, PairingPhase.Failed, "invalid pairing code", PairingFailureKind.InvalidRequest);
            }

            return;
        }

        PairingEndpoint? endpoint = null;
        int generation = 0;
        CancellationTokenSource? pairCts = null;
        bool userRejected = false;
        lock (_gate) {
            if (_phase == PairingPhase.Pairing
                && string.Equals(_activeOfferId, offerId, StringComparison.Ordinal)) {
                // VALID IDEMPOTENT: duplicate concurrent submit.
                return;
            }

            if (!string.Equals(_activeOfferId, offerId, StringComparison.Ordinal)
                || _activeEndpoint is null
                || _phase is not (PairingPhase.AwaitingCode or PairingPhase.Failed)) {
                userRejected = true;
            }
            else if (!TryConsumeRateLimit_NoLock(offerId)) {
                _phase = PairingPhase.Failed;
                userRejected = false;
                // Rate limit — emit after lock.
                endpoint = null;
                generation = -1; // sentinel: rate limited
            }
            else {
                endpoint = _activeEndpoint;
                _phase = PairingPhase.Pairing;
                generation = _operationGeneration;
                pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pairCts.CancelAfter(_pairTimeout);
                _pairCts = pairCts;
            }
        }

        if (generation == -1) {
            Emit(offerId, PairingPhase.Failed, "rate limited", PairingFailureKind.RateLimited);
            return;
        }

        if (userRejected || endpoint is null || pairCts is null) {
            Emit(offerId, PairingPhase.Failed, "pairing request rejected", PairingFailureKind.InvalidRequest);
            return;
        }

        // Immediate Pairing before host:pair completes.
        Emit(offerId, PairingPhase.Pairing, null, PairingFailureKind.None);

        HostPairResult result;
        try {
            result = await _pairClient.PairAsync(endpoint, code, pairCts.Token).ConfigureAwait(false);
        }
        catch (AdbProtocolException ex) when (
            ex.ErrorCode == AdbErrorCode.CommandFailed
            && AdbHostServiceDiagnostics.IsUnknownHostService(ex.ServerReason)) {
            string? exe = _executableLocator.Locate();
            if (string.IsNullOrEmpty(exe)) {
                result = new HostPairResult(false, PairingFailureKind.ProtocolFailure, "pair service unsupported");
            }
            else {
                try {
                    result = await _cliFallback.PairAsync(exe, endpoint, code, pairCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    result = ct.IsCancellationRequested
                        ? new HostPairResult(false, PairingFailureKind.Cancelled, "cancelled")
                        : new HostPairResult(false, PairingFailureKind.Timeout, "pair timeout");
                }
            }
        }
        catch (OperationCanceledException) {
            // CancelAfter ⇒ Failed/Timeout. Explicit Cancel bumps generation so late results are dropped.
            // External SubmitAsync token ⇒ Cancelled/Cleared.
            result = ct.IsCancellationRequested
                ? new HostPairResult(false, PairingFailureKind.Cancelled, "cancelled")
                : new HostPairResult(false, PairingFailureKind.Timeout, "pair timeout");
        }

        // Drop late results after cancel / superseded generation.
        lock (_gate) {
            if (generation != _operationGeneration
                || !string.Equals(_activeOfferId, offerId, StringComparison.Ordinal)) {
                return;
            }

            if (_phase != PairingPhase.Pairing) {
                return;
            }

            if (result.Success) {
                _phase = PairingPhase.PairedWaitingForDevice;
            }
            else if (result.FailureKind == PairingFailureKind.Cancelled) {
                _phase = PairingPhase.Cleared;
                _activeOfferId = null;
                _activeEndpoint = null;
            }
            else {
                _phase = PairingPhase.Failed;
            }

            _pairCts = null;
        }

        pairCts.Dispose();

        if (result.Success) {
            Emit(offerId, PairingPhase.PairedWaitingForDevice, null, PairingFailureKind.None);
            return;
        }

        if (result.FailureKind == PairingFailureKind.Cancelled) {
            Emit(offerId, PairingPhase.Cleared, "cancelled", PairingFailureKind.Cancelled);
            return;
        }

        Emit(
            offerId,
            PairingPhase.Failed,
            result.DiagnosticReason ?? "pair failed",
            result.FailureKind);
    }

    private bool TryConsumeRateLimit_NoLock(string offerId) {
        var now = _getUtcNow();
        if (!_submitTimes.TryGetValue(offerId, out Queue<DateTimeOffset>? q)) {
            q = new Queue<DateTimeOffset>();
            _submitTimes[offerId] = q;
        }

        while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(1)) {
            q.Dequeue();
        }

        if (q.Count >= MaxSubmitsPerOfferPerMinute) {
            return false;
        }

        q.Enqueue(now);
        return true;
    }

    private void Emit(string offerId, PairingPhase phase, string? reason, PairingFailureKind kind) =>
        Progressed?.Invoke(new PairingProgressed(offerId, phase, reason, kind, _getUtcNow()));
}
