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
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private AppSnapshot _currentSnapshot;
    private int _disposed;

    public AppSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);

    public event Action<AppSnapshot>? SnapshotPublished;

    public StateEngine(
        AppSnapshot? initialSnapshot = null,
        Func<DateTimeOffset>? getUtcNow = null) {
        _getUtcNow = getUtcNow ?? (() => App.SystemClock.UtcNow);
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
                            var now = _getUtcNow();
                            var current = Volatile.Read(ref _currentSnapshot);
                            var next = StateReducer.Reduce(current, domain.Event, now);
                            if (!ReferenceEquals(next, current) && next != current) {
                                Volatile.Write(ref _currentSnapshot, next);
                                SnapshotPublished?.Invoke(next);
                            }
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

    public async Task StopAsync(CancellationToken cancellationToken = default) {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) {
            return;
        }

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
