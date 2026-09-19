#pragma warning disable CA1707

namespace AdbView.Tests.State;

using System;
using AdbView.State;
using Xunit;

public sealed class ShellAppearanceReducerTests {
    [Fact]
    public void ShellAppearance_Updates_Theme_And_Transparency() {
        var snap = StateEngine.CreateDefaultSnapshot(revision: 1);
        Assert.Equal(ThemeKind.Dark, snap.Shell.Theme);

        var result = StateReducer.ReduceWithDeviceState(
            snap,
            DeviceEngineState.Empty,
            new ShellAppearanceChangedEvent(ThemeKind.Light, true, false, false),
            DateTimeOffset.UtcNow);

        Assert.Equal(ThemeKind.Light, result.Snapshot.Shell.Theme);
        Assert.True(result.Snapshot.Shell.TransparencyEnabled);
        Assert.True(result.Snapshot.Revision > snap.Revision);
    }

    [Fact]
    public void ShellAppearance_NoOp_When_Unchanged() {
        var snap = StateEngine.CreateDefaultSnapshot(revision: 3);
        snap = snap with {
            Shell = snap.Shell with { Theme = ThemeKind.Light, TransparencyEnabled = true },
        };

        var result = StateReducer.ReduceWithDeviceState(
            snap,
            DeviceEngineState.Empty,
            new ShellAppearanceChangedEvent(ThemeKind.Light, true, false, false),
            DateTimeOffset.UtcNow);

        Assert.Equal(snap.Revision, result.Snapshot.Revision);
    }
}
