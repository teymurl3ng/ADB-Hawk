namespace WinUiShellProbe;

using System;
using Microsoft.UI.Dispatching;

// Prototype-only proof of a bounded, latest-revision visual mailbox. Domain events
// and actions never pass through it. A lock couples pending state and drain ownership.
internal sealed class ProbeSnapshotMailbox(DispatcherQueue dispatcher, Action<long, int> render) {
    private readonly object _gate = new();
    private (long Revision, int Count)? _pending;
    private long _highestAccepted = -1;
    private bool _scheduled;

    public int Published { get; private set; }
    public int Rendered { get; private set; }

    public void Publish(long revision, int count) {
        lock (_gate) {
            if (revision <= _highestAccepted) {
                return;
            }
            _highestAccepted = revision;
            _pending = (revision, count);
            Published++;
            if (!_scheduled) {
                _scheduled = true;
                Schedule();
            }
        }
    }

    private void Schedule() {
        if (!dispatcher.TryEnqueue(Drain)) {
            _scheduled = false;
            _pending = null;
            throw new InvalidOperationException("The probe visual mailbox dispatcher stopped before delivery.");
        }
    }

    private void Drain() {
        (long Revision, int Count)? next;
        lock (_gate) {
            next = _pending;
            _pending = null;
        }
        if (next is { } snapshot) {
            render(snapshot.Revision, snapshot.Count);
            Rendered++;
        }
        lock (_gate) {
            if (_pending is not null) {
                Schedule();
            }
            else {
                _scheduled = false;
            }
        }
    }
}
