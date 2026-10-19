namespace AdbView.WinUiMigration.Host;

using AdbView.Notifications;
using Serilog;

// Notification intents are lossless and separate from the visual mailbox.
internal sealed class DispatchedNotificationSink(Func<Action, bool> enqueue, Action<NotificationIntent> deliver,
    Action<Exception> fatal) : INotificationSink, IDisposable {
    private int _disposed;
    public void Emit(NotificationIntent intent) {
        if (Volatile.Read(ref _disposed) != 0) { return; }
        if (!enqueue(() => { if (Volatile.Read(ref _disposed) == 0) { deliver(intent); } })) {
            if (Volatile.Read(ref _disposed) != 0) { return; }
            var exception = new InvalidOperationException("Candidate notification dispatcher rejected delivery.");
            Log.Fatal(exception, "Candidate notification delivery failed"); fatal(exception);
        }
    }
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
