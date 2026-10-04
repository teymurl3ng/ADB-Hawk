#pragma warning disable CA1707
namespace AdbView.Tests.Host;

using AdbView.Host;
using Xunit;

/// <summary>
/// Decision 0021 self-sizing: one instant step per settled change, bottom edge frozen, 560 DIP
/// ceiling with scroll takeover, work-area clamping. The math is pure so these pin it exactly.
/// </summary>
public sealed class ShellSizingTests {
    private const double Bottom = 816;   // DIP bottom edge (125% monitor's work area bottom)
    private const double WorkTop = 0;

    [Fact]
    public void ContentBelowTheFloor_ClampsToTheMinimum() {
        var (height, top) = ShellSizing.Step(80, WorkTop, Bottom).GetValueOrDefault();

        Assert.Equal(ShellSizing.MinHeightDip, height);
        Assert.Equal(Bottom - ShellSizing.MinHeightDip, top);
    }

    [Fact]
    public void ContentBetweenFloorAndCeiling_IsTakenVerbatim() {
        var (height, top) = ShellSizing.Step(372, WorkTop, Bottom).GetValueOrDefault();

        Assert.Equal(372, height, 3);
        Assert.Equal(Bottom - 372, top, 3);
    }

    [Fact]
    public void ContentAboveTheCeiling_ClampsToScrollTakeover() {
        var (height, top) = ShellSizing.Step(900, WorkTop, Bottom).GetValueOrDefault();

        Assert.Equal(ShellSizing.MaxHeightDip, height);
        Assert.Equal(Bottom - ShellSizing.MaxHeightDip, top);
    }

    [Fact]
    public void TheBottomEdgeNeverMoves() {
        var (_, top1) = ShellSizing.Step(200, WorkTop, Bottom).GetValueOrDefault();
        var (_, top2) = ShellSizing.Step(560, WorkTop, Bottom).GetValueOrDefault();

        Assert.Equal(Bottom - 200, top1, 3);
        Assert.Equal(Bottom - 560, top2, 3);
        Assert.Equal(top1 + 200, top2 + 560, 3); // same bottom edge
    }

    [Fact]
    public void TightWorkArea_ClampsToWhatFitsAboveTheFloor() {
        // A short work area: a 500 DIP want does not fit between the work-area top (400) and the
        // frozen bottom (816), so the height shrinks to exactly what fits — top pinned to the edge.
        var (height, top) = ShellSizing.Step(500, workAreaTopDip: 400, currentBottomDip: 816).GetValueOrDefault();

        Assert.Equal(416, height, 3);
        Assert.Equal(400, top, 3);
    }

    [Fact]
    public void WorkAreaThatFitsTheWant_IsUntouched() {
        // A 300 DIP window at bottom 816 sits at top 516 — inside a work area starting at 400.
        var (height, top) = ShellSizing.Step(300, workAreaTopDip: 400, currentBottomDip: 816).GetValueOrDefault();

        Assert.Equal(300, height, 3);
        Assert.Equal(516, top, 3);
    }

    /// <summary>Priority when a (hypothetical) work area cannot fit even the floor: the floor wins
    /// and the top pokes above the work area. A sub-floor flyout (header + nothing) is worse than
    /// a partial overlap on displays small enough to trigger this; real work areas are never this
    /// small (the 560 ceiling itself needs ~560 DIP).</summary>
    [Fact]
    public void WorkAreaSmallerThanTheFloor_TheFloorWins() {
        var (height, top) = ShellSizing.Step(300, workAreaTopDip: 700, currentBottomDip: 816).GetValueOrDefault();

        Assert.Equal(ShellSizing.MinHeightDip, height);
        Assert.Equal(Bottom - ShellSizing.MinHeightDip, top, 3);
    }

    [Theory]
    [InlineData(372.0, 372.0, false)]
    [InlineData(372.4, 372.0, false)]
    [InlineData(372.6, 372.0, true)]
    [InlineData(371.0, 372.0, true)]
    public void SubDipDifferencesAreNotSteps(double target, double applied, bool expected) {
        Assert.Equal(expected, ShellSizing.IsWorthAStep(target, applied));
    }
}
