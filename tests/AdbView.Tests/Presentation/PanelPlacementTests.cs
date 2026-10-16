namespace AdbView.Tests.Presentation;

using AdbView.Presentation;
using Xunit;

public sealed class PanelPlacementTests {
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void PhysicalBoundsIncludeBorderAndFitNegativeAndSmallWorkAreas(double scale) {
        foreach (PanelBounds work in new[] { new PanelBounds(0, 0, 1920, 1040), new PanelBounds(-1920, -900, 1280, 800), new PanelBounds(-300, -200, 240, 180) }) {
            PanelBounds result = PanelPlacement.Calculate(work, scale, 4, 6);
            Assert.True(result.Width > 0 && result.Height > 0);
            Assert.True(result.X >= work.X && result.Y >= work.Y);
            Assert.Equal(work.X + work.Width - (int)Math.Round(8 * scale), result.X + result.Width);
            Assert.Equal(work.Y + work.Height - (int)Math.Round(8 * scale), result.Y + result.Height);
            Assert.True(result.Width <= (int)Math.Round(376 * scale) + 4);
            Assert.True(result.Height <= (int)Math.Round(480 * scale) + 6);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidScaleIsRejected(double scale) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        PanelPlacement.Calculate(new PanelBounds(0, 0, 1920, 1080), scale, 0, 0));
}
