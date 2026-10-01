#pragma warning disable CA1707

namespace AdbView.Tests.Host;

using AdbView.Host;
using Xunit;

/// <summary>Decision 0012 (amending 0005) — live content-driven sizing: instant steps, visible or hidden.</summary>
public sealed class FrozenVisibleBoundsTests {
    [Theory]
    [InlineData(500, 392, true)]
    [InlineData(392, 500, true)]
    [InlineData(392, 392, false)]
    [InlineData(120, 560, true)]
    public void Changed_Height_Applies_Regardless_Of_Visibility(int appliedHeight, int reportedHeight, bool expectApply) {
        Assert.Equal(expectApply, FlyoutInteractionPolicy.ShouldApplyContentResize(appliedHeight, reportedHeight));
    }

    [Fact]
    public void Equal_Height_Is_Suppressed() {
        // The hidden-priming invariant: HWND bounds must actually change when heights differ,
        // and identical reports must not churn the window.
        Assert.False(FlyoutInteractionPolicy.ShouldApplyContentResize(392, 392));
    }

    [Fact]
    public void Rapid_Report_Burst_Produces_Bounded_Applies_No_Loop() {
        // 10 sweeps of expand/collapse content reports while visible. Each sweep contains four
        // real content transitions (392→458→392→560→392) plus exact duplicates, which are
        // suppressed. Invariant: constant per-sweep cost — equal reports never apply, real
        // transitions apply exactly once each, and the cost never accumulates into the
        // per-frame churn (12 resizes/click) that decision 0005 originally banned.
        int appliedHeight = 500;
        int applies = 0;
        int appliesFirstSweep = -1;
        var reported = new[] { 392, 392, 458, 458, 392, 392, 560, 560, 392, 392 };
        for (int sweep = 0; sweep < 10; sweep++) {
            int appliesThisSweep = 0;
            foreach (int height in reported) {
                if (FlyoutInteractionPolicy.ShouldApplyContentResize(appliedHeight, height)) {
                    appliedHeight = height;
                    applies++;
                    appliesThisSweep++;
                }
            }

            if (sweep == 0) {
                appliesFirstSweep = appliesThisSweep;
            }
            else {
                Assert.Equal(4, appliesThisSweep); // exactly the four transitions, nothing more
            }
        }

        Assert.Equal(5, appliesFirstSweep); // 500→392 plus the four transitions
        Assert.Equal(5 + 9 * 4, applies); // constant cost; no amplification
    }

    [Fact]
    public void Pairing_Digit_Reports_Produce_Zero_Applies() {
        // The PairingCard outer geometry is frozen (decision 0005 pins): digit entry reports
        // the same height every time, so live sizing never churns during code entry.
        int appliedHeight = 392;
        int applies = 0;
        for (int digits = 0; digits <= 6; digits++) {
            if (FlyoutInteractionPolicy.ShouldApplyContentResize(appliedHeight, 392)) {
                appliedHeight = 392;
                applies++;
            }
        }

        Assert.Equal(0, applies);
    }

    [Fact]
    public void Clamp_Bounds_Are_Applicable() {
        Assert.True(FlyoutInteractionPolicy.ShouldApplyContentResize(500, 120));
        Assert.True(FlyoutInteractionPolicy.ShouldApplyContentResize(500, 560));
        Assert.False(FlyoutInteractionPolicy.ShouldApplyContentResize(560, 560));
        Assert.False(FlyoutInteractionPolicy.ShouldApplyContentResize(120, 120));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void Initial_Show_Completes_After_First_Content_Size(int reports, bool complete) {
        Assert.Equal(complete, FlyoutInteractionPolicy.ShouldCompleteInitialShow(reports));
    }

    [Fact]
    public void Light_Dismiss_Policy_Unchanged() {
        Assert.True(FlyoutInteractionPolicy.ShouldHonorLightDismiss(suppress: false, armed: true, pinned: false));
        Assert.False(FlyoutInteractionPolicy.ShouldHonorLightDismiss(suppress: true, armed: true, pinned: false));
        Assert.False(FlyoutInteractionPolicy.ShouldHonorLightDismiss(suppress: false, armed: false, pinned: false));
        Assert.False(FlyoutInteractionPolicy.ShouldHonorLightDismiss(suppress: false, armed: true, pinned: true));
    }
}
