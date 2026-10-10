#pragma warning disable CA1707
using System.Drawing;
using AdbView.Host;
using Xunit;

namespace AdbView.Tests.Host;

public class FlyoutPlacementTests {
    private static readonly Size EffectiveSize = new Size(376, 500);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void TaskbarBottom_CentersHorizontally_AboveTaskbar(double dpi) {
        var monitorWorkArea = new Rectangle(0, 0, 1920, 1040); // Taskbar is 40px at bottom
        var anchorRect = new Rectangle(1800, 1040, 40, 40);

        var result = FlyoutPlacement.CalculatePlacement(
            anchorRect,
            FlyoutPlacement.TaskbarEdge.Bottom,
            monitorWorkArea,
            dpi,
            EffectiveSize);

        var physicalWidth = (int)(EffectiveSize.Width * dpi);
        var physicalHeight = (int)(EffectiveSize.Height * dpi);
        var gap = (int)(12 * dpi);

        // Y should be above anchor minus gap
        Assert.Equal(anchorRect.Top - physicalHeight - gap, result.Y);
        // X should be clamped to right edge because anchor is far right
        Assert.Equal(monitorWorkArea.Right - physicalWidth - gap, result.X);
    }

    [Fact]
    public void TaskbarTop_PlacesBelowTaskbar() {
        var monitorWorkArea = new Rectangle(0, 40, 1920, 1040);
        var anchorRect = new Rectangle(1800, 0, 40, 40);

        var result = FlyoutPlacement.CalculatePlacement(
            anchorRect,
            FlyoutPlacement.TaskbarEdge.Top,
            monitorWorkArea,
            1.0,
            EffectiveSize);

        Assert.Equal(40 + 12, result.Y);
        Assert.Equal(monitorWorkArea.Right - EffectiveSize.Width - 12, result.X);
    }

    [Fact]
    public void TaskbarLeft_PlacesRightOfTaskbar() {
        var monitorWorkArea = new Rectangle(40, 0, 1880, 1080);
        var anchorRect = new Rectangle(0, 1000, 40, 40);

        var result = FlyoutPlacement.CalculatePlacement(
            anchorRect,
            FlyoutPlacement.TaskbarEdge.Left,
            monitorWorkArea,
            1.0,
            EffectiveSize);

        Assert.Equal(40 + 12, result.X);
        Assert.Equal(monitorWorkArea.Bottom - EffectiveSize.Height - 12, result.Y);
    }

    [Fact]
    public void TaskbarRight_PlacesLeftOfTaskbar() {
        var monitorWorkArea = new Rectangle(0, 0, 1880, 1080);
        var anchorRect = new Rectangle(1880, 1000, 40, 40);

        var result = FlyoutPlacement.CalculatePlacement(
            anchorRect,
            FlyoutPlacement.TaskbarEdge.Right,
            monitorWorkArea,
            1.0,
            EffectiveSize);

        Assert.Equal(1880 - EffectiveSize.Width - 12, result.X);
    }

    [Fact]
    public void NegativeMonitor_ClampsCorrectly() {
        var monitorWorkArea = new Rectangle(-1920, 0, 1920, 1040);
        var anchorRect = new Rectangle(-100, 1040, 40, 40);

        var result = FlyoutPlacement.CalculatePlacement(
            anchorRect,
            FlyoutPlacement.TaskbarEdge.Bottom,
            monitorWorkArea,
            1.0,
            EffectiveSize);

        Assert.True(result.Right <= 0 - 12);
    }

    [Fact]
    public void DefaultTrayAnchor_Bottom_CalculatesValidPlacement() {
        TestDefaultTrayAnchorForEdge(FlyoutPlacement.TaskbarEdge.Bottom);
    }

    [Fact]
    public void DefaultTrayAnchor_Top_CalculatesValidPlacement() {
        TestDefaultTrayAnchorForEdge(FlyoutPlacement.TaskbarEdge.Top);
    }

    [Fact]
    public void DefaultTrayAnchor_Left_CalculatesValidPlacement() {
        TestDefaultTrayAnchorForEdge(FlyoutPlacement.TaskbarEdge.Left);
    }

    [Fact]
    public void DefaultTrayAnchor_Right_CalculatesValidPlacement() {
        TestDefaultTrayAnchorForEdge(FlyoutPlacement.TaskbarEdge.Right);
    }

    private static void TestDefaultTrayAnchorForEdge(FlyoutPlacement.TaskbarEdge edge) {
        var monitorBounds = new Rectangle(0, 0, 1920, 1080);
        var monitorWorkArea = edge switch {
            FlyoutPlacement.TaskbarEdge.Bottom => new Rectangle(0, 0, 1920, 1040),
            FlyoutPlacement.TaskbarEdge.Top => new Rectangle(0, 40, 1920, 1040),
            FlyoutPlacement.TaskbarEdge.Left => new Rectangle(40, 0, 1880, 1080),
            FlyoutPlacement.TaskbarEdge.Right => new Rectangle(0, 0, 1880, 1080),
            _ => monitorBounds
        };

        var anchor = FlyoutPlacement.GetDefaultTrayAnchor(monitorWorkArea, monitorBounds, edge, 1.0);
        var result = FlyoutPlacement.CalculatePlacement(
            anchor,
            edge,
            monitorWorkArea,
            1.0,
            EffectiveSize);

        Assert.True(result.Left >= monitorWorkArea.Left);
        Assert.True(result.Top >= monitorWorkArea.Top);
        Assert.True(result.Right <= monitorWorkArea.Right);
        Assert.True(result.Bottom <= monitorWorkArea.Bottom);
        Assert.Equal(EffectiveSize.Width, result.Width);
        Assert.Equal(EffectiveSize.Height, result.Height);
    }

    [Fact]
    public void ResolveAnchor_WithValidExactRect_UsesExactRect() {
        var exactRect = new Rectangle(1200, 1040, 16, 16);
        var monitorWorkArea = new Rectangle(0, 0, 1920, 1040);
        var monitorBounds = new Rectangle(0, 0, 1920, 1080);

        var anchor = FlyoutPlacement.ResolveAnchor(
            exactRect,
            monitorWorkArea,
            monitorBounds,
            FlyoutPlacement.TaskbarEdge.Bottom,
            1.0);

        Assert.Equal(exactRect, anchor);
    }

    [Fact]
    public void ResolveAnchor_WithInvalidOrEmptyRect_UsesDefaultTrayAnchor_NotCursorPosition() {
        // When Shell_NotifyIconGetRect is unavailable or returns empty rect:
        // Must yield GetDefaultTrayAnchor, NEVER defaulting to cursor position (0,0) or top-left (12,12)
        var emptyOrUnavailableRect = Rectangle.Empty;
        var monitorWorkArea = new Rectangle(0, 0, 1920, 1040);
        var monitorBounds = new Rectangle(0, 0, 1920, 1080);

        var expectedDefault = FlyoutPlacement.GetDefaultTrayAnchor(
            monitorWorkArea,
            monitorBounds,
            FlyoutPlacement.TaskbarEdge.Bottom,
            1.0);

        var anchor = FlyoutPlacement.ResolveAnchor(
            emptyOrUnavailableRect,
            monitorWorkArea,
            monitorBounds,
            FlyoutPlacement.TaskbarEdge.Bottom,
            1.0);

        Assert.Equal(expectedDefault, anchor);

        // Resulting placement must be near taskbar notification area, NOT clamped to top-left (12, 12)
        var placement = FlyoutPlacement.CalculatePlacement(
            anchor,
            FlyoutPlacement.TaskbarEdge.Bottom,
            monitorWorkArea,
            1.0,
            EffectiveSize);

        Assert.True(placement.X > 1000, $"Expected placement near right taskbar edge, got X={placement.X}");
        Assert.Equal(anchor.Top - EffectiveSize.Height - 12, placement.Y);
    }

    [Fact]
    public void ResolveAnchor_Fallback_SupportsAllTaskbarEdges() {
        foreach (var edge in Enum.GetValues<FlyoutPlacement.TaskbarEdge>()) {
            var monitorBounds = new Rectangle(0, 0, 1920, 1080);
            var monitorWorkArea = edge switch {
                FlyoutPlacement.TaskbarEdge.Bottom => new Rectangle(0, 0, 1920, 1040),
                FlyoutPlacement.TaskbarEdge.Top => new Rectangle(0, 40, 1920, 1040),
                FlyoutPlacement.TaskbarEdge.Left => new Rectangle(40, 0, 1880, 1080),
                FlyoutPlacement.TaskbarEdge.Right => new Rectangle(0, 0, 1880, 1080),
                _ => monitorBounds
            };

            var anchor = FlyoutPlacement.ResolveAnchor(
                Rectangle.Empty,
                monitorWorkArea,
                monitorBounds,
                edge,
                1.0);

            var placement = FlyoutPlacement.CalculatePlacement(
                anchor,
                edge,
                monitorWorkArea,
                1.0,
                EffectiveSize);

            Assert.True(placement.Left >= monitorWorkArea.Left);
            Assert.True(placement.Top >= monitorWorkArea.Top);
            Assert.True(placement.Right <= monitorWorkArea.Right);
            Assert.True(placement.Bottom <= monitorWorkArea.Bottom);
            Assert.Equal(EffectiveSize.Width, placement.Width);
            Assert.Equal(EffectiveSize.Height, placement.Height);
        }
    }

    [Fact]
    public void CalculatePlacement_ResizeContentHeights_RemainsAnchoredToEdge() {
        foreach (var edge in Enum.GetValues<FlyoutPlacement.TaskbarEdge>()) {
            var monitorBounds = new Rectangle(0, 0, 1920, 1080);
            var monitorWorkArea = edge switch {
                FlyoutPlacement.TaskbarEdge.Bottom => new Rectangle(0, 0, 1920, 1040),
                FlyoutPlacement.TaskbarEdge.Top => new Rectangle(0, 40, 1920, 1040),
                FlyoutPlacement.TaskbarEdge.Left => new Rectangle(40, 0, 1880, 1080),
                FlyoutPlacement.TaskbarEdge.Right => new Rectangle(0, 0, 1880, 1080),
                _ => monitorBounds
            };

            var anchor = FlyoutPlacement.ResolveAnchor(
                Rectangle.Empty,
                monitorWorkArea,
                monitorBounds,
                edge,
                1.0);

            int[] heights = [120, 300, 560];
            Rectangle[] placements = heights
                .Select(h => FlyoutPlacement.CalculatePlacement(anchor, edge, monitorWorkArea, 1.0, new Size(376, h)))
                .ToArray();

            for (int i = 0; i < heights.Length; i++) {
                Assert.Equal(376, placements[i].Width);
                Assert.Equal(heights[i], placements[i].Height);

                switch (edge) {
                    case FlyoutPlacement.TaskbarEdge.Bottom:
                        // Horizontal position and bottom anchor remain fixed; top moves upward
                        Assert.Equal(placements[0].Left, placements[i].Left);
                        Assert.Equal(placements[0].Bottom, placements[i].Bottom);
                        if (i > 0) Assert.True(placements[i].Top < placements[i - 1].Top);
                        break;
                    case FlyoutPlacement.TaskbarEdge.Top:
                        // Horizontal position and top anchor remain fixed; bottom moves downward
                        Assert.Equal(placements[0].Left, placements[i].Left);
                        Assert.Equal(placements[0].Top, placements[i].Top);
                        if (i > 0) Assert.True(placements[i].Bottom > placements[i - 1].Bottom);
                        break;
                    case FlyoutPlacement.TaskbarEdge.Left:
                        Assert.Equal(placements[0].Left, placements[i].Left);
                        break;
                    case FlyoutPlacement.TaskbarEdge.Right:
                        Assert.Equal(placements[0].Right, placements[i].Right);
                        break;
                }
            }
        }
    }
}

/// <summary>
/// Decision 0024: the open reveal's placement plan. A visible window (a re-open during a close
/// slide, or a re-place after a display change) retargets from its current bounds — snapping it
/// to the off-tray origin would teleport it off-screen (disappear/reappear). A hidden window
/// reveals from the off-tray origin. Under reduced motion there is no slide.
/// </summary>
public sealed class OpenRevealPlanTests {
    private static readonly Rectangle Current = new(100, 200, 376, 392);
    private static readonly Rectangle Final = new(100, 600, 376, 392);
    private static readonly Rectangle Origin = new(100, 1040, 376, 392);

    [Fact]
    public void VisibleWindowRetargetsFromCurrentBounds() {
        var plan = FlyoutPlacement.OpenRevealPlan.Resolve(
            windowVisible: true,
            reducedMotion: false,
            Current,
            Final,
            Origin);

        Assert.Equal(Current, plan.Placement);
        Assert.True(plan.Slide);
    }

    [Fact]
    public void VisibleWindowUnderReducedMotionSnapsToFinal() {
        var plan = FlyoutPlacement.OpenRevealPlan.Resolve(
            windowVisible: true,
            reducedMotion: true,
            Current,
            Final,
            Origin);

        Assert.Equal(Final, plan.Placement);
        Assert.False(plan.Slide);
    }

    [Fact]
    public void HiddenWindowRevealsFromTheOffTrayOrigin() {
        var plan = FlyoutPlacement.OpenRevealPlan.Resolve(
            windowVisible: false,
            reducedMotion: false,
            Current,
            Final,
            Origin);

        Assert.Equal(Origin, plan.Placement);
        Assert.True(plan.Slide);
    }

    [Fact]
    public void HiddenWindowUnderReducedMotionPlacesAtFinal() {
        var plan = FlyoutPlacement.OpenRevealPlan.Resolve(
            windowVisible: false,
            reducedMotion: true,
            Current,
            Final,
            Origin);

        Assert.Equal(Final, plan.Placement);
        Assert.False(plan.Slide);
    }
}



