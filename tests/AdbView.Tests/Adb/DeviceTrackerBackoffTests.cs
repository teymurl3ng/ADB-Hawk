#pragma warning disable CA1707
namespace AdbView.Tests.Adb;

using System;
using AdbView.Adb;
using Xunit;

public sealed class DeviceTrackerBackoffTests {
    [Fact]
    public void Q_Sequence_WithZeroJitter_IsExact() {
        var backoff = new DeviceTrackerBackoff(jitterProvider: () => 0.0);

        Assert.Equal(TimeSpan.FromMilliseconds(500), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(10), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
    }

    [Fact]
    public void R_Reset_ReturnsToFirstStep() {
        var backoff = new DeviceTrackerBackoff(jitterProvider: () => 0.0);
        backoff.NextDelay();
        backoff.NextDelay();
        Assert.Equal(2, backoff.CurrentStep);

        backoff.Reset();
        Assert.Equal(0, backoff.CurrentStep);
        Assert.Equal(TimeSpan.FromMilliseconds(500), backoff.NextDelay());
    }

    [Fact]
    public void Jitter_ClampsToTwentyPercent() {
        var plus = new DeviceTrackerBackoff(jitterProvider: () => 0.20);
        Assert.Equal(TimeSpan.FromMilliseconds(600), plus.NextDelay());

        var minus = new DeviceTrackerBackoff(jitterProvider: () => -0.20);
        Assert.Equal(TimeSpan.FromMilliseconds(400), minus.NextDelay());
    }
}
