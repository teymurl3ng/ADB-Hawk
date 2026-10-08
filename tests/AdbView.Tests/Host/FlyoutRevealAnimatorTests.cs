#pragma warning disable CA1707

namespace AdbView.Tests.Host;

using System;
using System.Collections.Generic;
using System.Drawing;
using AdbView.Host;
using Xunit;

internal sealed class FakeTickSource : ITickSource {
    public Action? TickAction { get; private set; }
    public int StartCount { get; private set; }
    public bool IsRunning { get; private set; }

    public void Start(Action tick) {
        TickAction = tick;
        StartCount++;
        IsRunning = true;
    }

    public void Stop() {
        IsRunning = false;
        TickAction = null;
    }

    public void Tick() => TickAction?.Invoke();

    public void Dispose() { }
}

internal sealed class FakeClock {
    public long Now { get; private set; }
    public void Advance(long ms) => Now += ms;
    public long Read() => Now;
}

internal static class RectFactory {
    public static Rectangle Rect(int x, int y, int w, int h) => new(x, y, w, h);
}

public sealed class FlyoutRevealAnimatorTests {
    private static Rectangle Rect(int x, int y, int w, int h) => RectFactory.Rect(x, y, w, h);

    [Fact]
    public void EqualFromToAppliesImmediatelyWithoutStarting() {
        using var ticks = new FakeTickSource();
        var applied = new List<Rectangle>();
        var animator = new FlyoutRevealAnimator(ticks, applied.Add, new FakeClock().Read);

        animator.AnimateBounds(Rect(0, 0, 376, 392), Rect(0, 0, 376, 392), TimeSpan.FromMilliseconds(167));

        Assert.Equal(Rect(0, 0, 376, 392), Assert.Single(applied));
        Assert.False(animator.IsAnimating);
        Assert.Equal(0, ticks.StartCount);
    }

    [Fact]
    public void GrowthAnimatesFramesAndCompletesOnTarget() {
        using var ticks = new FakeTickSource();
        var clock = new FakeClock();
        var applied = new List<Rectangle>();
        int completions = 0;
        var animator = new FlyoutRevealAnimator(
            ticks,
            r => applied.Add(r),
            clock.Read);

        animator.AnimateBounds(
            Rect(0, 100, 376, 400),
            Rect(0, 40, 376, 460),
            TimeSpan.FromMilliseconds(167),
            completed: () => completions++);

        Assert.True(animator.IsAnimating);
        ticks.Tick(); // first frame at progress 0
        clock.Advance(167);
        ticks.Tick(); // past duration → completes on target

        Assert.Equal(Rect(0, 40, 376, 460), applied[^1]);
        Assert.False(animator.IsAnimating);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void RetargetMidFlightContinuesFromLastAppliedFrame() {
        using var ticks = new FakeTickSource();
        var clock = new FakeClock();
        var applied = new List<Rectangle>();
        var animator = new FlyoutRevealAnimator(ticks, applied.Add, clock.Read);

        animator.AnimateBounds(Rect(0, 100, 376, 400), Rect(0, 40, 376, 460), TimeSpan.FromMilliseconds(167));
        clock.Advance(40);
        ticks.Tick(); // mid-flight frame

        // Latest content change wins: retarget from the current animated state (R-06).
        animator.AnimateBounds(animator.LastApplied, Rect(0, 20, 376, 480), TimeSpan.FromMilliseconds(167));
        clock.Advance(167);
        ticks.Tick();

        Assert.Equal(Rect(0, 20, 376, 480), applied[^1]);
        Assert.False(animator.IsAnimating);
    }

    [Fact]
    public void CancelStopsWithoutApplyingTarget() {
        using var ticks = new FakeTickSource();
        var clock = new FakeClock();
        var applied = new List<Rectangle>();
        var animator = new FlyoutRevealAnimator(ticks, applied.Add, clock.Read);

        animator.AnimateBounds(Rect(0, 100, 376, 400), Rect(0, 40, 376, 460), TimeSpan.FromMilliseconds(167));
        clock.Advance(40);
        ticks.Tick();
        int framesBefore = applied.Count;

        animator.Cancel();
        clock.Advance(500);
        ticks.Tick();

        Assert.False(animator.IsAnimating);
        Assert.Equal(framesBefore, applied.Count);
    }

    [Fact]
    public void CompleteImmediatelySnapsToTargetAndFiresCompletionOnce() {
        using var ticks = new FakeTickSource();
        var clock = new FakeClock();
        var applied = new List<Rectangle>();
        int completions = 0;
        var animator = new FlyoutRevealAnimator(ticks, applied.Add, clock.Read);

        animator.AnimateBounds(
            Rect(0, 100, 376, 400),
            Rect(0, 40, 376, 460),
            TimeSpan.FromMilliseconds(167),
            completed: () => completions++);

        animator.CompleteImmediately();

        Assert.Equal(Rect(0, 40, 376, 460), applied[^1]);
        Assert.Equal(1, completions);
        Assert.False(animator.IsAnimating);
    }

    [Fact]
    public void FramesAreMonotoneTowardTarget() {
        using var ticks = new FakeTickSource();
        var clock = new FakeClock();
        var applied = new List<Rectangle>();
        var animator = new FlyoutRevealAnimator(ticks, applied.Add, clock.Read);

        animator.AnimateBounds(Rect(0, 100, 376, 400), Rect(0, 40, 376, 460), TimeSpan.FromMilliseconds(167));

        int lastY = 100;
        for (int i = 0; i < 20; i++) {
            clock.Advance(10);
            ticks.Tick();
            if (animator.IsAnimating || i == 19) {
                int y = applied[^1].Y;
                Assert.True(y <= lastY, $"frame Y regressed: {y} > {lastY}");
                lastY = y;
            }

            if (!animator.IsAnimating) {
                break;
            }
        }

        Assert.Equal(40, lastY);
    }

    [Fact]
    public void RetargetComposesPriorCompletion_LightDismissArmingIsNeverLost() {
        // Decision 0013 integration: a growth resize retargeting an in-flight OPEN reveal must
        // not drop the open's completion (light-dismiss arming) — the exact bug this guards.
        using var ticks = new FakeTickSource();
        var clock = new FakeClock();
        var applied = new List<Rectangle>();
        int openCompletions = 0;
        int growthCompletions = 0;
        var animator = new FlyoutRevealAnimator(ticks, applied.Add, clock.Read);

        animator.AnimateBounds(
            Rect(0, 100, 376, 400),
            Rect(0, 40, 376, 460),
            TimeSpan.FromMilliseconds(167),
            completed: () => openCompletions++);

        clock.Advance(40);
        ticks.Tick();

        animator.AnimateBounds(
            animator.LastApplied,
            Rect(0, 20, 376, 480),
            TimeSpan.FromMilliseconds(167),
            completed: () => growthCompletions++);

        clock.Advance(167);
        ticks.Tick();
        clock.Advance(167);
        ticks.Tick();

        Assert.Equal(1, openCompletions);
        Assert.Equal(1, growthCompletions);
        Assert.False(animator.IsAnimating);
    }

    // Decision 0023 / DESIGN §24: the reveal is a position-only translation — the window's size
    // is fixed for the flight, so the slide never resizes the shadowed HWND per frame.

    [Fact]
    public void SlideIsPositionOnly_SizeNeverChangesMidFlight() {
        using var ticks = new FakeTickSource();
        var clock = new FakeClock();
        var applied = new List<Rectangle>();
        var animator = new FlyoutRevealAnimator(ticks, applied.Add, clock.Read);

        animator.AnimateSlide(Rect(100, 600, 376, 392), Rect(100, 200, 376, 392), TimeSpan.FromMilliseconds(167));

        for (int i = 0; i < 12 && animator.IsAnimating; i++) {
            clock.Advance(20);
            ticks.Tick();
        }

        Assert.NotEmpty(applied);
        Assert.All(applied, frame => {
            Assert.Equal(376, frame.Width);
            Assert.Equal(392, frame.Height);
        });
        Assert.Equal(Rect(100, 200, 376, 392), applied[^1]);
    }

    [Fact]
    public void SlideEndpointsDifferingInSizeAdoptTheTargetSize() {
        // A stale origin (e.g. read after a layout pass stepped the height) must not turn the
        // slide into a resize; the target size wins from the first applied frame.
        using var ticks = new FakeTickSource();
        var clock = new FakeClock();
        var applied = new List<Rectangle>();
        var animator = new FlyoutRevealAnimator(ticks, applied.Add, clock.Read);

        animator.AnimateSlide(Rect(0, 600, 300, 300), Rect(0, 200, 376, 392), TimeSpan.FromMilliseconds(167));

        clock.Advance(40);
        ticks.Tick();

        Assert.NotEmpty(applied);
        Assert.All(applied, frame => {
            Assert.Equal(376, frame.Width);
            Assert.Equal(392, frame.Height);
        });
    }

    [Fact]
    public void IdenticalFramesAreNotReapplied() {
        // The frame callback can fire more than once per composed frame; a rect identical to the
        // one the window is already at is skipped rather than re-issuing SetWindowPos. That
        // includes the start itself: the caller has already placed the window on `from`, so the
        // progress-0 frame is a no-op too.
        using var ticks = new FakeTickSource();
        var clock = new FakeClock();
        var applied = new List<Rectangle>();
        var animator = new FlyoutRevealAnimator(ticks, applied.Add, clock.Read);

        animator.AnimateBounds(Rect(0, 100, 376, 400), Rect(0, 40, 376, 460), TimeSpan.FromMilliseconds(167));

        ticks.Tick(); // same wall-clock instant → same frame → still nothing applied
        Assert.Empty(applied);

        clock.Advance(167);
        ticks.Tick(); // completion applies the target exactly once
        Assert.Equal(Rect(0, 40, 376, 460), Assert.Single(applied));
    }
}

public sealed class EasingTests {
    [Fact]
    public void EndpointsAreExact() {
        Assert.Equal(0.0, Easing.FluentPointToPoint(0.0));
        Assert.Equal(1.0, Easing.FluentPointToPoint(1.0));
    }

    [Fact]
    public void CurveIsMonotoneAndBounded() {
        double previous = 0.0;
        for (int i = 1; i <= 20; i++) {
            double value = Easing.FluentPointToPoint(i / 20.0);
            Assert.True(value > previous, $"easing regressed at {i}/20");
            Assert.InRange(value, 0.0, 1.0);
            previous = value;
        }
    }
}
