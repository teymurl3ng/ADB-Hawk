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
}
