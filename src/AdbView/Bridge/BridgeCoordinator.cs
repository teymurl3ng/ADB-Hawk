namespace AdbView.Bridge;

using System;
using System.Collections.Immutable;
using AdbView.App;
using AdbView.State;
using Serilog;

internal sealed class BridgeCoordinator {
    private readonly IBridgeHost _host;
    private AppSnapshot _currentSnapshot;
    private long _revision;
    private bool _isReady;

    private readonly Action<bool>? _onSetPinned;
    private readonly Action<string>? _onPairStart;
    private readonly Action<string, string>? _onPairSubmit;
    private readonly Action<string>? _onPairCancel;

    public AppSnapshot CurrentSnapshot => _currentSnapshot;
    public long Revision => _revision;
    public bool IsReady => _isReady;

    public BridgeCoordinator(
        IBridgeHost host,
        bool isPinned = false,
        AppSnapshot? initialSnapshot = null,
        Action<bool>? onSetPinned = null,
        Action<string>? onPairStart = null,
        Action<string, string>? onPairSubmit = null,
        Action<string>? onPairCancel = null) {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _currentSnapshot = initialSnapshot ?? StateEngine.CreateDefaultSnapshot(isPinned, 1);
        _revision = _currentSnapshot.Revision;
        _onSetPinned = onSetPinned;
        _onPairStart = onPairStart;
        _onPairSubmit = onPairSubmit;
        _onPairCancel = onPairCancel;
    }

    public void UpdateSnapshot(AppSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        _currentSnapshot = snapshot;
        _revision = snapshot.Revision;
        _host.SetPinned(snapshot.Shell.Pinned);
        if (_isReady) {
            ResendCurrentSnapshot();
        }
    }

    public static AppSnapshot CreateDefaultSnapshot(bool isPinned, long revision) {
        return StateEngine.CreateDefaultSnapshot(isPinned, revision);
    }

    public void ResendCurrentSnapshot() {
        var envelope = new SnapshotEnvelope {
            Type = "snapshot",
            Protocol = BridgeProtocol.Version,
            Snapshot = _currentSnapshot
        };
        _host.SendSnapshot(envelope);
    }

    public void HandleWebMessage(string messageJson) {
        WebAction action;
        try {
            action = BridgeSerializer.DeserializeAction(messageJson);
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "bridge.action.rejected: failed to deserialize message JSON");
            return;
        }
#pragma warning restore CA1031

        HandleAction(action);
    }

    public void HandleAction(WebAction action) {
        ArgumentNullException.ThrowIfNull(action);

        switch (action) {
            case ReadyAction ready:
                HandleReady(ready);
                break;
            case PinAction:
                HandlePin(true);
                break;
            case UnpinAction:
                HandlePin(false);
                break;
            case RequestDismissAction:
                HandleRequestDismiss();
                break;
            case ReportContentSizeAction size:
                HandleReportContentSize(size);
                break;
            case PairStartAction pairStart:
                Log.Information("BridgeCoordinator: pairStart dispatch");
                _onPairStart?.Invoke(pairStart.OfferId);
                break;
            case PairSubmitAction pairSubmit:
                Log.Information("BridgeCoordinator: pairSubmit dispatch");
                _onPairSubmit?.Invoke(pairSubmit.OfferId, pairSubmit.Code);
                break;
            case PairCancelAction pairCancel:
                Log.Information("BridgeCoordinator: pairCancel dispatch");
                _onPairCancel?.Invoke(pairCancel.OfferId);
                break;
            default:
                Log.Warning("bridge.action.rejected: unhandled action type {Type}", action.GetType().Name);
                break;
        }
    }

    private void HandleReady(ReadyAction ready) {
        if (ready.Protocol != BridgeProtocol.Version) {
            Log.Warning("bridge.action.rejected: ready protocol mismatch: expected {Expected}, got {Actual}",
                BridgeProtocol.Version, ready.Protocol);
            return;
        }

        _isReady = true;
        Log.Information("Bridge ready handshake accepted (protocol {Protocol}). Resending snapshot rev {Revision}.",
            ready.Protocol, _currentSnapshot.Revision);
        ResendCurrentSnapshot();
    }

    public void SetPinned(bool pinned) {
        HandlePin(pinned);
    }

    private void HandlePin(bool pinned) {
        if (_currentSnapshot.Shell.Pinned == pinned) {
            return; // Suppress duplicate
        }

        Log.Information("BridgeCoordinator: dispatching pin state change {Pinned} to state authority", pinned);
        _onSetPinned?.Invoke(pinned);
    }

    private void HandleRequestDismiss() {
        if (_currentSnapshot.Shell.Pinned) {
            Log.Information("RequestDismiss ignored because flyout is pinned.");
            return;
        }

        Log.Information("RequestDismiss accepted; hiding flyout.");
        _host.HideFlyout();
    }

    private void HandleReportContentSize(ReportContentSizeAction action) {
        if (double.IsNaN(action.HeightEffectivePx) || double.IsInfinity(action.HeightEffectivePx)) {
            Log.Warning("bridge.action.rejected: non-finite heightEffectivePx {Height}", action.HeightEffectivePx);
            return;
        }

        int clampedHeight = Math.Clamp((int)Math.Round(action.HeightEffectivePx), 120, 560);
        ContentSizePhase phase = ParseContentSizePhase(action.Phase);
        _host.ResizeFlyout(clampedHeight, phase, action.LayoutKey);
    }

    internal static ContentSizePhase ParseContentSizePhase(string? phase) {
        if (string.IsNullOrEmpty(phase)) {
            return ContentSizePhase.Live;
        }

        if (string.Equals(phase, "structural", StringComparison.OrdinalIgnoreCase)) {
            return ContentSizePhase.Structural;
        }

        if (string.Equals(phase, "reconcile", StringComparison.OrdinalIgnoreCase)) {
            return ContentSizePhase.Reconcile;
        }

        return ContentSizePhase.Live;
    }

    public long NextRevision() {
        // Fixture / harness path for meaningful snapshot changes. Must never silently
        // clamp a changed snapshot to the same revision (Phase 4D revision invariant).
        return StateReducer.NextRevision(_revision);
    }
}
