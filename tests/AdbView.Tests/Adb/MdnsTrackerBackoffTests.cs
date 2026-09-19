#pragma warning disable CA1707
namespace AdbView.Tests.Adb;

using System;
using AdbView.Adb;
using Xunit;

public sealed class MdnsTrackerBackoffTests {
    [Fact]
    public void Sequence_WithZeroJitter_IsExact() {
        var backoff = new MdnsTrackerBackoff(jitterProvider: () => 0.0);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(15), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
    }

    [Fact]
    public void Reset_ReturnsToFirstStep() {
        var backoff = new MdnsTrackerBackoff(jitterProvider: () => 0.0);
        backoff.NextDelay();
        backoff.NextDelay();
        Assert.Equal(2, backoff.CurrentStep);
        backoff.Reset();
        Assert.Equal(0, backoff.CurrentStep);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
    }

    [Fact]
    public void Jitter_ClampsToTwentyPercent() {
        var plus = new MdnsTrackerBackoff(jitterProvider: () => 0.20);
        Assert.Equal(TimeSpan.FromSeconds(1.2), plus.NextDelay());

        var minus = new MdnsTrackerBackoff(jitterProvider: () => -0.20);
        Assert.Equal(TimeSpan.FromSeconds(0.8), minus.NextDelay());
    }
}
