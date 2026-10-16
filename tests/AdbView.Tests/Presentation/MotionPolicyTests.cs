namespace AdbView.Tests.Presentation;

using System.Numerics;
using AdbView.WinUiMigration.NativeUi.Motion;
using Xunit;

public sealed class MotionPolicyTests {
    [Fact]
    public async Task ExistingAppearanceEventCarriesMotionIntoAuthoritativeState() {
        var initial = AdbView.Bridge.FixtureSnapshotProvider.GetSnapshot("connected-usb");
        using var engine = new AdbView.State.StateEngine(initial);
        Assert.True(engine.Post(new AdbView.State.ShellAppearanceChangedEvent(initial.Shell.Theme,
            initial.Shell.TransparencyEnabled, initial.Shell.ForcedColors, ReducedMotion: true)));
        await engine.DrainAsync().ConfigureAwait(true);
        Assert.True(engine.CurrentSnapshot.Shell.ReducedMotion);
        Assert.True(engine.CurrentSnapshot.Revision > initial.Revision);
        Assert.Equal(initial.Devices, engine.CurrentSnapshot.Devices);
    }
    [Fact]
    public void EitherReducedInputWinsAndOnlyEffectiveChangesNotify() {
        var policy = new MotionPolicy(true);
        int changes = 0; policy.Changed += () => changes++;
        policy.SetSnapshotReduced(true); policy.SetSystemEnabled(false); policy.SetSnapshotReduced(false);
        Assert.False(policy.Enabled); Assert.Equal(1, changes);
        policy.SetSystemEnabled(true); policy.SetSystemEnabled(true);
        Assert.True(policy.Enabled); Assert.Equal(2, changes);
    }
    [Fact]
    public void RecipesKeepFallbackBImmediateAndNamedNavigationParameters() {
        var policy = new MotionPolicy(true);
        Assert.Equal(TimeSpan.Zero, policy.Parameters(MotionRecipe.SurfaceEnter).Duration);
        Assert.Equal(TimeSpan.Zero, policy.Parameters(MotionRecipe.SurfaceExit).Duration);
        Assert.Equal(TimeSpan.Zero, policy.Parameters(MotionRecipe.SemanticUpdate).Duration);
        Assert.Equal(TimeSpan.Zero, policy.Parameters(MotionRecipe.EnvironmentChange).Duration);
        MotionParameters navigation = policy.Parameters(MotionRecipe.ContentNavigate);
        Assert.Equal(TimeSpan.FromMilliseconds(167), navigation.Duration);
        Assert.Equal(Vector2.Zero, navigation.FirstControl); Assert.Equal(new Vector2(0, 1), navigation.SecondControl);
        Assert.Equal(TimeSpan.FromMilliseconds(83), policy.Parameters(MotionRecipe.Feedback).Duration);
        policy.SetSystemEnabled(false);
        Assert.All(Enum.GetValues<MotionRecipe>(), recipe => Assert.Equal(TimeSpan.Zero, policy.Parameters(recipe).Duration));
    }
    [Fact]
    public void ImmediateSurfaceVisibilityHasNoStaleCompletionPath() {
        int shows = 0, hides = 0;
        bool interactive = false;
        using var surface = new SurfaceTransitionController(() => shows++, () => hides++, value => interactive = value);
        surface.Open(); long first = surface.Generation; surface.Open();
        Assert.Equal(first, surface.Generation); Assert.Equal(1, shows); Assert.True(interactive);
        surface.Close(); Assert.False(interactive); Assert.Equal(1, hides);
        surface.Open(); Assert.Equal(SurfaceState.Open, surface.State); Assert.True(interactive);
        surface.Suspend(); surface.Open(); Assert.Equal(SurfaceState.Hidden, surface.State);
        surface.Resume(); Assert.Equal(SurfaceState.Hidden, surface.State);
        surface.Open(); surface.Dispose(); surface.Open();
        Assert.Equal(SurfaceState.Disposed, surface.State); Assert.False(interactive);
        Assert.Equal(3, shows); Assert.Equal(3, hides);
    }
}
