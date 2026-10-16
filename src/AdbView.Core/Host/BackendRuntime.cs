namespace AdbView.Host;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Notifications;
using AdbView.State;

/// <summary>Existing backend composition and lifetime, independent of the desktop shell.</summary>
internal sealed class BackendRuntime : IDisposable {
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _supervisorTask;
    private int _shutdown;
    private int _disposed;

    public BackendRuntime(AppSnapshot initialSnapshot) {
        StateEngine = new StateEngine(initialSnapshot);
    }

    public StateEngine StateEngine { get; }
    public CancellationToken LifetimeToken => _lifetime.Token;
    public AdbServerSupervisor? Supervisor { get; private set; }
    public DeviceTrackerLifecycle? DeviceTrackerLifecycle { get; private set; }
    public MdnsTrackerLifecycle? MdnsTrackerLifecycle { get; private set; }
    public MdnsFallbackLifecycle? MdnsFallbackLifecycle { get; private set; }
    public PairingSession? PairingSession { get; private set; }
    public QrPairingSession? QrPairingSession { get; private set; }
    public PostPairConnectCoordinator? PostPairConnect { get; private set; }
    public DeviceMetadataLifecycle? MetadataLifecycle { get; private set; }
    public NotificationCoordinator? NotificationCoordinator { get; private set; }

    public void InitializeServices(
        Func<AdbServerSupervisor>? supervisorFactory = null,
        Func<DeviceTracker>? deviceTrackerFactory = null,
        Func<MdnsTracker>? mdnsTrackerFactory = null,
        PairingCoordinator? pairingCoordinator = null,
        IMetadataProbe? metadataProbe = null,
        Func<IReadOnlyList<IPAddress>>? multicastCandidates = null,
        IHostConnectClient? postPairConnectClient = null) {
        Supervisor = supervisorFactory is not null
            ? supervisorFactory()
            : new AdbServerSupervisor(
                connectionFactory: () => new Adb.Protocol.AdbHostConnection(),
                locator: new AdbExecutableLocator(),
                processRunner: new AdbProcessRunner(),
                backoff: new Backoff());

        DeviceTrackerLifecycle = new DeviceTrackerLifecycle(
            post: evt => StateEngine.Post(evt),
            trackerFactory: deviceTrackerFactory,
            onStreamFailed: () => Supervisor?.RequestProbe(revalidateSession: false));
        MdnsTrackerLifecycle = new MdnsTrackerLifecycle(
            post: evt => StateEngine.Post(evt),
            trackerFactory: mdnsTrackerFactory);
        if (multicastCandidates is not null) {
            MdnsFallbackLifecycle = new MdnsFallbackLifecycle(
                post: evt => StateEngine.Post(evt),
                interfaces: multicastCandidates);
        }

        PairingSession = new PairingSession(
            post: evt => StateEngine.Post(evt),
            resolveOffer: offerId => {
                var state = StateEngine.CurrentDeviceState;
                return state.OfferBindings.TryGetValue(offerId, out var binding) ? binding : null;
            },
            coordinator: pairingCoordinator);
        QrPairingSession = new QrPairingSession(
            post: evt => StateEngine.Post(evt),
            startOffer: offerId => PairingSession.Start(offerId),
            submitCode: (offerId, code) => PairingSession.Submit(offerId, code),
            isStartActive: offerId => PairingSession.IsActive(offerId),
            onEnded: () => MdnsFallbackLifecycle?.StopQrQueryWindow());
        PostPairConnect = new PostPairConnectCoordinator(postPairConnectClient ?? new HostConnectClient());
        MetadataLifecycle = new DeviceMetadataLifecycle(
            postMetadata: (generation, evt) => StateEngine.PostMetadata(generation, evt),
            probe: metadataProbe);
        StateEngine.MetadataTargetsChanged += targets => MetadataLifecycle.OnTargetsChanged(targets);
    }

    public void InitializeNotifications(
        AppSnapshot initialSnapshot,
        Func<DateTimeOffset> clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        INotificationSink sink,
        Func<bool> suppressDelivery,
        bool notificationsEnabled) {
        NotificationCoordinator = new NotificationCoordinator(clock, delay, sink, suppressDelivery);
        NotificationCoordinator.SetNotificationsEnabled(notificationsEnabled);
        NotificationCoordinator.AcceptSnapshot(initialSnapshot);
    }

    public void AcceptServerState(AdbServerStateChanged state) {
        StateEngine.Post(new AdbServerStateChangedEvent(state));
        DeviceTrackerLifecycle?.OnServerStateChanged(state);
        MdnsTrackerLifecycle?.OnServerStateChanged(state);
        MdnsFallbackLifecycle?.OnServerStateChanged(state);
    }

    /// <summary>Runs for reconciliation even when no visual snapshot was published.</summary>
    public void Reconcile(AppSnapshot snapshot) {
        var state = StateEngine.CurrentDeviceState;
        PostPairConnect?.OnSnapshotPublished(snapshot, state);
        QrPairingSession?.OnSnapshotPublished(snapshot, state);
    }

    public void SetPresentationState(bool visible, bool pinned) =>
        MetadataLifecycle?.SetPresentationState(visible, pinned);

    public void Start() {
        if (Supervisor is not null) {
            _supervisorTask = Task.Run(() => Supervisor.RunAsync(_lifetime.Token));
        }
    }

    public void BeginShutdown() {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) {
            return;
        }
        NotificationCoordinator?.BeginShutdown();
        _lifetime.Cancel();
        try {
            if (_supervisorTask is not null && !_supervisorTask.IsCompleted) {
                _supervisorTask.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or AggregateException) {
            // Preserve the existing cancellation/shutdown boundary and report the observation.
            Serilog.Log.Debug(exception, "Backend supervisor completed during shutdown.");
        }
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }
        BeginShutdown();
        PairingSession?.Dispose();
        PostPairConnect?.Dispose();
        MetadataLifecycle?.Dispose();
        MdnsTrackerLifecycle?.Dispose();
        MdnsFallbackLifecycle?.Dispose();
        DeviceTrackerLifecycle?.Dispose();
        StateEngine.Dispose();
        _lifetime.Dispose();
        NotificationCoordinator?.Dispose();
    }
}
