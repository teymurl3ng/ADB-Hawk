#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.NativeUi;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdbView.NativeUi;
using AdbView.State;
using Xunit;

/// <summary>
/// Decision 0020 / DESIGN.md §30: which aggregate state the bubble shows, in which colour,
/// for how long, and what it does when a transient expires.
/// </summary>
public sealed class StatusBubblePresenterTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Hand-controlled delay: the test decides when the one-shot lifetime completes.</summary>
    private sealed class ManualDelay {
        private readonly List<TaskCompletionSource> _pending = new();
        private readonly List<TimeSpan> _requested = new();

        public TimeSpan[] Requested {
            get {
                lock (_pending) {
                    return _requested.ToArray();
                }
            }
        }

        public int Waiting {
            get {
                lock (_pending) {
                    return _pending.Count;
                }
            }
        }

        public Task WaitAsync(TimeSpan span, CancellationToken ct) {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) {
                _requested.Add(span);
                _pending.Add(tcs);
            }

            // A cancelled wait is no longer pending: retargeting replaces it rather than adding
            // to it, so the pending count must not include the superseded entry.
            ct.Register(() => {
                lock (_pending) {
                    _pending.Remove(tcs);
                }

                tcs.TrySetCanceled(ct);
            });
            return tcs.Task;
        }

        public void CompleteAll() {
            List<TaskCompletionSource> waiting;
            lock (_pending) {
                waiting = new List<TaskCompletionSource>(_pending);
                _pending.Clear();
            }

            foreach (TaskCompletionSource tcs in waiting) {
                tcs.TrySetResult();
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000) {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            if (condition()) {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("condition not met within " + timeoutMs + "ms");
    }

    private static (StatusBubblePresenter Presenter, ManualDelay Delay, List<BubbleState?> Changes) Create() {
        var delay = new ManualDelay();
        var presenter = new StatusBubblePresenter(() => T0, delay.WaitAsync);
        var changes = new List<BubbleState?>();
        presenter.Changed += state => changes.Add(state);
        return (presenter, delay, changes);
    }

    [Fact]
    public void NothingToReportStaysHidden() {
        var (presenter, delay, _) = Create();
        using (presenter) {
            presenter.Accept(TrayStatusKind.Idle, "No ADB devices");

            Assert.Null(presenter.Current);
            Assert.Equal(0, delay.Waiting);
        }
    }

    [Theory]
    [InlineData((int)TrayStatusKind.Connected, (int)BubbleColor.Green)]
    [InlineData((int)TrayStatusKind.Attention, (int)BubbleColor.Amber)]
    [InlineData((int)TrayStatusKind.PairingAvailable, (int)BubbleColor.Blue)]
    public void PermanentStatesShowWhileTheirConditionHolds(int statusValue, int expectedValue) {
        var status = (TrayStatusKind)statusValue;
        var expected = (BubbleColor)expectedValue;
        var (presenter, delay, _) = Create();
        using (presenter) {
            presenter.Accept(status, "detail");

            Assert.NotNull(presenter.Current);
            Assert.Equal(expected, presenter.Current!.Color);
            Assert.Equal(BubbleLifetime.Permanent, presenter.Current.Lifetime);
            // Permanent states never arm an expiry — they persist until the condition changes.
            Assert.Equal(0, delay.Waiting);
        }
    }

    [Theory]
    [InlineData((int)TrayStatusKind.Failure, (int)BubbleColor.Red)]
    [InlineData((int)TrayStatusKind.Special, (int)BubbleColor.Blue)]
    public async Task TransientStatesShowThenHideAfterTheLifetime(int statusValue, int expectedValue) {
        var status = (TrayStatusKind)statusValue;
        var expected = (BubbleColor)expectedValue;
        var (presenter, delay, _) = Create();
        using (presenter) {
            presenter.Accept(status, "detail");

            Assert.Equal(expected, presenter.Current!.Color);
            Assert.Equal(BubbleLifetime.Transient, presenter.Current.Lifetime);
            Assert.Equal(1, delay.Waiting);
            Assert.Equal(StatusBubblePresenter.TransientLifetime, Assert.Single(delay.Requested));

            delay.CompleteAll();

            await WaitUntilAsync(() => presenter.Current is null);
            Assert.Null(presenter.Current);
        }
    }

    [Fact]
    public async Task JustDisconnectedShowsRedBrieflyUsingTheLastConnectionDetail() {
        // The aggregate collapses straight from connected to idle, so the drop episode is the
        // only thing that can produce the red the owner asked for.
        var (presenter, delay, _) = Create();
        using (presenter) {
            presenter.Accept(TrayStatusKind.Connected, "Connected: Galaxy S24 (Wi-Fi)");
            Assert.Equal(BubbleColor.Green, presenter.Current!.Color);

            presenter.Accept(TrayStatusKind.Idle, "No ADB devices");

            Assert.NotNull(presenter.Current);
            Assert.Equal(BubbleColor.Red, presenter.Current!.Color);
            Assert.Equal(BubbleLifetime.Transient, presenter.Current.Lifetime);
            Assert.Equal("Connected: Galaxy S24 (Wi-Fi)", presenter.Current.Detail);

            delay.CompleteAll();
            await WaitUntilAsync(() => presenter.Current is null);
        }
    }

    [Fact]
    public async Task APermanentStateSupersedesAPendingTransient() {
        var (presenter, delay, _) = Create();
        using (presenter) {
            presenter.Accept(TrayStatusKind.Failure, "ADB unavailable");
            Assert.Equal(BubbleLifetime.Transient, presenter.Current!.Lifetime);

            presenter.Accept(TrayStatusKind.Connected, "Connected: Phone (USB)");
            Assert.Equal(BubbleColor.Green, presenter.Current!.Color);
            Assert.Equal(BubbleLifetime.Permanent, presenter.Current.Lifetime);

            // The superseded wait completing must not hide the now-permanent bubble.
            delay.CompleteAll();
            await Task.Delay(80);
            Assert.NotNull(presenter.Current);
            Assert.Equal(BubbleColor.Green, presenter.Current!.Color);
        }
    }

    [Fact]
    public async Task ReenteringTheSameTransientDoesNotDoubleArmButRestartingRetargets() {
        var (presenter, delay, _) = Create();
        using (presenter) {
            presenter.Accept(TrayStatusKind.Special, "Bootloader");
            Assert.Equal(1, delay.Waiting);

            // Same transient again: still one pending expiry, not two.
            presenter.Accept(TrayStatusKind.Special, "Bootloader");
            Assert.Equal(1, delay.Waiting);

            // A different transient retargets: the old wait is cancelled and a new one armed.
            presenter.Accept(TrayStatusKind.Failure, "ADB unavailable");
            Assert.Equal(1, delay.Waiting);
            Assert.Equal(2, delay.Requested.Length);

            delay.CompleteAll();
            await WaitUntilAsync(() => presenter.Current is null);
        }
    }

    [Fact]
    public void IdleAfterIdleIsNotADrop() {
        var (presenter, delay, _) = Create();
        using (presenter) {
            presenter.Accept(TrayStatusKind.Idle, "No ADB devices");
            presenter.Accept(TrayStatusKind.Idle, "No ADB devices");

            // Nothing was connected, so there is nothing to report and nothing to arm.
            Assert.Null(presenter.Current);
            Assert.Equal(0, delay.Waiting);
        }
    }

    [Fact]
    public async Task DisposeStopsHidingLater() {
        var (presenter, delay, changes) = Create();
        presenter.Accept(TrayStatusKind.Failure, "ADB unavailable");
        Assert.Single(changes);

        presenter.Dispose();
        Assert.Null(presenter.Current);

        delay.CompleteAll();
        await Task.Delay(80);

        // Disposal cleared state once; the cancelled wait must not publish again.
        Assert.Single(changes);
    }
}

/// <summary>
/// Decision 0024: the bubble's fade is an appear transition. A state change while the bubble is
/// already visible (colour or detail text) updates in place — re-fading from opacity 0 would
/// blink the bubble off and on for every status-text change. Under reduced motion there is no
/// fade at all.
/// </summary>
public sealed class StatusBubblePolicyTests {
    [Fact]
    public void HiddenBubbleFadesInOnAppear() {
        Assert.True(StatusBubblePolicy.ShouldFadeIn(wasVisible: false, animationsEnabled: true));
    }

    [Fact]
    public void VisibleBubbleDoesNotRefadeOnStateChange() {
        Assert.False(StatusBubblePolicy.ShouldFadeIn(wasVisible: true, animationsEnabled: true));
    }

    [Fact]
    public void ReducedMotionNeverFades() {
        Assert.False(StatusBubblePolicy.ShouldFadeIn(wasVisible: false, animationsEnabled: false));
        Assert.False(StatusBubblePolicy.ShouldFadeIn(wasVisible: true, animationsEnabled: false));
    }
}
