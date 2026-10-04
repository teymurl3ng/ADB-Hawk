#pragma warning disable CA1707
namespace AdbView.Tests.Host;

using AdbView.Host;
using Xunit;

/// <summary>
/// Decision 0013's reduced-motion gate. The 2026-10-04 audit found the only production producer
/// of ShellAppearanceChangedEvent hardcoded ReducedMotion=false, which reset the gate to
/// "enabled" on every snapshot and permanently defeated OS reduced motion. ShellMotionGate is
/// the extracted decision; these tests pin its contract.
/// </summary>
public sealed class ShellMotionGateTests {
    [Fact]
    public void OsOff_ClosesTheGate() {
        var (enabled, changed) = ShellMotionGate.Apply(osAnimationsEnabled: false, currentGate: true);

        Assert.False(enabled);
        Assert.True(changed);
    }

    [Fact]
    public void OsOn_ReopensTheGate() {
        var (enabled, changed) = ShellMotionGate.Apply(osAnimationsEnabled: true, currentGate: false);

        Assert.True(enabled);
        Assert.True(changed);
    }

    [Fact]
    public void OsOn_GateAlreadyOpen_IsANoOp() {
        var (enabled, changed) = ShellMotionGate.Apply(osAnimationsEnabled: true, currentGate: true);

        Assert.True(enabled);
        Assert.False(changed);
    }

    [Fact]
    public void OsOff_GateAlreadyClosed_IsANoOp() {
        var (enabled, changed) = ShellMotionGate.Apply(osAnimationsEnabled: false, currentGate: false);

        Assert.False(enabled);
        Assert.False(changed);
    }

    /// <summary>The load-bearing invariant: the gate ALWAYS ends up equal to the OS probe — it can
    /// never lag behind or get stuck, whatever sequence of probes arrives.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void GateAlwaysConvergesToTheOsValue(bool os, bool current) {
        var (enabled, _) = ShellMotionGate.Apply(os, current);

        Assert.Equal(os, enabled);
    }
}
