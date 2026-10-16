namespace AdbView.WinUiMigration.Host;

using System;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Host;
using AdbView.Notifications;
using AdbView.State;
using AdbView.WinUiMigration.Host.Native;
using Serilog;

/// <summary>
/// M08 live-composition slice: composes the existing <see cref="BackendRuntime"/> behind the
/// candidate shell on an explicit <c>--live</c> path, mirroring the WPF AppHost reference wiring
/// (services → supervisor state → snapshot publication → notifications/reconcile → clean
/// disposal). Isolation: the runtime owns no production identity, preferences stay in memory,
/// and read-only ADB probing is the only backend activity. Deliberately NOT composed here (each
/// is recorded as remaining M08 work): persisted preferences, single-instance forwarding, the
/// diagnosis/repair owners and the QR multicast fallback source (WPF-project-owned today).
/// </summary>
internal sealed class LiveShell : IDisposable {
    private readonly BackendRuntime _runtime;
    private readonly Func<bool> _flyoutVisible;
    private readonly Action<AppSnapshot> _publishToShell;
    private readonly Action<Action> _marshalToUi;
    private NativeTray? _tray;
    private int _disposed;

    public LiveShell(
        Func<bool> flyoutVisible,
        Action<AppSnapshot> publishToShell,
        Action<Action> marshalToUi) {
        _flyoutVisible = flyoutVisible;
        _publishToShell = publishToShell;
        _marshalToUi = marshalToUi;

        AppSnapshot initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        _runtime = new BackendRuntime(initial);
        _runtime.StateEngine.SnapshotPublished += OnSnapshotPublished;
        _runtime.StateEngine.StateReconciled += OnStateReconciled;
        _runtime.InitializeServices();
        _runtime.Supervisor!.StateChanged += state => _runtime.AcceptServerState(state);
        _runtime.InitializeNotifications(
            initial,
            () => AdbView.App.SystemClock.UtcNow,
            (delay, token) => Task.Delay(delay, token),
            new NativeBalloonSink(this, marshalToUi),
            suppressDelivery: flyoutVisible,
            notificationsEnabled: true);
        Actions = new BackendShellActions(_runtime, NotComposedDiscoveryCheck, NotComposedRepair);
        Log.Information("LiveShell: backend composed (supervisor, trackers, pairing, metadata, notifications).");
    }

    /// <summary>Called once the candidate's native shell owner exists. The tray is attached after
    /// construction because <see cref="NativeShellOwner"/> needs the presenter, which needs this
    /// shell's actions — the attach step breaks that cycle without late-bound lookups.</summary>
    public void AttachTray(NativeTray tray) {
        _tray = tray;
        tray.PreferencesChanged += OnPreferencesChanged;
    }

    public IShellActions Actions { get; }
    public AppSnapshot InitialSnapshot => _runtime.StateEngine.CurrentSnapshot;

    public void Start() {
        Log.Information("LiveShell: starting backend supervisor.");
        _runtime.Start();
    }

    /// <summary>Pin stays snapshot-owned: the request rounds trips through the reducer, exactly
    /// like the WPF shell's OnPinChangeRequestedFromShell (no optimistic local state).</summary>
    public void PostPinned(bool pinned) => _runtime.StateEngine.Post(new SetPinnedEvent(pinned));

    public void PostAppearance(ShellAppearanceChangedEvent appearance) => _runtime.StateEngine.Post(appearance);

    public void SetPresentationState(bool pinned) => _runtime.SetPresentationState(_flyoutVisible(), pinned);

    private void OnSnapshotPublished(AppSnapshot snapshot) {
        if (_disposed != 0) {
            return;
        }

        // Same order as the WPF reference: pairing reconciliation first (offer withdrawal /
        // parked-slot release), then the visual projection, then notifications and the metadata
        // presentation state. The mailbox holds only the newest snapshot (M05).
        _runtime.PairingSession?.CancelIfOfferWithdrawn();
        _runtime.PairingSession?.ReconcileWithSnapshot(snapshot);
        _marshalToUi(() => {
            if (_disposed == 0) {
                _publishToShell(snapshot);
            }
        });
        _runtime.NotificationCoordinator?.AcceptSnapshot(snapshot);
        _runtime.SetPresentationState(_flyoutVisible(), snapshot.Shell.Pinned);
    }

    private void OnStateReconciled(AppSnapshot snapshot) {
        if (_disposed != 0) {
            return;
        }

        _runtime.Reconcile(snapshot);
    }

    private void OnPreferencesChanged() {
        // In-memory only for this item: persisted preferences are separate M08 work.
        bool notifications = _tray?.Notifications ?? true;
        _runtime.NotificationCoordinator?.SetNotificationsEnabled(notifications);
        Log.Information("LiveShell: notifications preference toggled in memory to {Notifications}.", notifications);
    }

    private void NotComposedDiscoveryCheck() =>
        Log.Information("LiveShell: discovery check requested; diagnosis owners are not composed on the live candidate yet (remaining M08 item).");

    private void NotComposedRepair(string repairId) =>
        Log.Information("LiveShell: discovery repair {RepairId} requested; repair owners are not composed on the live candidate yet (remaining M08 item).", repairId);

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        if (_tray is { } attached) {
            attached.PreferencesChanged -= OnPreferencesChanged;
        }

        Log.Information("LiveShell: shutting down backend owners.");
        _runtime.BeginShutdown();
        _runtime.Dispose();
        Log.Information("LiveShell: backend owners disposed.");
    }

    /// <summary>Notification sink over the candidate's native tray balloons, marshaled to the UI
    /// thread (the coordinator emits from delay continuations, like the WPF balloon sink).</summary>
    private sealed class NativeBalloonSink(LiveShell owner, Action<Action> marshalToUi) : INotificationSink {
        public void Emit(NotificationIntent intent) {
            marshalToUi(() => {
                if (owner._tray is { } target) {
                    target.ShowBalloon(intent);
                }
                else {
                    // Never silent (R-08): an emission before the native owner exists is reported.
                    Log.Warning("LiveShell: notification dropped because the native tray is not attached ({Title}).", intent.Title);
                }
            });
        }
    }
}
