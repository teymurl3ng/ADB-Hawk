#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable CA1849
#pragma warning disable CA1307
#pragma warning disable xUnit1051
#pragma warning disable CA2000
namespace AdbView.Tests.State;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Bridge;
using AdbView.Host;
using AdbView.Notifications;
using AdbView.State;
using Xunit;

[Collection("AppHostSerial")]
public sealed class CompositionTests {
    private sealed class MockBridgeHost : IBridgeHost {
        public readonly List<SnapshotEnvelope> SentSnapshots = new();
        public readonly List<bool> PinnedStates = new();

        public void SendSnapshot(SnapshotEnvelope envelope) => SentSnapshots.Add(envelope);
        public void SetPinned(bool isPinned) => PinnedStates.Add(isPinned);
        public void HideFlyout() { }
        public void ResizeFlyout(int clampedEffectiveHeight, AdbView.Bridge.ContentSizePhase phase = AdbView.Bridge.ContentSizePhase.Live, string? layoutKey = null) { }
    }

    [Fact]
    public async Task ScenarioL_EndToEndEventFlow_SupervisorToBridgeCoordinator() {
        var mockHost = new MockBridgeHost();
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        await using var engine = new StateEngine(initial);

        var coordinator = new BridgeCoordinator(
            mockHost,
            isPinned: false,
            initialSnapshot: initial,
            onSetPinned: pinned => engine.Post(new SetPinnedEvent(pinned)));

        // WebView reports ready
        coordinator.HandleAction(new ReadyAction { Protocol = BridgeProtocol.Version });
        Assert.Single(mockHost.SentSnapshots); // initial snapshot sent

        // Wire engine to coordinator
        engine.SnapshotPublished += snapshot => coordinator.UpdateSnapshot(snapshot);

        // Emit supervisor state change
        var state = new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0);
        engine.Post(new AdbServerStateChangedEvent(state));

        await engine.DrainAsync();

        // Must have received the updated snapshot through the IPC host
        Assert.Equal(2, mockHost.SentSnapshots.Count);
        var latest = mockHost.SentSnapshots[1].Snapshot;
        Assert.Equal(GlobalHealthKind.AdbUnavailable, latest.Health.Kind);
        Assert.Equal(2, latest.Revision);
    }

    [Fact]
    public void ScenarioM_BridgeCoordinator_DoesNotIndependentlyMutateShellPinned() {
        var mockHost = new MockBridgeHost();
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: false, revision: 1);
        bool pinDispatched = false;

        var coordinator = new BridgeCoordinator(
            mockHost,
            isPinned: false,
            initialSnapshot: initial,
            onSetPinned: pinned => { pinDispatched = pinned; });

        // Handle PinAction
        coordinator.HandleAction(new PinAction());

        // Callback was dispatched
        Assert.True(pinDispatched);

        // BUT coordinator's CurrentSnapshot was NOT independently mutated!
        // It remains unpinned until authoritative StateEngine delivers an updated snapshot.
        Assert.False(coordinator.CurrentSnapshot.Shell.Pinned);
    }

    private sealed class HermeticExecutableLocator : IAdbExecutableLocator {
        public string? Locate() => null;
    }

    private sealed class HermeticProcessRunner : IAdbProcessRunner {
        public Task<ProcessResult> StartServerAsync(string executablePath, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(1, "", "Failed", false));

        public Task<ProcessResult> KillServerAsync(string executablePath, CancellationToken ct = default) =>
            throw new InvalidOperationException("Composition scenarios never kill the server.");
    }

    private AdbServerSupervisor CreateHermeticSupervisor() =>
        new(
            connectionFactory: () => throw new System.IO.IOException("Hermetic test - no sockets"),
            locator: new HermeticExecutableLocator(),
            processRunner: new HermeticProcessRunner(),
            backoff: new Backoff(jitterProvider: () => 0.0));

    [Fact]
    public void ScenarioR_FixtureMode_DoesNotStartSupervisor() {
        int factoryCallCount = 0;
        int trackerFactoryCount = 0;
        int mdnsFactoryCount = 0;
        using var host = new AppHost(
            sessionId: 54321,
            fixtureSuffix: "?fixture=empty",
            supervisorFactory: () => {
                factoryCallCount++;
                return CreateHermeticSupervisor();
            },
            bridgeHost: new MockBridgeHost(),
            headless: true,
            deviceTrackerFactory: () => {
                trackerFactoryCount++;
                return new DeviceTracker(connectionFactory: () => throw new System.IO.IOException("no"));
            },
            mdnsTrackerFactory: () => {
                mdnsFactoryCount++;
                return new MdnsTracker(connectionFactory: () => throw new System.IO.IOException("no"));
            });

        Assert.Equal(0, factoryCallCount);
        Assert.Equal(0, trackerFactoryCount);
        Assert.Equal(0, mdnsFactoryCount);
        Assert.Null(host.Supervisor);
        Assert.Null(host.DeviceTrackerLifecycle);
        Assert.Null(host.MdnsTrackerLifecycle);
        Assert.Null(host.PairingSession);
        Assert.Null(host.MetadataLifecycle);
        Assert.NotNull(host.StateEngine);
        Assert.NotNull(host.Coordinator);
        Assert.NotNull(host.NotificationCoordinator);
        Assert.False(host.UsesRealBalloonNotificationSink);
        Assert.Same(NullNotificationSink.Instance, host.NotificationSink);
    }

    [Fact]
    public async Task ScenarioS_NormalProductionComposition_StartsLifecyclePath() {
        int factoryCallCount = 0;
        using var host = new AppHost(
            sessionId: 54322,
            fixtureSuffix: "",
            supervisorFactory: () => {
                factoryCallCount++;
                return CreateHermeticSupervisor();
            },
            bridgeHost: new MockBridgeHost(),
            headless: true,
            deviceTrackerFactory: () => new DeviceTracker(
                connectionFactory: () => throw new System.IO.IOException("hermetic-no-socket")));

        Assert.Equal(1, factoryCallCount);
        Assert.NotNull(host.Supervisor);
        Assert.NotNull(host.DeviceTrackerLifecycle);
        Assert.NotNull(host.MdnsTrackerLifecycle);
        Assert.NotNull(host.PairingSession);
        Assert.NotNull(host.MetadataLifecycle);
        Assert.NotNull(host.StateEngine);
        Assert.NotNull(host.Coordinator);
        Assert.NotNull(host.NotificationCoordinator);
        Assert.False(host.UsesRealBalloonNotificationSink);
        Assert.Same(NullNotificationSink.Instance, host.NotificationSink);

        await host.Supervisor.RunIterationAsync();
        await host.StateEngine.DrainAsync();

        Assert.Equal(GlobalHealthKind.AdbRestoring, host.StateEngine.CurrentSnapshot.Health.Kind);
        Assert.False(host.DeviceTrackerLifecycle.IsRunning);
    }

    [Fact]
    public async Task ScenarioAW_OfferWithdrawal_ReleasesCoordinatorForNextOffer() {
        var t0 = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        using var host = new AppHost(
            sessionId: 54323,
            fixtureSuffix: "",
            supervisorFactory: CreateHermeticSupervisor,
            bridgeHost: new MockBridgeHost(),
            headless: true);

        var offerA = new RawMdnsService {
            InstanceName = "phone-a",
            ServiceType = MdnsServiceTypes.Pairing,
            Kind = MdnsServiceKind.Pairing,
            Ipv4 = "192.168.1.10",
            Port = 37100,
            Format = RawMdnsFormat.ProtoBinary,
        };
        host.StateEngine!.Post(new MdnsServicesReceivedEvent(ImmutableArray.Create(offerA), t0));
        await host.StateEngine.DrainAsync();
        Assert.Single(host.StateEngine.CurrentSnapshot.PairingOffers);

        host.Coordinator!.HandleAction(new PairStartAction { OfferId = offerA.DiscoveryKey });
        await host.StateEngine.DrainAsync();
        Assert.Equal(AdbView.State.PairingPhase.AwaitingCode, host.StateEngine.CurrentSnapshot.Pairing!.Phase);
        Assert.Equal(offerA.DiscoveryKey, host.StateEngine.CurrentSnapshot.Pairing.OfferId);

        // Offer A withdrawn (e.g. the phone dialog closed); a fresh offer B appears.
        var offerB = new RawMdnsService {
            InstanceName = "phone-b",
            ServiceType = MdnsServiceTypes.Pairing,
            Kind = MdnsServiceKind.Pairing,
            Ipv4 = "192.168.1.11",
            Port = 37101,
            Format = RawMdnsFormat.ProtoBinary,
        };
        host.StateEngine.Post(new MdnsServicesReceivedEvent(ImmutableArray.Create(offerB), t0.AddSeconds(1)));
        await host.StateEngine.DrainAsync();
        Assert.Null(host.StateEngine.CurrentSnapshot.Pairing);

        // Regression: pairStart for B must be accepted, not rejected as a failed op.
        host.Coordinator.HandleAction(new PairStartAction { OfferId = offerB.DiscoveryKey });
        await host.StateEngine.DrainAsync();
        var pairing = host.StateEngine.CurrentSnapshot.Pairing;
        Assert.NotNull(pairing);
        Assert.Equal(AdbView.State.PairingPhase.AwaitingCode, pairing!.Phase);
        Assert.Equal(offerB.DiscoveryKey, pairing.OfferId);
    }

    [Fact]
    public void Scenario_8C_Composition_Rules_Headless_Fixture_And_Policy_Gate() {
        Assert.True(AppHost.ShouldConstructRealBalloonSink(headless: false, isFixtureMode: false));
        Assert.False(AppHost.ShouldConstructRealBalloonSink(headless: true, isFixtureMode: false));
        Assert.False(AppHost.ShouldConstructRealBalloonSink(headless: false, isFixtureMode: true));
        Assert.False(AppHost.ShouldConstructRealBalloonSink(headless: true, isFixtureMode: true));
    }

    [Fact]
    public async Task ScenarioAN_AO_ProductionUp_StartsDeviceAndMdnsTrackers() {
        int deviceStarts = 0;
        int mdnsStarts = 0;

        using var host = new AppHost(
            sessionId: 54324,
            fixtureSuffix: "",
            supervisorFactory: () => CreateHermeticSupervisor(),
            bridgeHost: new MockBridgeHost(),
            headless: true,
            deviceTrackerFactory: () => {
                deviceStarts++;
                return new DeviceTracker(
                    connectionFactory: () => throw new System.IO.IOException("block-connect"));
            },
            mdnsTrackerFactory: () => {
                mdnsStarts++;
                return new MdnsTracker(
                    connectionFactory: () => throw new System.IO.IOException("block-connect"));
            });

        Assert.NotNull(host.DeviceTrackerLifecycle);
        Assert.NotNull(host.MdnsTrackerLifecycle);
        var up = new AdbServerStateChanged(AdbServerPhase.Up, null, 41, 7);
        host.DeviceTrackerLifecycle!.OnServerStateChanged(up);
        host.MdnsTrackerLifecycle!.OnServerStateChanged(up);
        await Task.Delay(50);
        Assert.Equal(1, deviceStarts);
        Assert.Equal(1, mdnsStarts);
        Assert.Equal(7, host.DeviceTrackerLifecycle.RunningGeneration);
        Assert.Equal(7, host.MdnsTrackerLifecycle.RunningGeneration);

        var upSame = new AdbServerStateChanged(AdbServerPhase.Up, null, 41, 7);
        host.DeviceTrackerLifecycle.OnServerStateChanged(upSame);
        host.MdnsTrackerLifecycle.OnServerStateChanged(upSame);
        await Task.Delay(20);
        Assert.Equal(1, deviceStarts);
        Assert.Equal(1, mdnsStarts);

        var upNext = new AdbServerStateChanged(AdbServerPhase.Up, null, 41, 8);
        host.DeviceTrackerLifecycle.OnServerStateChanged(upNext);
        host.MdnsTrackerLifecycle.OnServerStateChanged(upNext);
        await Task.Delay(50);
        Assert.Equal(2, deviceStarts);
        Assert.Equal(2, mdnsStarts);
        Assert.Equal(8, host.MdnsTrackerLifecycle.RunningGeneration);
    }

    [Fact]
    public async Task ScenarioAM_AP_FixtureNoTracker_ShutdownStopsLifecycle() {
        using var host = new AppHost(
            sessionId: 54325,
            fixtureSuffix: "",
            supervisorFactory: CreateHermeticSupervisor,
            bridgeHost: new MockBridgeHost(),
            headless: true,
            deviceTrackerFactory: () => new DeviceTracker(
                connectionFactory: () => throw new System.IO.IOException("x")));

        host.DeviceTrackerLifecycle!.OnServerStateChanged(
            new AdbServerStateChanged(AdbServerPhase.Up, null, 1, 3));
        await Task.Delay(30);
        Assert.True(host.DeviceTrackerLifecycle.IsRunning || host.DeviceTrackerLifecycle.RunningGeneration == 3);

        host.StateEngine!.Post(new SetPinnedEvent(true));
        await host.StateEngine.DrainAsync();
        host.Dispose();

        Assert.Null(host.DeviceTrackerLifecycle.RunningGeneration);
        Assert.True(host.StateEngine.CurrentSnapshot.Shell.Pinned);
    }

    [Fact]
    public async Task ScenarioT_GracefulShutdown_CancelsSupervisorAndDrainsStateEngine() {
        using var host = new AppHost(
            sessionId: 54323,
            fixtureSuffix: "",
            supervisorFactory: CreateHermeticSupervisor,
            bridgeHost: new MockBridgeHost(),
            headless: true);

        host.StateEngine!.Post(new SetPinnedEvent(true));
        await host.StateEngine.DrainAsync();
        host.Dispose();

        Assert.True(host.StateEngine.CurrentSnapshot.Shell.Pinned);
    }
}
