namespace AdbView.Notifications;

using System;
using System.Collections.Generic;

/// <summary>Delivery seam. 8A: fake/null only. 8B: BalloonNotificationSink.</summary>
internal interface INotificationSink {
    void Emit(NotificationIntent intent);
}

internal sealed class NullNotificationSink : INotificationSink {
    public static NullNotificationSink Instance { get; } = new();

    public void Emit(NotificationIntent intent) {
        // intentionally empty
    }
}

internal sealed class FakeNotificationSink : INotificationSink {
    private readonly List<NotificationIntent> _emitted = new();
    private readonly object _gate = new();

    public Action? OnEmit { get; set; }

    public Exception? ThrowOnEmit { get; set; }

    public IReadOnlyList<NotificationIntent> Emitted {
        get {
            lock (_gate) {
                return _emitted.ToArray();
            }
        }
    }

    public void Emit(NotificationIntent intent) {
        ArgumentNullException.ThrowIfNull(intent);
        if (ThrowOnEmit is not null) {
            throw ThrowOnEmit;
        }

        lock (_gate) {
            _emitted.Add(intent);
        }

        OnEmit?.Invoke();
    }

    public void Clear() {
        lock (_gate) {
            _emitted.Clear();
        }
    }
}
