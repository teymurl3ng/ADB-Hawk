namespace AdbView.Tests.Host;

using AdbView.WinUiMigration.Host;
using Xunit;

public sealed class CandidateBubbleVisibilityTests {
    [Fact]
    public void OnlyEnabledCurrentStateWithNoPresentationSuppressionCanShow() {
        for (int bits = 0; bits < 32; bits++) {
            bool enabled = (bits & 1) != 0, flyoutVisible = (bits & 2) != 0,
                fullScreen = (bits & 4) != 0, suspended = (bits & 8) != 0, hasState = (bits & 16) != 0;
            Assert.Equal(bits == 17, BubbleVisibility.ShouldShow(enabled, flyoutVisible, fullScreen, suspended, hasState));
        }
    }
}
