namespace AdbView.Notifications;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.State;

/// <summary>
/// Domain timeline owner: accepts authoritative snapshots, schedules holds with an
/// injected clock/delay, re-checks current snapshot at due time, emits intents.
/// No Windows / WinForms / ADB protocol references.
/// </summary>
internal sealed class NotificationCoordinator : IAsyncDisposable, IDisposable {
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly INotificationSink _sink;
    private readonly object _gate = new();

    private NotifierState _state;
    private AppSnapshot? _previous;
    private AppSnapshot? _current;
    private readonly Dictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private CancellationTokenSource _lifetime = new();
    private bool _disposed;

    private sealed class PendingEntry {
        public required PendingHold Hold { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    public NotificationCoordinator(
        Func<DateTimeOffset> clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        INotificationSink sink) {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _state = NotifierState.Create(_clock());
    }

    public NotifierState State {
        get {
            lock (_gate) {
                return _state;
            }
        }
    }

    public void AcceptSnapshot(AppSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        List<string> toCancel;
        List<PendingHold> toArm;
        List<NotificationIntent> immediate;
        bool absorbOnly;

        lock (_gate) {
            if (_disposed || _state.ShuttingDown) {
                return;
            }

            DateTimeOffset now = _clock();
            AppSnapshot? previous = _current;
            _previous = previous;
            _current = snapshot;

            NotificationDecision decision = NotificationPolicy.Evaluate(previous, snapshot, _state, now);
            _state = decision.NextState with { BaselineEstablished = true };

            absorbOnly = previous is null || now < _state.SuppressUntil || _state.Suspended;
            if (absorbOnly) {
                CancelAllPendingLocked();
                return;
            }

            toCancel = decision.CancelKeys.Select(k => k.Composite).ToList();
            toArm = decision.ArmHolds.ToList();
            immediate = decision.ImmediateIntents.ToList();
        }

        foreach (string composite in toCancel) {
            CancelPending(composite);
        }

        foreach (PendingHold hold in toArm) {
            ArmHold(hold);
        }

        if (immediate.Count > 0) {
            EmitBatch(immediate);
        }
    }

    public void RebaselineForResume(AppSnapshot current) {
        ArgumentNullException.ThrowIfNull(current);
        DateTimeOffset now = _clock();
        lock (_gate) {
            if (_disposed || _state.ShuttingDown) {
                return;
            }

            CancelAllPendingLocked();
            _state = NotificationPolicy.ForResume(_state, now);
            _current = current;
            _previous = current;
        }
    }

    public void OnSuspend() {
        lock (_gate) {
            if (_disposed || _state.ShuttingDown) {
                return;
            }

            CancelAllPendingLocked();
            _state = NotificationPolicy.ForSuspend(_state);
        }
    }

    public void BeginShutdown() {
        lock (_gate) {
            if (_disposed) {
                return;
            }

            CancelAllPendingLocked();
            _state = NotificationPolicy.ForShutdown(_state);
            try {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException) {
                // already disposed
            }
        }
    }

    private void ArmHold(PendingHold hold) {
        CancellationTokenSource cts;
        lock (_gate) {
            if (_disposed || _state.ShuttingDown || _state.Suspended) {
                return;
            }

            if (_pending.TryGetValue(hold.Key.Composite, out PendingEntry? existing)) {
                existing.Cts.Cancel();
                existing.Cts.Dispose();
                _pending.Remove(hold.Key.Composite);
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _pending[hold.Key.Composite] = new PendingEntry { Hold = hold, Cts = cts };
            _ = RunHoldAsync(hold, cts.Token);
        }
    }

    private async Task RunHoldAsync(PendingHold hold, CancellationToken ct) {
        try {
            DateTimeOffset now = _clock();
            TimeSpan delay = hold.DueAt - now;
            if (delay > TimeSpan.Zero) {
                await _delay(delay, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            OnHoldDue(hold);
        }
        catch (OperationCanceledException) {
            // superseded or shutdown
        }
#pragma warning disable CA1031 // Hold timer boundary: log and continue; never crash the host.
        catch (Exception ex) {
            Serilog.Log.Warning(ex, "NotificationCoordinator hold failed {Kind}", hold.Key.Kind);
        }
#pragma warning restore CA1031
        finally {
            lock (_gate) {
                if (_pending.TryGetValue(hold.Key.Composite, out PendingEntry? entry)
                    && entry.Hold.DetectedAt == hold.DetectedAt) {
                    _pending.Remove(hold.Key.Composite);
                }
            }
        }
    }

    private void OnHoldDue(PendingHold hold) {
        List<PendingHold> dueHolds;
        lock (_gate) {
            if (_disposed || _state.ShuttingDown || _state.Suspended) {
                return;
            }

            DateTimeOffset now = _clock();
            if (now < _state.SuppressUntil || _current is null) {
                return;
            }

            dueHolds = _pending.Values
                .Select(e => e.Hold)
                .Where(h => h.DueAt <= now)
                .ToList();

            // Another wake may have already drained; do not re-materialize this hold alone.
            if (dueHolds.Count == 0) {
                return;
            }

            foreach (PendingHold due in dueHolds) {
                CancelPendingLocked(due.Key.Composite);
            }
        }

        dueHolds = dueHolds
            .GroupBy(h => h.Key.Composite)
            .Select(g => g.First())
            .OrderBy(h => h.Key.Kind)
            .ThenBy(h => h.Key.Subject, StringComparer.Ordinal)
            .ToList();

        var batch = new List<NotificationIntent>();
        foreach (PendingHold due in dueHolds) {
            NotificationIntent? intent;
            lock (_gate) {
                DateTimeOffset now = _clock();
                AppSnapshot current = _current!;
                intent = MaterializeLocked(due, current, now);
            }

            if (intent is not null) {
                batch.Add(intent);
            }
        }

        if (batch.Count > 0) {
            EmitBatch(batch);
        }
    }

    private NotificationIntent? MaterializeLocked(PendingHold hold, AppSnapshot current, DateTimeOffset now) {
        if (hold.Key.Kind == NotificationKind.DeviceDisconnected) {
            if (current.Devices.Any(d => d.Id == hold.Key.Subject)) {
                return null;
            }

            if (NotificationPolicy.IsDedupBlocked(hold.Key, _state, now)) {
                return null;
            }

            return NotificationPolicy.BuildDisconnectedIntent(
                hold.Key,
                hold.DisplayNameHint ?? hold.Key.Subject);
        }

        return NotificationPolicy.TryMaterializeHold(hold, current, _state, now);
    }

    private void EmitBatch(IReadOnlyList<NotificationIntent> candidates) {
        System.Collections.Immutable.ImmutableArray<NotificationIntent> toEmit;
        lock (_gate) {
            DateTimeOffset now = _clock();
            (toEmit, _state) = NotificationPolicy.ApplyBurstAndRecord(
                System.Collections.Immutable.ImmutableArray.CreateRange(candidates),
                _state,
                now);
        }

        foreach (NotificationIntent intent in toEmit) {
            try {
                Serilog.Log.Information(
                    "NotificationCoordinator emitting {Kind} subject-len={SubjectLen} title={Title}",
                    intent.Key.Kind,
                    intent.Key.Subject.Length,
                    intent.Title);
                _sink.Emit(intent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException) {
                Serilog.Log.Warning(ex, "Notification sink failed for {Kind} {Subject}", intent.Key.Kind, intent.Key.Subject);
            }
        }
    }

    private void CancelPending(string composite) {
        lock (_gate) {
            CancelPendingLocked(composite);
        }
    }

    private void CancelPendingLocked(string composite) {
        if (_pending.TryGetValue(composite, out PendingEntry? entry)) {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
            _pending.Remove(composite);
        }
    }

    private void CancelAllPendingLocked() {
        foreach (PendingEntry entry in _pending.Values.ToArray()) {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
        }

        _pending.Clear();
    }

    public void Dispose() {
        BeginShutdown();
        lock (_gate) {
            if (_disposed) {
                return;
            }

            _disposed = true;
            _lifetime.Dispose();
        }
    }

    public ValueTask DisposeAsync() {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
