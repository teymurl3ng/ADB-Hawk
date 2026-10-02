namespace AdbView.NativeUi;

using System;
using System.Windows.Threading;
using AdbView.Host;

/// <summary>ITickSource on the WPF dispatcher (decision 0013/0015). 16 ms UI tick.</summary>
internal sealed class DispatcherTimerTickSource : ITickSource {
    private readonly DispatcherTimer _timer;
    private Action? _tick;

    public DispatcherTimerTickSource() {
        _timer = new DispatcherTimer(DispatcherPriority.Send) {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += (_, _) => _tick?.Invoke();
    }

    public void Start(Action tick) {
        _tick = tick;
        _timer.Start();
    }

    public void Stop() {
        _timer.Stop();
        _tick = null;
    }

    public void Dispose() => _timer.Stop();
}
