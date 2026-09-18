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

    public AppSnapshot CurrentSnapshot => _currentSnapshot;
    public long Revision => _revision;
    public bool IsReady => _isReady;

    public BridgeCoordinator(IBridgeHost host, bool isPinned = false, AppSnapshot? initialSnapshot = null) {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _currentSnapshot = initialSnapshot ?? CreateDefaultSnapshot(isPinned, 1);
        _revision = _currentSnapshot.Revision;
    }

    public void UpdateSnapshot(AppSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        _currentSnapshot = snapshot;
        _revision = snapshot.Revision;
        if (_isReady) {
            ResendCurrentSnapshot();
        }
    }

    public static AppSnapshot CreateDefaultSnapshot(bool isPinned, long revision) {
        return new AppSnapshot {
            Protocol = BridgeProtocol.Version,
            Revision = revision,
            GeneratedAt = SystemClock.UtcNow,
            Health = new HealthView {
                Kind = GlobalHealthKind.Healthy,
                Title = null,
                Detail = null,
                Hint = null,
                Tray = TrayStatusKind.Idle,
                TrayTooltip = "ADB View"
            },
            Shell = new ShellView {
                Pinned = isPinned,
                Theme = ThemeKind.Dark,
                TransparencyEnabled = true,
                ForcedColors = false,
                ReducedMotion = false
            },
            Devices = ImmutableArray<DeviceView>.Empty,
            PairingOffers = ImmutableArray<PairingOfferView>.Empty,
            Pairing = null,
            Warnings = ImmutableArray<SubsystemWarningView>.Empty,
            DeviceCountLabel = null
        };
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
            case PairStartAction:
            case PairSubmitAction:
            case PairCancelAction:
                Log.Information("BridgeCoordinator: pair action received in Wave 2 (contract shape only): {Type}", action.GetType().Name);
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

        _host.SetPinned(pinned);

        _revision = NextRevision();
        _currentSnapshot = _currentSnapshot with {
            Revision = _revision,
            GeneratedAt = SystemClock.UtcNow,
            Shell = _currentSnapshot.Shell with { Pinned = pinned }
        };

        Log.Information("Pin state changed to {Pinned}, new revision {Revision}", pinned, _revision);
        ResendCurrentSnapshot();
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
        _host.ResizeFlyout(clampedHeight);
    }

    public long NextRevision() {
        if (_revision >= BridgeProtocol.MaxSafeRevision) {
            Log.Warning("Revision reached BridgeProtocol.MaxSafeRevision ({Max}); revision cannot increment further.", BridgeProtocol.MaxSafeRevision);
            return _revision;
        }
        return _revision + 1;
    }
}
