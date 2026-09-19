#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable CA1849
#pragma warning disable CA1307
#pragma warning disable xUnit1051
#pragma warning disable CA2000
namespace AdbView.Tests.State;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Bridge;
using AdbView.Host;
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
        public void ResizeFlyout(int clampedEffectiveHeight) { }
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
        using var host = new AppHost(
            sessionId: 54321,
            fixtureSuffix: "?fixture=empty",
            supervisorFactory: () => {
                factoryCallCount++;
                return CreateHermeticSupervisor();
            },
            bridgeHost: new MockBridgeHost(),
            headless: true);

        Assert.Equal(0, factoryCallCount);
        Assert.Null(host.Supervisor);
        Assert.NotNull(host.StateEngine);
        Assert.NotNull(host.Coordinator);
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
            headless: true);

        Assert.Equal(1, factoryCallCount);
        Assert.NotNull(host.Supervisor);
        Assert.NotNull(host.StateEngine);
        Assert.NotNull(host.Coordinator);

        await host.Supervisor.RunIterationAsync();
        await host.StateEngine.DrainAsync();

        Assert.Equal(GlobalHealthKind.AdbRestoring, host.StateEngine.CurrentSnapshot.Health.Kind);
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
