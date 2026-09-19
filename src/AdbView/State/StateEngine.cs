namespace AdbView.State;

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

internal sealed class StateEngine : IAsyncDisposable, IDisposable {
    private abstract record EngineMessage;
    private sealed record DomainMessage(AdbViewEvent Event) : EngineMessage;
    private sealed record BarrierMessage(TaskCompletionSource Tcs) : EngineMessage;

    private readonly Channel<EngineMessage> _channel;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private AppSnapshot _currentSnapshot;
    private DeviceEngineState _deviceState = DeviceEngineState.Empty;
    private CancellationTokenSource? _graceCts;
    private int _disposed;

    public AppSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    internal DeviceEngineState CurrentDeviceState => Volatile.Read(ref _deviceState)!;

    public event Action<AppSnapshot>? SnapshotPublished;

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
            Protocol = 1,
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
                TransparencyEnabled = true,
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
        return _channel.Writer.TryWrite(new DomainMessage(evt));
    }

    public Task DrainAsync() {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new BarrierMessage(tcs))) {
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    private async Task ProcessEventsAsync() {
        try {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false)) {
                while (reader.TryRead(out var msg)) {
                    switch (msg) {
                        case DomainMessage domain:
                            ApplyDomainEvent(domain.Event);
                            break;
                        case BarrierMessage barrier:
                            barrier.Tcs.TrySetResult();
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) {
            // Expected on emergency cancellation
        }
    }

    private void ApplyDomainEvent(AdbViewEvent evt) {
        var now = _getUtcNow();
        var current = Volatile.Read(ref _currentSnapshot);
        var deviceState = Volatile.Read(ref _deviceState) ?? DeviceEngineState.Empty;
        var result = StateReducer.ReduceWithDeviceState(current, deviceState, evt, now);

        Volatile.Write(ref _deviceState, result.DeviceState);
        ApplyGraceCommand(result.Grace);

        if (!ReferenceEquals(result.Snapshot, current) && result.Snapshot != current) {
            Volatile.Write(ref _currentSnapshot, result.Snapshot);
            SnapshotPublished?.Invoke(result.Snapshot);
        }
    }

    private void ApplyGraceCommand(GraceCommand command) {
        switch (command.Kind) {
            case GraceCommandKind.Cancel:
                CancelGraceTimer();
                break;
            case GraceCommandKind.Schedule:
                CancelGraceTimer();
                var cts = new CancellationTokenSource();
                _graceCts = cts;
                long generation = command.Generation;
#pragma warning disable CA2008 // TaskScheduler
                _ = Task.Run(async () => {
                    try {
                        await _delay(StateReducer.TrackerFailureGrace, cts.Token).ConfigureAwait(false);
                        Post(new DeviceTrackerGraceExpiredEvent(generation));
                    }
                    catch (OperationCanceledException) {
                        // Grace cancelled by newer list or dispose.
                    }
                });
#pragma warning restore CA2008
                break;
        }
    }

    private void CancelGraceTimer() {
        var cts = _graceCts;
        _graceCts = null;
        if (cts is null) {
            return;
        }

        try {
#pragma warning disable CA1849 // Sync dispose/cancel path; CancelAsync is used from StopAsync.
            cts.Cancel();
#pragma warning restore CA1849
        }
        catch (ObjectDisposedException) {
            // Already disposed.
        }
        finally {
            cts.Dispose();
        }
    }

    private async Task CancelGraceTimerAsync() {
        var cts = _graceCts;
        _graceCts = null;
        if (cts is null) {
            return;
        }

        try {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) {
            // Already disposed.
        }
        finally {
            cts.Dispose();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default) {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) {
            return;
        }

        await CancelGraceTimerAsync().ConfigureAwait(false);
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

        CancelGraceTimer();
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
