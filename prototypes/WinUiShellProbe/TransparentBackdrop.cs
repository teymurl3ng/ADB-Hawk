namespace WinUiShellProbe;

using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Windows.UI;

internal sealed class TransparentBackdrop : IDisposable {
    private readonly ICompositionSupportsSystemBackdrop _target;
    private readonly Windows.UI.Composition.Compositor _compositor;
    private readonly Windows.UI.Composition.CompositionColorBrush _brush;
    private bool _disposed;

    public TransparentBackdrop(Window window) {
        ArgumentNullException.ThrowIfNull(window);
        ProbeDiagnostics.Stage($"System dispatcher initially present: {Windows.System.DispatcherQueue.GetForCurrentThread() is not null}");
        window.DispatcherQueue.EnsureSystemDispatcherQueue();
        ProbeDiagnostics.Stage("System dispatcher ensured");
        _target = WinRT.CastExtensions.As<ICompositionSupportsSystemBackdrop>(window);
        ProbeDiagnostics.Stage("Native backdrop target acquired");
        _compositor = new Windows.UI.Composition.Compositor();
        ProbeDiagnostics.Stage("System compositor created");
        _brush = _compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        _target.SystemBackdrop = _brush;
    }

    public void Dispose() {
        if (!_disposed) {
            _disposed = true;
            _target.SystemBackdrop = null;
            _brush.Dispose();
            _compositor.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
