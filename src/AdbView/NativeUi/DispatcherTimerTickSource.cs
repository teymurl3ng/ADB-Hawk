namespace AdbView.NativeUi;

using System;
using System.Windows.Threading;
using AdbView.Host;

/// <summary>
/// ITickSource on the WPF dispatcher — the FALLBACK clock (decision 0023). Production uses
/// <see cref="CompositionTickSource"/>; this stays for environments where the composition hook
/// must not run. Priority is Render, not Send: a Send-priority tick outranks the render pass and
/// let geometry changes preempt the frame that would have presented the previous one.
/// </summary>
internal sealed class DispatcherTimerTickSource : ITickSource {
    private readonly DispatcherTimer _timer;
    private Action? _tick;

    public DispatcherTimerTickSource() {
        _timer = new DispatcherTimer(DispatcherPriority.Render) {
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
