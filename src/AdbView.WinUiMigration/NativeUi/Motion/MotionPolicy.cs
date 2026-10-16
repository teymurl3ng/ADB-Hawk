namespace AdbView.WinUiMigration.NativeUi.Motion;

using System.Numerics;

internal enum MotionRecipe { SurfaceEnter, SurfaceExit, ContentNavigate, Feedback, SemanticUpdate, EnvironmentChange }
internal readonly record struct MotionParameters(TimeSpan Duration, Vector2 FirstControl, Vector2 SecondControl);

/// <summary>One recipe authority. Fallback B deliberately disables whole-surface effects.</summary>
internal sealed class MotionPolicy {
    public const float ContentTravel = 12;
    private bool _systemEnabled;
    private bool _snapshotReduced;
    public bool Enabled => _systemEnabled && !_snapshotReduced;
    public event Action? Changed;
    public MotionPolicy(bool systemEnabled) { _systemEnabled = systemEnabled; }
    public void SetSystemEnabled(bool enabled) => Update(enabled, _snapshotReduced);
    public void SetSnapshotReduced(bool reduced) => Update(_systemEnabled, reduced);
    private void Update(bool system, bool reduced) {
        bool previous = Enabled;
        _systemEnabled = system; _snapshotReduced = reduced;
        if (previous != Enabled) { Changed?.Invoke(); }
    }
    public MotionParameters Parameters(MotionRecipe recipe) => recipe switch {
        MotionRecipe.ContentNavigate when Enabled => new(TimeSpan.FromMilliseconds(167), Vector2.Zero, new Vector2(0, 1)),
        MotionRecipe.Feedback when Enabled => new(TimeSpan.FromMilliseconds(83), Vector2.Zero, Vector2.One),
        _ => new(TimeSpan.Zero, Vector2.Zero, Vector2.One),
    };
}
