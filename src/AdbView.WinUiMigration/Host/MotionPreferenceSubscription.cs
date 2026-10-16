namespace AdbView.WinUiMigration.Host;

using Microsoft.UI.Dispatching;
using Serilog;
using Windows.UI.ViewManagement;

internal interface IMotionPreferenceSource {
    bool Enabled { get; }
    event Action? Changed;
}

internal sealed class SystemMotionPreferenceSource : IMotionPreferenceSource, IDisposable {
    private readonly UISettings _settings = new();
    public bool Enabled => _settings.AnimationsEnabled;
    public event Action? Changed;
    public SystemMotionPreferenceSource() { _settings.AnimationsEnabledChanged += OnChanged; }
    private void OnChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => Changed?.Invoke();
    public void Dispose() { _settings.AnimationsEnabledChanged -= OnChanged; }
}

/// <summary>Event may arrive off-thread. Read latest on delivery, not the potentially stale event value.</summary>
internal sealed class MotionPreferenceSubscription : IDisposable {
    private readonly IMotionPreferenceSource _source;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<bool> _apply;
    private int _disposed;
    public MotionPreferenceSubscription(IMotionPreferenceSource source, DispatcherQueue dispatcher, Action<bool> apply) {
        _source = source; _dispatcher = dispatcher; _apply = apply;
        _source.Changed += OnChanged;
        _apply(_source.Enabled);
    }
    private void OnChanged() {
        if (Volatile.Read(ref _disposed) != 0) { return; }
        if (!_dispatcher.TryEnqueue(() => {
            if (Volatile.Read(ref _disposed) == 0) { _apply(_source.Enabled); }
        })) { Log.Warning("Motion preference: dispatcher rejected delivery during shutdown"); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _source.Changed -= OnChanged; } }
}
