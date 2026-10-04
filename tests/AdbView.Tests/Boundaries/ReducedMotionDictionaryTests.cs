#pragma warning disable CA1707
namespace AdbView.Tests.Boundaries;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Decision 0021: Themes/ControlsReduced.xaml is the reduced-motion twin of Controls.xaml —
/// identical styles with instant setters instead of trigger animations. Nothing else pins the
/// two together, so this test pins key parity and the no-animation invariant; without it the
/// twins could silently drift.
/// </summary>
public sealed class ReducedMotionDictionaryTests {
    private static string RepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) {
            dir = dir.Parent!;
        }

        return dir!.FullName;
    }

    private static string ReadTheme(string fileName) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "AdbView", "NativeUi", "Themes", fileName));

    private static List<string> KeysOf(string xaml) =>
        Regex.Matches(xaml, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void ReducedVariantDefinesEveryKeyTheAnimatedVariantDefines() {
        var animated = KeysOf(ReadTheme("Controls.xaml"));
        var reduced = KeysOf(ReadTheme("ControlsReduced.xaml"));

        var missing = animated.Except(reduced).ToList();
        Assert.True(missing.Count == 0, "ControlsReduced.xaml is missing keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void ReducedVariantContainsNoStoryboardAnimations() {
        var reduced = ReadTheme("ControlsReduced.xaml");

        Assert.DoesNotContain("BeginStoryboard", reduced, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleAnimation", reduced, StringComparison.Ordinal);
    }

    [Fact]
    public void ReducedVariantMotionTokensAreZero() {
        var reduced = ReadTheme("ControlsReduced.xaml");

        Assert.Contains("<Duration x:Key=\"Motion.Feedback\">0:0:0</Duration>", reduced, StringComparison.Ordinal);
        Assert.Contains("<Duration x:Key=\"Motion.State\">0:0:0</Duration>", reduced, StringComparison.Ordinal);
        Assert.Contains("<Duration x:Key=\"Motion.Structural\">0:0:0</Duration>", reduced, StringComparison.Ordinal);
    }
}
