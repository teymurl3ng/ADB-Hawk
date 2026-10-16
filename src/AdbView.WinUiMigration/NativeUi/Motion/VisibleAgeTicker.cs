namespace AdbView.WinUiMigration.NativeUi.Motion;

using Microsoft.UI.Dispatching;

/// <summary>The existing sanctioned 1 s formatting tick. Never refreshes state or backend data.</summary>
internal sealed class VisibleAgeTicker : IDisposable {
    private readonly DispatcherQueueTimer _timer;
    private readonly Action _tick;
    private bool _disposed;
    public bool IsRunning => _timer.IsRunning;
    public int TickCount { get; private set; }
    public VisibleAgeTicker(DispatcherQueue dispatcher, Action tick) {
        _tick = tick; _timer = dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTick;
    }
    public void SetNeeded(bool needed) {
        if (_disposed) { return; }
        if (needed && !_timer.IsRunning) { _tick(); _timer.Start(); }
        else if (!needed) { _timer.Stop(); }
    }
    private void OnTick(DispatcherQueueTimer sender, object args) {
        if (_disposed || !_timer.IsRunning) { return; }
        TickCount++; _tick();
    }
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true; _timer.Stop(); _timer.Tick -= OnTick;
    }
}
