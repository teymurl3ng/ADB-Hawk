namespace AdbView.State;

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

internal sealed class StateEngine : IAsyncDisposable, IDisposable {
    private abstract record EngineMessage;
    private sealed record DomainMessage(AdbViewEvent Event) : EngineMessage;
    private sealed record MetadataWorkMessage(long ExpectedGeneration, MetadataReceivedEvent Event) : EngineMessage;
    private sealed record BarrierMessage(TaskCompletionSource Tcs) : EngineMessage;

    private readonly Channel<EngineMessage> _channel;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private AppSnapshot _currentSnapshot;
    private DeviceEngineState _deviceState = DeviceEngineState.Empty;
    private ImmutableArray<MetadataProbeTargetView> _lastTargets = ImmutableArray<MetadataProbeTargetView>.Empty;
    private CancellationTokenSource? _graceCts;
    private CancellationTokenSource? _pairingWaitCts;
    private CancellationTokenSource? _wirelessRestoringCts;
    private CancellationTokenSource? _wirelessUnavailableCts;
    private CancellationTokenSource? _pairingQrCts;
    private int _disposed;
    private int _pendingCount;

    /// <summary>ENGINEERING.md §11 row 26: warn when pending work exceeds this count.</summary>
    internal const int BacklogWarnThreshold = 256;

    private static readonly TimeSpan BacklogWarnCooldown = TimeSpan.FromSeconds(30);
    private DateTimeOffset _lastBacklogWarningUtc = DateTimeOffset.MinValue;

    public AppSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    /// <summary>Diagnostic: pending engine messages (unbounded channel; Interlocked counter).</summary>
    internal int PendingCount => Volatile.Read(ref _pendingCount);

    internal DeviceEngineState CurrentDeviceState => Volatile.Read(ref _deviceState)!;

    public event Action<AppSnapshot>? SnapshotPublished;

    /// <summary>Accepted backend reconciliation, including discovery changes that do not
    /// alter presentation. Coordinators must not depend on a visible snapshot delta.</summary>
    public event Action<AppSnapshot>? StateReconciled;

    /// <summary>Authoritative metadata targets after accepted device/metadata reconciliation.</summary>
    public event Action<ImmutableArray<MetadataProbeTargetView>>? MetadataTargetsChanged;

    public StateEngine(
        AppSnapshot? initialSnapshot = null,
        Func<DateTimeOffset>? getUtcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) {
        _getUtcNow = getUtcNow ?? (() => App.SystemClock.UtcNow);
        _delay = delay ?? Task.Delay;
        _currentSnapshot = initialSnapshot ?? CreateDefaultSnapshot();

        _channel = Channel.CreateUnbounded<EngineMessage>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false,
        });

        _processingTask = Task.Run(ProcessEventsAsync);
    }

    public static AppSnapshot CreateDefaultSnapshot(bool isPinned = false, long revision = 1) {
        return new AppSnapshot {
            Revision = revision,
            GeneratedAt = App.SystemClock.UtcNow,
            Health = new HealthView {
                Kind = GlobalHealthKind.Healthy,
                Title = null,
                Detail = null,
                Hint = null,
                Tray = TrayStatusKind.Idle,
                TrayTooltip = Vocabulary.Health.NoDevicesTrayTooltip,
            },
            Shell = new ShellView {
                Pinned = isPinned,
                Theme = ThemeKind.Dark,
                TransparencyEnabled = false, // v1 opaque (decision 0004)
                ForcedColors = false,
                ReducedMotion = false,
            },
            Devices = ImmutableArray<DeviceView>.Empty,
            PairingOffers = ImmutableArray<PairingOfferView>.Empty,
            Pairing = null,
            Warnings = ImmutableArray<SubsystemWarningView>.Empty,
            DeviceCountLabel = null,
        };
    }

    public bool Post(AdbViewEvent evt) {
        ArgumentNullException.ThrowIfNull(evt);
        bool written = _channel.Writer.TryWrite(new DomainMessage(evt));
        if (written) {
            Interlocked.Increment(ref _pendingCount);
            MaybeWarnBacklog();
        }

        return written;
    }

    /// <summary>
    /// Posts metadata with generation validated at the serialized engine boundary.
    /// Stale ExpectedGeneration → no-op (even if DeviceId still exists).
    /// </summary>
    public bool PostMetadata(long expectedGeneration, MetadataReceivedEvent evt) {
        ArgumentNullException.ThrowIfNull(evt);
        bool written = _channel.Writer.TryWrite(new MetadataWorkMessage(expectedGeneration, evt));
        if (written) {
            Interlocked.Increment(ref _pendingCount);
            MaybeWarnBacklog();
        }

        return written;
    }

    private void MaybeWarnBacklog() {
        int pending = Volatile.Read(ref _pendingCount);
        if (pending < BacklogWarnThreshold) {
            return;
        }

        DateTimeOffset now = _getUtcNow();
        if (now - _lastBacklogWarningUtc < BacklogWarnCooldown) {
            return;
        }

        _lastBacklogWarningUtc = now;
        Serilog.Log.Warning(
            "StateEngine backlog pending={Pending} threshold={Threshold} (ENGINEERING §11 row 26)",
            pending,
            BacklogWarnThreshold);
    }

    public Task DrainAsync() {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new BarrierMessage(tcs))) {
            tcs.TrySetResult();
        }
        else {
            Interlocked.Increment(ref _pendingCount);
        }

        return tcs.Task;
    }

    private async Task ProcessEventsAsync() {
        Exception? failure = null;
        try {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false)) {
                while (reader.TryRead(out var msg)) {
                    Interlocked.Decrement(ref _pendingCount);
                    switch (msg) {
                        case DomainMessage domain:
                            ApplyDomainEvent(domain.Event);
                            break;
                        case MetadataWorkMessage metadata:
                            ApplyMetadataWork(metadata.ExpectedGeneration, metadata.Event);
                            break;
                        case BarrierMessage barrier:
                            barrier.Tcs.TrySetResult();
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) {
            // Expected on emergency cancellation
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            failure = ex;
            _channel.Writer.TryComplete(ex);
            Serilog.Log.Error(ex, "StateEngine processing stopped; rejecting further work.");
            if (CurrentSnapshot.Revision < StateReducer.MaxJsSafeInteger - 1) {
                ApplyDomainEvent(new AdbServerStateChangedEvent(new Adb.Model.AdbServerStateChanged(
                    Adb.Model.AdbServerPhase.Unavailable, null, 0, 0)));
                ApplyDomainEvent(new DeviceTrackerGraceExpiredEvent(CurrentDeviceState.ActiveGraceGeneration));
            }
        }
        finally {
            _channel.Writer.TryComplete(failure);
            while (_channel.Reader.TryRead(out var remaining)) {
                Interlocked.Decrement(ref _pendingCount);
                if (remaining is BarrierMessage barrier) {
                    if (failure is null) {
                        barrier.Tcs.TrySetCanceled();
                    }
                    else {
                        barrier.Tcs.TrySetException(failure);
                    }
                }
            }
        }
    }

    private void ApplyMetadataWork(long expectedGeneration, MetadataReceivedEvent evt) {
        var deviceState = Volatile.Read(ref _deviceState) ?? DeviceEngineState.Empty;
        if (!deviceState.MetadataSessions.TryGetValue(evt.DeviceId, out DeviceMetadataSession? session)
            || session.Generation != expectedGeneration) {
            return;
        }

        ApplyDomainEvent(evt);
    }

    private void ApplyDomainEvent(AdbViewEvent evt) {
        var now = _getUtcNow();
        var current = Volatile.Read(ref _currentSnapshot);
        var deviceState = Volatile.Read(ref _deviceState) ?? DeviceEngineState.Empty;
        var result = StateReducer.ReduceWithDeviceState(current, deviceState, evt, now);

        Volatile.Write(ref _deviceState, result.DeviceState);
        ApplyTimerCommand(ref _graceCts, result.Grace, StateReducer.TrackerFailureGrace, gen => new DeviceTrackerGraceExpiredEvent(gen));
        ApplyTimerCommand(ref _pairingWaitCts, result.PairingWait, StateReducer.PairingWaitTimeout, gen => new PairingWaitExpiredEvent(gen));
        ApplyTimerCommand(ref _wirelessRestoringCts, result.WirelessRestoring, StateReducer.WirelessRestoringDelay, gen => new WirelessDiscoveryRestoringExpiredEvent(gen));
        ApplyTimerCommand(ref _wirelessUnavailableCts, result.WirelessUnavailable, StateReducer.WirelessUnavailableDelay, gen => new WirelessDiscoveryUnavailableExpiredEvent(gen));
        ApplyTimerCommand(ref _pairingQrCts, result.PairingQr, StateReducer.QrSessionTimeout, gen => new PairingQrClearedEvent(gen));

        if (!ReferenceEquals(result.Snapshot, current) && result.Snapshot != current) {
            Volatile.Write(ref _currentSnapshot, result.Snapshot);
            NotifyObservers(SnapshotPublished, result.Snapshot, "snapshot");
        }

        PublishTargetsIfChanged(result.Snapshot, result.DeviceState);
        NotifyObservers(StateReconciled, result.Snapshot, "reconciliation");
    }

    private static void NotifyObservers<T>(Action<T>? observers, T value, string kind) {
        if (observers is null) {
            return;
        }

        foreach (Action<T> observer in observers.GetInvocationList()) {
            try {
                observer(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) {
                Serilog.Log.Error(ex, "StateEngine {Kind} observer failed; other observers and processing continue.", kind);
            }
        }
    }

    private void PublishTargetsIfChanged(AppSnapshot snapshot, DeviceEngineState deviceState) {
        var targets = BuildMetadataTargets(snapshot, deviceState);
        if (TargetsEqual(_lastTargets, targets)) {
            return;
        }

        _lastTargets = targets;
        NotifyObservers(MetadataTargetsChanged, targets, "metadata targets");
    }

    internal static ImmutableArray<MetadataProbeTargetView> BuildMetadataTargets(
        AppSnapshot snapshot,
        DeviceEngineState deviceState) {
        if (snapshot.Devices.IsDefaultOrEmpty || deviceState.MetadataSessions.IsEmpty) {
            return ImmutableArray<MetadataProbeTargetView>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<MetadataProbeTargetView>(snapshot.Devices.Length);
        foreach (DeviceView device in snapshot.Devices) {
            if (!deviceState.MetadataSessions.TryGetValue(device.Id, out DeviceMetadataSession? session)) {
                continue;
            }

            builder.Add(new MetadataProbeTargetView(
                device.Id,
                session.AdbSerial,
                session.Generation,
                device.State,
                device.BatteryPercent,
                session.BatteryObservedAt));
        }

        return builder.ToImmutable();
    }

    private static bool TargetsEqual(
        ImmutableArray<MetadataProbeTargetView> a,
        ImmutableArray<MetadataProbeTargetView> b) {
        if (a.IsDefault) {
            a = ImmutableArray<MetadataProbeTargetView>.Empty;
        }

        if (b.IsDefault) {
            b = ImmutableArray<MetadataProbeTargetView>.Empty;
        }

        if (a.Length != b.Length) {
            return false;
        }

        for (int i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }

        return true;
    }

    private void ApplyTimerCommand(
        ref CancellationTokenSource? slot,
        GraceCommand command,
        TimeSpan delay,
        Func<long, AdbViewEvent> factory) {
        switch (command.Kind) {
            case GraceCommandKind.Cancel:
                CancelTimer(ref slot);
                break;
            case GraceCommandKind.Schedule:
                CancelTimer(ref slot);
                var cts = new CancellationTokenSource();
                slot = cts;
                long generation = command.Generation;
#pragma warning disable CA2008 // TaskScheduler
                _ = Task.Run(async () => {
                    try {
                        await _delay(delay, cts.Token).ConfigureAwait(false);
                        Post(factory(generation));
                    }
                    catch (OperationCanceledException) {
                        // Cancelled by newer schedule or dispose.
                    }
                });
#pragma warning restore CA2008
                break;
        }
    }

    private static void CancelTimer(ref CancellationTokenSource? slot) {
        var cts = slot;
        slot = null;
        if (cts is null) {
            return;
        }

        try {
#pragma warning disable CA1849
            cts.Cancel();
#pragma warning restore CA1849
        }
        catch (ObjectDisposedException) {
        }
        finally {
            cts.Dispose();
        }
    }

    private static async Task CancelTimerAsync(CancellationTokenSource? cts) {
        if (cts is null) {
            return;
        }

        try {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) {
        }
        finally {
            cts.Dispose();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default) {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) {
            return;
        }

        await CancelTimerAsync(_graceCts).ConfigureAwait(false);
        _graceCts = null;
        await CancelTimerAsync(_pairingWaitCts).ConfigureAwait(false);
        _pairingWaitCts = null;
        await CancelTimerAsync(_wirelessRestoringCts).ConfigureAwait(false);
        _wirelessRestoringCts = null;
        await CancelTimerAsync(_wirelessUnavailableCts).ConfigureAwait(false);
        _wirelessUnavailableCts = null;
        await CancelTimerAsync(_pairingQrCts).ConfigureAwait(false);
        _pairingQrCts = null;

        _channel.Writer.TryComplete();
        try {
            await _processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            await _cts.CancelAsync().ConfigureAwait(false);
            try {
                await _processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            throw;
        }
        finally {
            _cts.Dispose();
        }
    }

    public void Dispose() {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) {
            return;
        }

        CancelTimer(ref _graceCts);
        CancelTimer(ref _pairingWaitCts);
        CancelTimer(ref _wirelessRestoringCts);
        CancelTimer(ref _wirelessUnavailableCts);
        CancelTimer(ref _pairingQrCts);
        _channel.Writer.TryComplete();
        try {
            if (!_processingTask.Wait(TimeSpan.FromSeconds(2))) {
                _cts.Cancel();
                _processingTask.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException) { }
        finally {
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync() {
        await StopAsync().ConfigureAwait(false);
    }
}
