namespace AdbView.WinUiMigration.NativeUi.Motion;

using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

/// <summary>Only this incoming container owns custom opacity/translation. No outgoing live tree.</summary>
internal sealed class ContentTransitionController : IDisposable {
    private readonly Grid _container;
    private readonly MotionPolicy _policy;
    private ScalarKeyFrameAnimation? _opacity;
    private Vector3KeyFrameAnimation? _translation;
    private CompositionScopedBatch? _batch;
    private TypedEventHandler<object, CompositionBatchCompletedEventArgs>? _completed;
    private RoutedEventHandler? _loaded;
    private bool _visible;
    private bool _disposed;
    private string? _target;
    public long Generation { get; private set; }
    public long CompletedGeneration { get; private set; }
    public bool IsActive { get; private set; }
    public int OwnedAnimationHandles => (_opacity is null ? 0 : 1) + (_translation is null ? 0 : 1);
    public int CompletionSubscriptions => _completed is null ? 0 : 1;

    public ContentTransitionController(Grid container, MotionPolicy policy) {
        _container = container; _policy = policy; _policy.Changed += OnPolicyChanged;
    }
    public void Navigate(string target, FrameworkElement incoming, bool reverse = false, bool animate = true) {
        if (_disposed || _target == target && _container.Children.Count == 1 && ReferenceEquals(_container.Children[0], incoming)) { return; }
        _target = target;
        long generation = ++Generation;
        RemoveLoaded();
        bool replacing = IsActive;
        _container.Children.Clear(); _container.Children.Add(incoming);
        if (!_visible || !animate || !_policy.Enabled) { Settle(); return; }
        if (!replacing) { _container.Opacity = 0; _container.Translation = new Vector3(reverse ? -MotionPolicy.ContentTravel : MotionPolicy.ContentTravel, 0, 0); }
        if (!_container.IsLoaded) {
            IsActive = true;
            _loaded = (_, _) => {
                RemoveLoaded();
                if (!_disposed && generation == Generation && _visible && _policy.Enabled) { Start(generation); }
            };
            _container.Loaded += _loaded;
        }
        else { Start(generation); }
    }
    private void Start(long generation) {
        MotionParameters recipe = _policy.Parameters(MotionRecipe.ContentNavigate);
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        DetachBatch();
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        using var easing = compositor.CreateCubicBezierEasingFunction(recipe.FirstControl, recipe.SecondControl);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity"; opacity.Duration = recipe.Duration;
        opacity.InsertExpressionKeyFrame(0, "this.StartingValue"); opacity.InsertKeyFrame(1, 1, easing);
        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.Target = "Translation"; translation.Duration = recipe.Duration;
        translation.InsertExpressionKeyFrame(0, "this.StartingValue"); translation.InsertKeyFrame(1, Vector3.Zero, easing);
        // Start replacements first. Stopping/reading base values would snap rather than retarget.
        _container.StartAnimation(opacity); _container.StartAnimation(translation);
        _opacity?.Dispose(); _translation?.Dispose();
        _opacity = opacity; _translation = translation; _batch = batch; IsActive = true;
        _completed = (_, _) => {
            if (_disposed || generation != Generation || !ReferenceEquals(batch, _batch)) { return; }
            CompletedGeneration = generation;
            Settle();
        };
        batch.Completed += _completed; batch.End();
    }
    public void SetVisible(bool visible) {
        if (_disposed || _visible == visible) { return; }
        _visible = visible;
        if (!visible) { Settle(); }
    }
    private void OnPolicyChanged() { if (!_policy.Enabled) { Settle(); } }
    public void Settle() {
        if (_disposed) { return; }
        Generation++; RemoveLoaded(); DetachBatch();
        if (_opacity is not null) { _container.StopAnimation(_opacity); _opacity.Dispose(); _opacity = null; }
        if (_translation is not null) { _container.StopAnimation(_translation); _translation.Dispose(); _translation = null; }
        _container.Opacity = 1; _container.Translation = Vector3.Zero; IsActive = false;
    }
    private void RemoveLoaded() {
        if (_loaded is not null) { _container.Loaded -= _loaded; _loaded = null; }
    }
    private void DetachBatch() {
        if (_batch is null) { return; }
        if (_completed is not null) { _batch.Completed -= _completed; }
        _completed = null; _batch.Dispose(); _batch = null;
    }
    public void Dispose() {
        if (_disposed) { return; }
        Settle(); _disposed = true; _policy.Changed -= OnPolicyChanged;
    }
}
