namespace AdbView.NativeUi;

using System;
using System.Windows.Media;
using AdbView.Host;

/// <summary>
/// Decision 0023: ITickSource on the WPF composition clock. <see cref="CompositionTarget.Rendering"/>
/// fires once per composed frame, so bounds frames align with what DWM composes instead of racing
/// it from a timer queue — the research note (2026-10-07) records that a DispatcherTimer is
/// explicitly not time-accurate and that a Send-priority tick outranks Render, which let geometry
/// changes preempt the render pass that would present the previous frame.
///
/// <para>Attached on Start, detached on Stop: the WPF performance guidance is to detach the
/// Rendering handler "at every opportunity", so the hook exists only while a reveal is in flight.
/// The animator derives progress from the wall clock and skips a frame identical to the last
/// applied one, so an extra callback per composed frame is idempotent. This is the one sanctioned
/// window-bounds clock (ENGINEERING §10.4, decision 0023); content stays on the WPF timing
/// system's storyboards.</para>
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
