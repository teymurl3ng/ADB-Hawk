namespace AdbView.Host;

using System;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Serilog;

/// <summary>
/// Owns DeviceTracker session lifetime against supervisor generation.
/// DeviceTracker remains unaware of StateEngine; this class only posts events.
/// </summary>
internal sealed class DeviceTrackerLifecycle : IDisposable {
    private readonly Func<DeviceTracker> _trackerFactory;
    private readonly Action<AdbViewEvent> _post;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private DeviceTracker? _tracker;
    private int? _runningGeneration;
    private int _disposed;

    public DeviceTrackerLifecycle(Action<AdbViewEvent> post, Func<DeviceTracker>? trackerFactory = null) {
        ArgumentNullException.ThrowIfNull(post);
        _post = post;
        _trackerFactory = trackerFactory ?? (() => new DeviceTracker(
            onDegraded: reason => Log.Warning("DeviceTracker degraded: {Reason}", reason),
            onMalformedPayload: reason => Log.Information("DeviceTracker malformed payload: {Reason}", reason)));
    }

    public int? RunningGeneration {
        get {
            lock (_gate) {
                return _runningGeneration;
            }
        }
    }

    public bool IsRunning {
        get {
            lock (_gate) {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    public void OnServerStateChanged(AdbServerStateChanged state) {
        ArgumentNullException.ThrowIfNull(state);

        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        if (state.Phase == AdbServerPhase.Up) {
            EnsureRunning(state.Generation);
        }
        else {
            Stop();
        }
    }

    public void EnsureRunning(int generation) {
        lock (_gate) {
            if (Volatile.Read(ref _disposed) != 0) {
                return;
            }

            if (_runningGeneration == generation && _runTask is { IsCompleted: false }) {
                return;
            }

            StopLocked();
            StartLocked(generation);
        }
    }

    public void Stop() {
        lock (_gate) {
            StopLocked();
        }
    }

    private void StartLocked(int generation) {
        var tracker = _trackerFactory();
        var cts = new CancellationTokenSource();
        tracker.ListReceived += OnListReceived;
        tracker.Failed += OnFailed;

        _tracker = tracker;
        _cts = cts;
        _runningGeneration = generation;
        _runTask = Task.Run(() => tracker.RunAsync(generation, cts.Token));
        Log.Information("DeviceTracker started for server generation {Generation}", generation);
    }

    private void StopLocked() {
        var cts = _cts;
        var tracker = _tracker;
        var task = _runTask;
        _cts = null;
        _tracker = null;
        _runTask = null;
        _runningGeneration = null;

        if (tracker is not null) {
            tracker.ListReceived -= OnListReceived;
            tracker.Failed -= OnFailed;
        }

        if (cts is not null) {
            try {
                cts.Cancel();
            }
            finally {
                cts.Dispose();
            }
        }

        if (task is not null && !task.IsCompleted) {
            try {
                task.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException) {
                // Expected on cancel.
            }
        }
    }

    private void OnListReceived(DeviceListReceived list) =>
        _post(new DeviceListReceivedEvent(list.Devices, list.At));

    private void OnFailed(DeviceTrackerFailed failed) =>
        _post(new DeviceTrackerFailedEvent(failed.Reason));

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        Stop();
    }
}
