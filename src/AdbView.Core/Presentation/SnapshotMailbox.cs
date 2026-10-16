namespace AdbView.Presentation;

using AdbView.State;

// Visual delivery only. Domain events, notifications and commands never enter this slot.
internal sealed class SnapshotMailbox(Func<Action, bool> enqueue, Action<AppSnapshot> render) : IDisposable {
    private readonly object _gate = new();
    private AppSnapshot? _pending;
    private long _highestAccepted = -1;
    private bool _scheduled;
    private bool _disposed;

    public bool Publish(AppSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool schedule;
        lock (_gate) {
            if (_disposed || snapshot.Revision <= _highestAccepted) {
                return false;
            }
            _highestAccepted = snapshot.Revision;
            _pending = snapshot;
            schedule = !_scheduled;
            _scheduled = true;
        }
        if (schedule) {
            Schedule();
        }
        return true;
    }

    private void Schedule() {
        if (!enqueue(Drain)) {
            Dispose();
            throw new InvalidOperationException("Snapshot presentation dispatcher rejected delivery.");
        }
    }

    private void Drain() {
        AppSnapshot? next;
        lock (_gate) {
            if (_disposed) {
                return;
            }
            next = _pending;
            _pending = null;
        }
        // Render exceptions deliberately reach the application's fatal-failure handler.
        if (next is not null) {
            try { render(next); }
            catch { Dispose(); throw; }
        }
        bool again;
        lock (_gate) {
            again = !_disposed && _pending is not null;
            if (!again) {
                _scheduled = false;
            }
        }
        if (again) {
            Schedule();
        }
    }

    public void Dispose() {
        lock (_gate) {
            _disposed = true;
            _pending = null;
            _scheduled = false;
        }
    }
}
