namespace AdbView.Host;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;

/// <summary>
/// Visibility-aware metadata orchestration. Posts generation-qualified results to StateEngine.
/// No AppSnapshot ownership, no shell protocol, no WinForms/WebView types.
/// </summary>
internal sealed class DeviceMetadataLifecycle : IAsyncDisposable, IDisposable {
    public static readonly TimeSpan FlyoutStaleBatteryAge = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PinnedBatteryInterval = TimeSpan.FromSeconds(120);

    private enum PendingKind {
        None,
        Full,
        BatteryOnly,
    }

    private sealed class DeviceWork {
        public CancellationTokenSource? Cts;
        public bool InFlight;
        public PendingKind Pending;
        public CancellationTokenSource? PinnedTimerCts;
        public long Generation;
        public string AdbSerial = "";
        public DeviceStateKind State;
        public int? BatteryPercent;
        public DateTimeOffset? BatteryObservedAt;
        public bool InitialScheduled;
    }

    private readonly Func<long, MetadataReceivedEvent, bool> _postMetadata;
    private readonly IMetadataProbe _probe;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceWork> _devices = new(StringComparer.Ordinal);
    private readonly List<(string Serial, string Service)> _requests = new();
    private bool _visible;
    private bool _pinned;
    private bool _suspended;
    private int _disposed;

    /// <summary>Test hook: recorded (AdbSerial, shell service) pairs for idle-cost proofs.</summary>
    internal IReadOnlyList<(string Serial, string Service)> RecordedRequests {
        get {
            lock (_gate) {
                return _requests.ToArray();
            }
        }
    }

    internal int RecordedRequestCount {
        get {
            lock (_gate) {
                return _requests.Count;
            }
        }
    }

    public DeviceMetadataLifecycle(
        Func<long, MetadataReceivedEvent, bool> postMetadata,
        IMetadataProbe? probe = null,
        Func<DateTimeOffset>? getUtcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) {
        _postMetadata = postMetadata ?? throw new ArgumentNullException(nameof(postMetadata));
        _probe = probe ?? new MetadataProbe(new MetadataDeviceShellClient());
        _getUtcNow = getUtcNow ?? (() => App.SystemClock.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public void OnTargetsChanged(ImmutableArray<MetadataProbeTargetView> targets) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        lock (_gate) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (!targets.IsDefaultOrEmpty) {
                foreach (MetadataProbeTargetView target in targets) {
                    seen.Add(target.DeviceId);
                    ApplyTargetLocked(target);
                }
            }

            foreach (string id in _devices.Keys.ToArray()) {
                if (!seen.Contains(id)) {
                    CancelDeviceLocked(id, remove: true);
                }
            }
        }
    }

    public void SetPresentationState(bool visible, bool pinned) {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        lock (_gate) {
            bool wasVisible = _visible;
            bool wasPinned = _pinned;
            _visible = visible;
            _pinned = pinned;

            if (_suspended) {
                return;
            }

            bool becameVisible = visible && !wasVisible;
            bool pinOrVisibilityChanged = becameVisible
                || (visible && pinned && !wasPinned)
                || (!pinned && wasPinned)
                || (!visible && wasVisible);

            if (!pinOrVisibilityChanged && visible == wasVisible && pinned == wasPinned) {
                return;
            }

            if (!visible || !pinned) {
                CancelAllPinnedTimersLocked();
            }

            if (becameVisible || (visible && pinned && !wasPinned)) {
                foreach (var pair in _devices) {
                    MaybeBatteryOnVisibilityLocked(pair.Key, pair.Value);
                }
            }

            if (visible && pinned) {
                foreach (var pair in _devices) {
                    ArmPinnedTimerLocked(pair.Key, pair.Value);
                }
            }
        }
    }

    public void OnSuspend() {
        lock (_gate) {
            _suspended = true;
            CancelAllPinnedTimersLocked();
            foreach (string id in _devices.Keys.ToArray()) {
                CancelInFlightLocked(id);
            }
        }
    }

    public void OnResume() {
        if (Volatile.Read(ref _disposed) != 0) {
            return;
        }

        lock (_gate) {
            _suspended = false;
            if (!_visible) {
                return;
            }

            foreach (var pair in _devices) {
                if (pair.Value.State == DeviceStateKind.Connected) {
                    RequestBatteryLocked(pair.Key, pair.Value, force: true);
                }
            }

            if (_pinned) {
                foreach (var pair in _devices) {
                    ArmPinnedTimerLocked(pair.Key, pair.Value);
                }
            }
        }
    }

    private void ApplyTargetLocked(MetadataProbeTargetView target) {
        if (!_devices.TryGetValue(target.DeviceId, out DeviceWork? work)) {
            work = new DeviceWork();
            _devices[target.DeviceId] = work;
        }

        bool generationChanged = work.Generation != 0 && work.Generation != target.Generation;
        bool serialChanged = work.Generation != 0
            && !string.Equals(work.AdbSerial, target.AdbSerial, StringComparison.Ordinal);

        if (generationChanged || serialChanged) {
            CancelInFlightLocked(target.DeviceId);
            CancelPinnedTimerLocked(work);
            work.Pending = PendingKind.None;
            work.InitialScheduled = false;
        }

        work.Generation = target.Generation;
        work.AdbSerial = target.AdbSerial;
        work.State = target.State;
        work.BatteryPercent = target.BatteryPercent;
        work.BatteryObservedAt = target.BatteryObservedAt;

        if (work.State != DeviceStateKind.Connected) {
            CancelInFlightLocked(target.DeviceId);
            CancelPinnedTimerLocked(work);
            work.Pending = PendingKind.None;
            work.InitialScheduled = false;
            return;
        }

        if (!work.InitialScheduled) {
            work.InitialScheduled = true;
            RequestFullLocked(target.DeviceId, work);
        }

        if (_visible && !_suspended) {
            MaybeBatteryOnVisibilityLocked(target.DeviceId, work);
        }

        if (_visible && _pinned && !_suspended) {
            ArmPinnedTimerLocked(target.DeviceId, work);
        }
        else {
            CancelPinnedTimerLocked(work);
        }
    }

    private void MaybeBatteryOnVisibilityLocked(string deviceId, DeviceWork work) {
        if (!_visible || _suspended || work.State != DeviceStateKind.Connected) {
            return;
        }

        // Repeated visibility while already handled: only on transition callers invoke this.
        if (work.BatteryPercent is null || work.BatteryObservedAt is null) {
            RequestBatteryLocked(deviceId, work, force: false);
            return;
        }

        DateTimeOffset now = _getUtcNow();
        if (now - work.BatteryObservedAt.Value > FlyoutStaleBatteryAge) {
            RequestBatteryLocked(deviceId, work, force: false);
        }
    }

    private void RequestFullLocked(string deviceId, DeviceWork work) {
        if (work.InFlight) {
            work.Pending = PendingKind.Full;
            return;
        }

        StartWorkLocked(deviceId, work, PendingKind.Full);
    }

    private void RequestBatteryLocked(string deviceId, DeviceWork work, bool force) {
        if (work.State != DeviceStateKind.Connected) {
            return;
        }

        if (work.InFlight) {
            if (work.Pending != PendingKind.Full) {
                work.Pending = PendingKind.BatteryOnly;
            }

            return;
        }

        if (!force
            && work.BatteryPercent is not null
            && work.BatteryObservedAt is not null
            && _getUtcNow() - work.BatteryObservedAt.Value <= FlyoutStaleBatteryAge) {
            return;
        }

        StartWorkLocked(deviceId, work, PendingKind.BatteryOnly);
    }

    private void StartWorkLocked(string deviceId, DeviceWork work, PendingKind kind) {
        CancelInFlightLocked(deviceId);
        var cts = new CancellationTokenSource();
        work.Cts = cts;
        work.InFlight = true;
        long generation = work.Generation;
        string serial = work.AdbSerial;
        PendingKind runKind = kind;

#pragma warning disable CA2008
        _ = Task.Run(() => RunProbeAsync(deviceId, generation, serial, runKind, cts.Token));
#pragma warning restore CA2008
    }

    private async Task RunProbeAsync(
        string deviceId,
        long generation,
        string adbSerial,
        PendingKind kind,
        CancellationToken ct) {
        try {
            var target = new MetadataProbeTarget(deviceId, adbSerial, generation);
            if (kind == PendingKind.Full) {
                RecordRequest(adbSerial, MetadataDeviceShellClient.ServiceName(MetadataCommand.Battery));
                RecordRequest(adbSerial, MetadataDeviceShellClient.ServiceName(MetadataCommand.ExactModel));
                RecordRequest(adbSerial, MetadataDeviceShellClient.ServiceName(MetadataCommand.AndroidVersion));
                MetadataProbeResult result = await _probe.ProbeAsync(target, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) {
                    return;
                }

                _postMetadata(
                    generation,
                    new MetadataReceivedEvent(
                        deviceId,
                        result.BatteryStatus == MetadataFieldStatus.Ok ? result.BatteryPercent : null,
                        result.ExactModelStatus == MetadataFieldStatus.Ok ? result.ExactModel : null,
                        result.AndroidVersionStatus == MetadataFieldStatus.Ok ? result.AndroidVersion : null));
            }
            else {
                RecordRequest(adbSerial, MetadataDeviceShellClient.ServiceName(MetadataCommand.Battery));
                MetadataCommandResult battery = await _probe
                    .ProbeCommandAsync(target, MetadataCommand.Battery, ct)
                    .ConfigureAwait(false);
                if (ct.IsCancellationRequested) {
                    return;
                }

                // Battery-only: Model/AndroidVersion null → reducer preserves cache; Battery null clears.
                int? batteryValue = battery.Status == MetadataFieldStatus.Ok ? battery.BatteryPercent : null;
                _postMetadata(
                    generation,
                    new MetadataReceivedEvent(deviceId, batteryValue, null, null));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // expected
        }
        finally {
            lock (_gate) {
                if (_devices.TryGetValue(deviceId, out DeviceWork? work)
                    && work.Generation == generation) {
                    work.InFlight = false;
                    work.Cts = null;
                    PendingKind pending = work.Pending;
                    work.Pending = PendingKind.None;
                    if (pending != PendingKind.None && work.State == DeviceStateKind.Connected && !_suspended) {
                        if (pending == PendingKind.Full) {
                            StartWorkLocked(deviceId, work, PendingKind.Full);
                        }
                        else if (work.BatteryPercent is null
                            || work.BatteryObservedAt is null
                            || _getUtcNow() - work.BatteryObservedAt.Value > FlyoutStaleBatteryAge) {
                            StartWorkLocked(deviceId, work, PendingKind.BatteryOnly);
                        }
                        else if (_visible && _pinned) {
                            ArmPinnedTimerLocked(deviceId, work);
                        }
                    }
                    else if (_visible && _pinned && !_suspended) {
                        ArmPinnedTimerLocked(deviceId, work);
                    }
                }
            }
        }
    }

    private void ArmPinnedTimerLocked(string deviceId, DeviceWork work) {
        CancelPinnedTimerLocked(work);
        if (!_visible || !_pinned || _suspended || work.State != DeviceStateKind.Connected) {
            return;
        }

        DateTimeOffset now = _getUtcNow();
        DateTimeOffset due = (work.BatteryObservedAt ?? now) + PinnedBatteryInterval;
        TimeSpan wait = due - now;
        if (wait < TimeSpan.Zero) {
            wait = TimeSpan.Zero;
        }

        var cts = new CancellationTokenSource();
        work.PinnedTimerCts = cts;
        long generation = work.Generation;
#pragma warning disable CA2008
        _ = Task.Run(async () => {
            try {
                await _delay(wait, cts.Token).ConfigureAwait(false);
                lock (_gate) {
                    if (Volatile.Read(ref _disposed) != 0 || _suspended || !_visible || !_pinned) {
                        return;
                    }

                    if (!_devices.TryGetValue(deviceId, out DeviceWork? current)
                        || current.Generation != generation
                        || current.State != DeviceStateKind.Connected) {
                        return;
                    }

                    RequestBatteryLocked(deviceId, current, force: true);
                }
            }
            catch (OperationCanceledException) {
                // cancelled
            }
        });
#pragma warning restore CA2008
    }

    private void RecordRequest(string serial, string service) {
        lock (_gate) {
            _requests.Add((serial, service));
        }
    }

    private void CancelAllPinnedTimersLocked() {
        foreach (DeviceWork work in _devices.Values) {
            CancelPinnedTimerLocked(work);
        }
    }

    private static void CancelPinnedTimerLocked(DeviceWork work) {
        var cts = work.PinnedTimerCts;
        work.PinnedTimerCts = null;
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

    private void CancelInFlightLocked(string deviceId) {
        if (!_devices.TryGetValue(deviceId, out DeviceWork? work)) {
            return;
        }

        var cts = work.Cts;
        work.Cts = null;
        work.InFlight = false;
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

    private void CancelDeviceLocked(string deviceId, bool remove) {
        CancelInFlightLocked(deviceId);
        if (_devices.TryGetValue(deviceId, out DeviceWork? work)) {
            CancelPinnedTimerLocked(work);
            if (remove) {
                _devices.Remove(deviceId);
            }
        }
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        lock (_gate) {
            foreach (string id in _devices.Keys.ToArray()) {
                CancelDeviceLocked(id, remove: true);
            }
        }
    }

    public ValueTask DisposeAsync() {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
