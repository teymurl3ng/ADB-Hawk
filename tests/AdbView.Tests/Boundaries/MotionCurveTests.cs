#pragma warning disable CA1707
namespace AdbView.Tests.Boundaries;

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Decision 0023 / DESIGN §24: the shared production easing is cubic-bezier(0.55, 0.55, 0, 1),
/// and every production storyboard animation carries it. XAML is text, so the gate is structural:
/// each DoubleAnimation in the chrome and the cards must declare an easing function. The one
/// documented exception is the pairing spinner's deliberately linear busy loop (decision 0023
/// records it as a separate defect), which is why PairingCardView allows exactly one.
/// </summary>
public sealed class MotionCurveTests {
    private static string RepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) {
            dir = dir.Parent!;
        }

        return dir!.FullName;
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "src", "AdbView", .. parts]));

    // "[\s>]" so the easing child tag <DoubleAnimation.EasingFunction> is not counted as an
    // animation (a word boundary sits before the '.').
    private static (int Animations, int Eased) CountAnimations(string xaml) => (
        Regex.Count(xaml, "<DoubleAnimation[\\s>]"),
        Regex.Count(xaml, "<DoubleAnimation\\.EasingFunction>"));

    [Fact]
    public void ChromeAnimationsCarryTheFrozenCurve() {
        var (animations, eased) = CountAnimations(ReadSource("NativeUi", "Themes", "Controls.xaml"));

        Assert.True(animations > 0, "Controls.xaml is expected to define trigger animations");
        Assert.Equal(animations, eased);
    }

    [Fact]
    public void DeviceCardAnimationsCarryTheFrozenCurve() {
        var (animations, eased) = CountAnimations(ReadSource("NativeUi", "Cards", "DeviceCardView.xaml"));

        Assert.True(animations > 0, "DeviceCardView.xaml is expected to define trigger animations");
        Assert.Equal(animations, eased);
    }

    [Fact]
    public void PairingCardAnimationsCarryTheCurveExceptTheDocumentedSpinner() {
        var (animations, eased) = CountAnimations(ReadSource("NativeUi", "Shell", "PairingCardView.xaml"));

        // Exactly one linear animation: the 850 ms RepeatBehavior="Forever" spinner, documented
        // in decision 0023 as a separate defect (not reduced-motion gated, no Motion.* token).
        Assert.Equal(animations - 1, eased);
    }

    [Fact]
    public void TheCurveIsTheSharedBezierEase() {
        var chrome = ReadSource("NativeUi", "Themes", "Controls.xaml");
        var card = ReadSource("NativeUi", "Cards", "DeviceCardView.xaml");
        var pairing = ReadSource("NativeUi", "Shell", "PairingCardView.xaml");

        // One easing implementation, so a second curve cannot appear beside the frozen one.
        foreach (var xaml in new[] { chrome, card, pairing }) {
            Assert.Contains("motion:BezierEase", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("CubicEase", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("QuadraticEase", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("KeySpline", xaml, StringComparison.Ordinal);
        }
    }
}
