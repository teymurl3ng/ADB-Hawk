namespace WinUiShellProbe;

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

internal sealed class SurfaceMotionProbe : IDisposable {
    private readonly UIElement _element;
    private readonly Vector3 _travel;
    private readonly Dictionary<CompositionScopedBatch, TypedEventHandler<object, CompositionBatchCompletedEventArgs>> _batches = [];
    private ScalarKeyFrameAnimation? _opacity;
    private Vector3KeyFrameAnimation? _translation;
    private long _generation;
    private bool? _shown;
    private bool _disposed;

    public SurfaceMotionProbe(UIElement element, bool horizontal = false, bool reverse = false) {
        _element = element;
        _travel = horizontal ? new Vector3(reverse ? -12 : 12, 0, 0) : new Vector3(0, 12, 0);
    }

    public long Generation => _generation;

    public long CompletedGeneration { get; private set; }

    public bool IsActive { get; private set; }

    public void SetShown(bool shown) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_shown == shown) {
            return;
        }

        _shown = shown;
        long generation = ++_generation;
        IsActive = true;
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        using var easing = compositor.CreateCubicBezierEasingFunction(
            shown ? Vector2.Zero : new Vector2(1, 0), shown ? new Vector2(0, 1) : Vector2.One);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.InsertExpressionKeyFrame(0, "this.StartingValue");
        opacity.InsertKeyFrame(1, shown ? 1 : 0, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(167);
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Target = "Translation";
        offset.InsertExpressionKeyFrame(0, "this.StartingValue");
        offset.InsertKeyFrame(1, shown ? Vector3.Zero : _travel, easing);
        offset.Duration = opacity.Duration;
        _element.StartAnimation(opacity);
        _element.StartAnimation(offset);
        _opacity?.Dispose();
        _translation?.Dispose();
        _opacity = opacity;
        _translation = offset;
        TypedEventHandler<object, CompositionBatchCompletedEventArgs> completed = (sender, args) => {
            if (!_disposed && generation == _generation) {
                CompletedGeneration = generation;
                IsActive = false;
            }
            if (_batches.Remove(batch, out var handler)) {
                batch.Completed -= handler;
                batch.Dispose();
            }
        };
        _batches.Add(batch, completed);
        batch.Completed += completed;
        batch.End();
    }

    public void Settle(bool shown) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _generation++;
        _shown = shown;
        if (_opacity is not null) {
            _element.StopAnimation(_opacity);
            _opacity.Dispose();
            _opacity = null;
        }
        if (_translation is not null) {
            _element.StopAnimation(_translation);
            _translation.Dispose();
            _translation = null;
        }
        foreach (var entry in _batches) {
            entry.Key.Completed -= entry.Value;
            entry.Key.Dispose();
        }
        _batches.Clear();
        _element.Opacity = shown ? 1 : 0;
        _element.Translation = shown ? Vector3.Zero : _travel;
        IsActive = false;
    }

    public void Dispose() {
        if (!_disposed) {
            Settle(false);
            _disposed = true;
        }
    }
}
