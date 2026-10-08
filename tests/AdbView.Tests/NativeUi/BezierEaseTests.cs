#pragma warning disable CA1707
namespace AdbView.Tests.NativeUi;

using AdbView.Host;
using AdbView.NativeUi.Motion;
using Xunit;

/// <summary>
/// Decision 0023: the XAML storyboards animate on the same frozen curve as the window animator.
/// <see cref="BezierEase"/> delegates to <see cref="Easing.FluentPointToPoint"/>, so this test
/// pins the whole composed path — if someone changes the easing class, its EasingMode, or the
/// code curve, XAML and code can no longer disagree silently.
/// </summary>
public sealed class BezierEaseTests {
    [Fact]
    public void EaseMatchesTheCodeAnimatorCurve() {
        var ease = new BezierEase();

        for (int i = 0; i <= 40; i++) {
            double t = i / 40.0;
            Assert.Equal(Easing.FluentPointToPoint(t), ease.Ease(t), 9);
        }
    }

    [Fact]
    public void EndpointsAreExact() {
        var ease = new BezierEase();

        Assert.Equal(0.0, ease.Ease(0.0), 9);
        Assert.Equal(1.0, ease.Ease(1.0), 9);
    }
}
