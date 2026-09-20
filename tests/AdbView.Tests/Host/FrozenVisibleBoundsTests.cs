#pragma warning disable CA1707

namespace AdbView.Tests.Host;

using AdbView.Host;
using Xunit;

/// <summary>v1 decision 0005 — frozen HWND while visible.</summary>
public sealed class FrozenVisibleBoundsTests {
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Content_Resize_Applies_Only_When_Hidden(bool visible, bool expectApply) {
        Assert.Equal(expectApply, FlyoutInteractionPolicy.ShouldApplyContentResizeToHwnd(visible));
    }

    [Fact]
    public void Rapid_Disclosure_Reports_Never_Apply_While_Visible() {
        // Simulate 100 expand/collapse content reports while the session is open.
        for (int i = 0; i < 100; i++) {
            Assert.False(FlyoutInteractionPolicy.ShouldApplyContentResizeToHwnd(flyoutVisible: true));
        }
    }

    [Fact]
    public void Pairing_Phase_Reports_Never_Apply_While_Visible() {
        for (int i = 0; i < 20; i++) {
            Assert.False(FlyoutInteractionPolicy.ShouldApplyContentResizeToHwnd(flyoutVisible: true));
        }
    }

    [Fact]
    public void Next_Show_Path_Applies_When_Hidden() {
        Assert.True(FlyoutInteractionPolicy.ShouldApplyContentResizeToHwnd(flyoutVisible: false));
    }

    [Fact]
    public void Light_Dismiss_Policy_Unchanged() {
        Assert.True(FlyoutInteractionPolicy.ShouldHonorLightDismiss(suppress: false, armed: true, pinned: false));
        Assert.False(FlyoutInteractionPolicy.ShouldHonorLightDismiss(suppress: true, armed: true, pinned: false));
        Assert.False(FlyoutInteractionPolicy.ShouldHonorLightDismiss(suppress: false, armed: false, pinned: false));
        Assert.False(FlyoutInteractionPolicy.ShouldHonorLightDismiss(suppress: false, armed: true, pinned: true));
    }
}
