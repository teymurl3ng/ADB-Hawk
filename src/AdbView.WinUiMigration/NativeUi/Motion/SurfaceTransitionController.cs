namespace AdbView.WinUiMigration.NativeUi.Motion;

internal enum SurfaceState { Hidden, Opening, Open, Closing, Disposed }

/// <summary>M02 fallback B: immediate native visibility, no surface animation or completion clock.</summary>
internal sealed class SurfaceTransitionController(Action show, Action hide, Action<bool> setInteractive) : IDisposable {
    public SurfaceState State { get; private set; } = SurfaceState.Hidden;
    public long Generation { get; private set; }
    public bool Suspended { get; private set; }
    public void Open() {
        if (State is SurfaceState.Disposed or SurfaceState.Open || Suspended) { return; }
        Generation++; State = SurfaceState.Opening;
        setInteractive(true); show(); State = SurfaceState.Open;
    }
    public void Close() {
        if (State is SurfaceState.Disposed or SurfaceState.Hidden) { return; }
        Generation++; State = SurfaceState.Closing;
        setInteractive(false); hide(); State = SurfaceState.Hidden;
    }
    public void Suspend() { Close(); Suspended = true; }
    public void Resume() { if (State != SurfaceState.Disposed) { Suspended = false; } }
    public void Dispose() {
        if (State == SurfaceState.Disposed) { return; }
        Close(); Generation++; State = SurfaceState.Disposed;
    }
}
