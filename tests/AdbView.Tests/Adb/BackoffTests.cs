#pragma warning disable CA1707
namespace AdbView.Tests.Adb;

using System;
using AdbView.Adb;
using Xunit;

public sealed class BackoffTests {
    [Fact]
    public void Sequence_WithZeroJitter_ReturnsFrozenSequenceAndCapsAt60Seconds() {
        var backoff = new Backoff(jitterProvider: () => 0.0);

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(10), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.NextDelay());
    }

    [Fact]
    public void Jitter_AppliesWithinPlusMinusTwentyPercent() {
        var plusTwenty = new Backoff(jitterProvider: () => 0.20);
        Assert.Equal(TimeSpan.FromSeconds(1.2), plusTwenty.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2.4), plusTwenty.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(6.0), plusTwenty.NextDelay());

        var minusTwenty = new Backoff(jitterProvider: () => -0.20);
        Assert.Equal(TimeSpan.FromSeconds(0.8), minusTwenty.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(1.6), minusTwenty.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4.0), minusTwenty.NextDelay());
    }

    [Fact]
    public void Jitter_OutOfRange_ClampsToTwentyPercent() {
        var excess = new Backoff(jitterProvider: () => 0.99);
        Assert.Equal(TimeSpan.FromSeconds(1.2), excess.NextDelay());

        var deficit = new Backoff(jitterProvider: () => -0.99);
        Assert.Equal(TimeSpan.FromSeconds(0.8), deficit.NextDelay());
    }

    [Fact]
    public void Reset_ResetsStepToZero() {
        var backoff = new Backoff(jitterProvider: () => 0.0);
        backoff.NextDelay(); // 1s
        backoff.NextDelay(); // 2s
        backoff.NextDelay(); // 5s
        Assert.Equal(3, backoff.CurrentStep);

        backoff.Reset();

        Assert.Equal(0, backoff.CurrentStep);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
    }
}
