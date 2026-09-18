#pragma warning disable CA1707
namespace AdbView.Tests.Bridge;

using System;
using System.Collections.Generic;
using System.Globalization;
using AdbView.Bridge;
using AdbView.State;
using Xunit;

public sealed class BridgeCoordinatorTests {
    private sealed class TestBridgeHost : IBridgeHost {
        public List<SnapshotEnvelope> SentSnapshots { get; } = new();
        public List<bool> PinnedStates { get; } = new();
        public int HideFlyoutCalls { get; private set; }
        public List<int> ResizedHeights { get; } = new();

        public void SendSnapshot(SnapshotEnvelope envelope) => SentSnapshots.Add(envelope);
        public void SetPinned(bool isPinned) => PinnedStates.Add(isPinned);
        public void HideFlyout() => HideFlyoutCalls++;
        public void ResizeFlyout(int clampedEffectiveHeight) => ResizedHeights.Add(clampedEffectiveHeight);
    }

    [Fact]
    public void InitialStateIsCorrect() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host, isPinned: false);

        Assert.False(coordinator.IsReady);
        Assert.Equal(1, coordinator.Revision);
        Assert.False(coordinator.CurrentSnapshot.Shell.Pinned);
        Assert.Equal(BridgeProtocol.Version, coordinator.CurrentSnapshot.Protocol);
        Assert.Empty(host.SentSnapshots);
    }

    [Fact]
    public void ReadyWithValidProtocolResendsSnapshotAndMarksReady() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host);

        coordinator.HandleAction(new ReadyAction { Protocol = BridgeProtocol.Version });

        Assert.True(coordinator.IsReady);
        Assert.Single(host.SentSnapshots);
        Assert.Equal(1, host.SentSnapshots[0].Snapshot.Revision);
    }

    [Fact]
    public void ReadyWithInvalidProtocolRejectsAction() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host);

        coordinator.HandleAction(new ReadyAction { Protocol = 999 });

        Assert.False(coordinator.IsReady);
        Assert.Empty(host.SentSnapshots);
    }

    [Fact]
    public void ReadySentTwiceIsIdempotent() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host);

        coordinator.HandleAction(new ReadyAction { Protocol = BridgeProtocol.Version });
        coordinator.HandleAction(new ReadyAction { Protocol = BridgeProtocol.Version });

        Assert.True(coordinator.IsReady);
        Assert.Equal(2, host.SentSnapshots.Count);
        Assert.Equal(1, coordinator.Revision);
    }

    [Fact]
    public void PinWhenUnpinnedUpdatesSnapshotAndNotifiesHost() {
        var host = new TestBridgeHost();
        BridgeCoordinator coordinator = null!;
        coordinator = new BridgeCoordinator(host, isPinned: false, onSetPinned: pinned => {
            coordinator.UpdateSnapshot(coordinator.CurrentSnapshot with {
                Revision = coordinator.Revision + 1,
                Shell = coordinator.CurrentSnapshot.Shell with { Pinned = pinned }
            });
        });

        coordinator.HandleAction(new ReadyAction { Protocol = BridgeProtocol.Version });
        host.SentSnapshots.Clear();

        coordinator.HandleAction(new PinAction());

        Assert.Equal(2, coordinator.Revision);
        Assert.True(coordinator.CurrentSnapshot.Shell.Pinned);
        Assert.Single(host.PinnedStates);
        Assert.True(host.PinnedStates[0]);
        Assert.Single(host.SentSnapshots);
        Assert.Equal(2, host.SentSnapshots[0].Snapshot.Revision);
        Assert.True(host.SentSnapshots[0].Snapshot.Shell.Pinned);
    }

    [Fact]
    public void UnpinWhenPinnedUpdatesSnapshotAndNotifiesHost() {
        var host = new TestBridgeHost();
        BridgeCoordinator coordinator = null!;
        coordinator = new BridgeCoordinator(host, isPinned: true, onSetPinned: pinned => {
            coordinator.UpdateSnapshot(coordinator.CurrentSnapshot with {
                Revision = coordinator.Revision + 1,
                Shell = coordinator.CurrentSnapshot.Shell with { Pinned = pinned }
            });
        });

        coordinator.HandleAction(new ReadyAction { Protocol = BridgeProtocol.Version });
        host.SentSnapshots.Clear();

        coordinator.HandleAction(new UnpinAction());

        Assert.Equal(2, coordinator.Revision);
        Assert.False(coordinator.CurrentSnapshot.Shell.Pinned);
        Assert.Single(host.PinnedStates);
        Assert.False(host.PinnedStates[0]);
        Assert.Single(host.SentSnapshots);
        Assert.False(host.SentSnapshots[0].Snapshot.Shell.Pinned);
    }

    [Fact]
    public void DuplicatePinIsSuppressed() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host, isPinned: true);

        coordinator.HandleAction(new PinAction());

        Assert.Equal(1, coordinator.Revision);
        Assert.Empty(host.PinnedStates);
        Assert.Empty(host.SentSnapshots);
    }

    [Fact]
    public void DuplicateUnpinIsSuppressed() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host, isPinned: false);

        coordinator.HandleAction(new UnpinAction());

        Assert.Equal(1, coordinator.Revision);
        Assert.Empty(host.PinnedStates);
        Assert.Empty(host.SentSnapshots);
    }

    [Fact]
    public void RequestDismissWhenUnpinnedHidesFlyout() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host, isPinned: false);

        coordinator.HandleAction(new RequestDismissAction());

        Assert.Equal(1, host.HideFlyoutCalls);
    }

    [Fact]
    public void RequestDismissWhenPinnedIgnoresDismiss() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host, isPinned: true);

        coordinator.HandleAction(new RequestDismissAction());

        Assert.Equal(0, host.HideFlyoutCalls);
    }

    [Theory]
    [InlineData(50.0, 120)]    // Below minimum 120
    [InlineData(120.0, 120)]   // Exact minimum
    [InlineData(345.6, 346)]   // Rounded
    [InlineData(560.0, 560)]   // Exact maximum
    [InlineData(900.0, 560)]   // Above maximum 560
    public void ReportContentSizeClampsToApprovedRange(double input, int expectedClamped) {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host);

        coordinator.HandleAction(new ReportContentSizeAction { HeightEffectivePx = input });

        Assert.Single(host.ResizedHeights);
        Assert.Equal(expectedClamped, host.ResizedHeights[0]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ReportContentSizeWithNonFiniteValuesRejectsSafely(double nonFinite) {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host);

        coordinator.HandleAction(new ReportContentSizeAction { HeightEffectivePx = nonFinite });

        Assert.Empty(host.ResizedHeights);
    }

    [Fact]
    public void HandleWebMessageWithMalformedJsonRejectsSafelyWithoutException() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host);

        coordinator.HandleWebMessage("{ invalid json! }");

        Assert.Empty(host.SentSnapshots);
    }

    [Fact]
    public void HandleWebMessageWithForwardCompatibleFieldsExecutesAction() {
        var host = new TestBridgeHost();
        BridgeCoordinator coordinator = null!;
        coordinator = new BridgeCoordinator(host, isPinned: false, onSetPinned: pinned => {
            coordinator.UpdateSnapshot(coordinator.CurrentSnapshot with {
                Revision = coordinator.Revision + 1,
                Shell = coordinator.CurrentSnapshot.Shell with { Pinned = pinned }
            });
        });

        coordinator.HandleWebMessage("{\"type\":\"pin\",\"futureProperty\":123}");

        Assert.Equal(2, coordinator.Revision);
        Assert.True(coordinator.CurrentSnapshot.Shell.Pinned);
    }

    [Fact]
    public void PairingActionsAreLoggedAndIgnoredInWave2() {
        var host = new TestBridgeHost();
        var coordinator = new BridgeCoordinator(host);

        coordinator.HandleAction(new PairStartAction { OfferId = "offer-1" });
        coordinator.HandleAction(new PairSubmitAction { OfferId = "offer-1", Code = "123456" });
        coordinator.HandleAction(new PairCancelAction { OfferId = "offer-1" });

        Assert.Equal(1, coordinator.Revision);
        Assert.Empty(host.SentSnapshots);
    }

    [Fact]
    public void NextRevision_AtMaxJsSafeInteger_ThrowsRatherThanClamping() {
        var host = new TestBridgeHost();
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: StateReducer.MaxJsSafeInteger);
        var coordinator = new BridgeCoordinator(host, initialSnapshot: initial);

        var ex = Assert.Throws<InvalidOperationException>(() => coordinator.NextRevision());
        Assert.Contains(StateReducer.MaxJsSafeInteger.ToString(CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.Equal(StateReducer.MaxJsSafeInteger, coordinator.Revision);
    }

    [Fact]
    public void NextRevision_BelowMax_IncrementsMonotonically() {
        var host = new TestBridgeHost();
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: StateReducer.MaxJsSafeInteger - 1);
        var coordinator = new BridgeCoordinator(host, initialSnapshot: initial);

        Assert.Equal(StateReducer.MaxJsSafeInteger, coordinator.NextRevision());
    }
}
