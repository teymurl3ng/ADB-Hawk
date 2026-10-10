namespace AdbView.NativeUi;

using System;
using System.Windows.Threading;
using AdbView.Host;

/// <summary>
/// ITickSource on the WPF dispatcher — the reveal's PRODUCTION clock (decision 0025; the
/// fallback role from decision 0023, inverted after measurement). A DispatcherTimer wakes the
/// message loop itself, so it ticks reliably (~16 ms) even when WPF has nothing to render —
/// which is exactly the slide's case: a position-only SetWindowPos never dirties the visual
/// tree, so the composition hook's ticks rode on incidental renders (measured 2026-10-10:
/// 4-10 frames per flight, worst tick gap 16-47 ms). Priority is Render, not Send: a
/// Send-priority tick outranks the render pass and let geometry changes preempt the frame
/// that would have presented the previous one (decision 0023's original finding). The timer is
/// not time-accurate, but the animator derives progress from the wall clock and skips frames
/// identical to the last applied one, so a late tick still applies the correct frame.
/// </summary>
internal sealed class DispatcherTimerTickSource : ITickSource {
    private readonly DispatcherTimer _timer;
    private Action? _tick;

    public DispatcherTimerTickSource() {
        // Decision 0025: 10 ms — below the default 15.6 ms system timer tick with margin, so
        // every tick fires the timer and the cadence IS the tick (~15.6 ms). Measured on
        // 2026-10-10: a 16 ms interval slips past each tick boundary and locks into a 2-tick
        // (31 ms) period (consistent 31 ms worst gap, 3-7 frames per flight); 15 ms still
        // slips 0.6 ms per tick and eats an occasional double period (worst gap 16-32 ms).
        // The dispatcher's wait is tick-grid quantized, so the interval only needs to be
        // under the tick; the animator's wall-clock progress and identical-frame dedup make
        // any extra tick a no-op.
        _timer = new DispatcherTimer(DispatcherPriority.Render) {
            Interval = TimeSpan.FromMilliseconds(10),
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
