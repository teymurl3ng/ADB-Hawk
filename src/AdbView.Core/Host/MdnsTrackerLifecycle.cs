namespace AdbView.Host;

using System;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Serilog;

/// <summary>
/// Owns MdnsTracker session lifetime against supervisor generation.
/// MdnsTracker remains unaware of StateEngine; this class only posts events.
/// </summary>
internal sealed class MdnsTrackerLifecycle : IDisposable {
    private readonly Func<MdnsTracker> _trackerFactory;
    private readonly Action<AdbViewEvent> _post;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private MdnsTracker? _tracker;
    private int? _runningGeneration;
    private int _disposed;
    private long _sequence;

    public MdnsTrackerLifecycle(Action<AdbViewEvent> post, Func<MdnsTracker>? trackerFactory = null) {
        ArgumentNullException.ThrowIfNull(post);
        _post = post;
        _trackerFactory = trackerFactory ?? (() => new MdnsTracker(
            onDegraded: reason => Log.Warning("MdnsTracker degraded: {Reason}", reason),
            onMalformedPayload: reason => Log.Information("MdnsTracker malformed payload: {Reason}", reason)));
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
        tracker.ServicesReceived += OnServicesReceived;
        tracker.Failed += OnFailed;

        _tracker = tracker;
        _cts = cts;
        _runningGeneration = generation;
        var token = cts.Token;
        _runTask = Task.Run(() => tracker.RunAsync(generation, token));
        Log.Information("MdnsTracker started for server generation {Generation}", generation);
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
            tracker.ServicesReceived -= OnServicesReceived;
            tracker.Failed -= OnFailed;
            if (Volatile.Read(ref _disposed) == 0) {
                _post(new MdnsTrackerFailedEvent("Discovery tracking stopped."));
            }
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

    private void OnServicesReceived(MdnsServicesReceived list) =>
        _post(new MdnsServicesReceivedEvent(list.Services, list.At, Interlocked.Increment(ref _sequence)));

    private void OnFailed(MdnsTrackerFailed failed) =>
        _post(new MdnsTrackerFailedEvent(failed.Reason));

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        Stop();
    }
}
