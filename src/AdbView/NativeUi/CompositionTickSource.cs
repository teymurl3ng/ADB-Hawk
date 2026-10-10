namespace AdbView.NativeUi;

using System;
using System.Windows.Media;
using AdbView.Host;

/// <summary>
/// ITickSource on the WPF composition clock (decision 0023). <see cref="CompositionTarget.Rendering"/>
/// fires once per composed frame — but only when WPF actually renders, and a position-only window
/// move never dirties the visual tree, so during a pure slide the ticks ride on incidental renders
/// (metadata updates, the age ticker, first-paint after Show). Measured 2026-10-10: the same
/// flights took 4-10 frames with a 16-47 ms worst tick gap depending on background activity
/// (decision 0025). Production therefore drives the reveal from
/// <see cref="DispatcherTimerTickSource"/>; this source stays as the alternative for environments
/// where a dispatcher timer must not run.
///
/// <para>Attached on Start, detached on Stop: the WPF performance guidance is to detach the
/// Rendering handler "at every opportunity", so the hook exists only while a reveal is in flight.
/// The animator derives progress from the wall clock and skips a frame identical to the last
/// applied one, so an extra callback per composed frame is idempotent. Content stays on the WPF
/// timing system's storyboards.</para>
/// </summary>
internal sealed class CompositionTickSource : ITickSource {
    private Action? _tick;

    public void Start(Action tick) {
        _tick = tick;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop() {
        CompositionTarget.Rendering -= OnRendering;
        _tick = null;
    }

    private void OnRendering(object? sender, EventArgs e) => _tick?.Invoke();

    public void Dispose() => Stop();
}
