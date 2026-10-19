namespace AdbView.WinUiMigration.Host;

using AdbView.Adb.Model;
using AdbView.Host;
using AdbView.Notifications;
using AdbView.Presentation;
using AdbView.State;
using Serilog;

// Executable Host composition only. No native UI dependency, so tests exercise this exact owner.
internal sealed class LiveBackendSession : IDisposable {
    private readonly BackendRuntime _runtime;
    private readonly SnapshotMailbox _mailbox;
    private readonly Action<Exception> _fatal;
    private int _visible;
    private int _started;
    private int _disposed;
    public LiveBackendSession(SnapshotMailbox mailbox, INotificationSink notificationSink, Action<Exception> fatal,
        BackendRuntime? runtime = null, Action<BackendRuntime>? configureServices = null,
        Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        bool notificationsEnabled = true) {
        _mailbox = mailbox; _fatal = fatal;
        _runtime = runtime ?? new BackendRuntime(StateEngine.CreateDefaultSnapshot());
        try {
            if (configureServices is null) { _runtime.InitializeServices(); }
            else { configureServices(_runtime); }
            _runtime.InitializeNotifications(_runtime.StateEngine.CurrentSnapshot, clock ?? (() => AdbView.App.SystemClock.UtcNow),
                delay ?? Task.Delay, notificationSink, () => Volatile.Read(ref _visible) != 0, notificationsEnabled);
            _runtime.StateEngine.SnapshotPublished += OnSnapshot;
            _runtime.StateEngine.StateReconciled += OnReconciled;
            if (_runtime.Supervisor is { } supervisor) { supervisor.StateChanged += OnServerState; }
            Actions = new BackendShellActions(_runtime,
                () => Log.Warning("Candidate discovery diagnostics are not composed; M08 remains incomplete"),
                _ => Log.Warning("Candidate discovery repair is not composed; M08 remains incomplete"));
        }
        catch { Dispose(); throw; }
    }
    public IShellActions Actions { get; }
    public AppSnapshot InitialSnapshot => _runtime.StateEngine.CurrentSnapshot;
    public void Start() {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) { return; }
        _runtime.Start(); Log.Information("Candidate live backend started with isolated identity");
    }
    public void RequestPin() => Post(new SetPinnedEvent(!_runtime.StateEngine.CurrentSnapshot.Shell.Pinned));
    public void PostAppearance(ShellAppearanceChangedEvent appearance) => Post(appearance);
    private void Post(AdbViewEvent value) {
        if (Volatile.Read(ref _disposed) != 0) { return; }
        if (!_runtime.StateEngine.Post(value)) { Fail(new InvalidOperationException("Candidate backend rejected a user/environment event.")); }
    }
    public void SetVisible(bool visible) {
        if (Volatile.Read(ref _disposed) != 0) { return; }
        Volatile.Write(ref _visible, visible ? 1 : 0);
        _runtime.SetPresentationState(visible, _runtime.StateEngine.CurrentSnapshot.Shell.Pinned);
    }
    public void SetNotificationsEnabled(bool enabled) {
        if (Volatile.Read(ref _disposed) == 0) { _runtime.NotificationCoordinator?.SetNotificationsEnabled(enabled); }
    }
    private void OnServerState(AdbServerStateChanged state) {
        if (Volatile.Read(ref _disposed) == 0) { _runtime.AcceptServerState(state); }
    }
    private void OnSnapshot(AppSnapshot snapshot) {
        if (Volatile.Read(ref _disposed) != 0) { return; }
        _runtime.PairingSession?.CancelIfOfferWithdrawn();
        _runtime.PairingSession?.ReconcileWithSnapshot(snapshot);
        // Publish directly from the backend thread. The mailbox owns the ONLY visual queue.
        try { _mailbox.Publish(snapshot); }
        catch (InvalidOperationException exception) { Fail(exception); return; }
        _runtime.NotificationCoordinator?.AcceptSnapshot(snapshot);
        _runtime.SetPresentationState(Volatile.Read(ref _visible) != 0, snapshot.Shell.Pinned);
        Log.Debug("Candidate backend published revision {Revision} to visual mailbox", snapshot.Revision);
    }
    private void OnReconciled(AppSnapshot snapshot) {
        if (Volatile.Read(ref _disposed) == 0) { _runtime.Reconcile(snapshot); }
    }
    private void Fail(Exception exception) {
        Log.Fatal(exception, "Candidate live delivery failed"); _fatal(exception);
    }
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _runtime.StateEngine.SnapshotPublished -= OnSnapshot;
        _runtime.StateEngine.StateReconciled -= OnReconciled;
        if (_runtime.Supervisor is { } supervisor) { supervisor.StateChanged -= OnServerState; }
        _runtime.BeginShutdown(); _runtime.Dispose();
        Log.Information("Candidate live backend owners disposed");
    }
}
