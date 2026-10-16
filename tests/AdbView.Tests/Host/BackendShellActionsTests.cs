namespace AdbView.Tests.Host;

using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Bridge;
using AdbView.Host;
using AdbView.State;
using Xunit;

public sealed class BackendShellActionsTests {
    [Fact]
    public async Task AdapterUsesExistingSessionAndLateCancellationCannotCreateConnectedTruth() {
        var pair = new DelayedPairClient();
        using var runtime = new BackendRuntime(FixtureSnapshotProvider.GetSnapshot("empty"));
        runtime.InitializeServices(pairingCoordinator: new PairingCoordinator(pairClient: pair));
        var actions = new BackendShellActions(runtime, () => throw new InvalidOperationException("Unexpected check"),
            _ => throw new InvalidOperationException("Unexpected repair"));
        var offer = new RawMdnsService {
            InstanceName = "adb-fixture-aa",
            ServiceType = "_adb-tls-pairing._tcp",
            Kind = MdnsServiceKind.Pairing,
            Ipv4 = "192.0.2.10",
            Port = 40123,
            Serial = "fixture",
            Format = RawMdnsFormat.Fallback,
        };
        Assert.True(runtime.StateEngine.Post(new MdnsServicesReceivedEvent([offer], FixtureSnapshotProvider.T0)));
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        string id = Assert.Single(runtime.StateEngine.CurrentSnapshot.PairingOffers).Id;
        actions.StartPairing("obsolete");
        Assert.Null(runtime.PairingSession!.Coordinator.ActiveOfferId);
        actions.StartPairing(id);
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.Equal(AdbView.State.PairingPhase.AwaitingCode, runtime.StateEngine.CurrentSnapshot.Pairing!.Phase);
        actions.SubmitPairing("obsolete", "123456");
        Assert.Equal(0, pair.Calls);
        actions.SubmitPairing(id, "123456");
        await pair.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.Equal(AdbView.State.PairingPhase.Pairing, runtime.StateEngine.CurrentSnapshot.Pairing!.Phase);
        actions.CancelPairing("obsolete");
        Assert.Equal(id, runtime.PairingSession.Coordinator.ActiveOfferId);
        actions.CancelPairing(id);
        pair.Complete.SetResult(new HostPairResult(true, PairingFailureKind.None, null));
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.Null(runtime.StateEngine.CurrentSnapshot.Pairing);
        Assert.Empty(runtime.StateEngine.CurrentSnapshot.Devices);
        Assert.Equal(AdbView.Adb.Model.PairingPhase.Cleared, runtime.PairingSession.Coordinator.CurrentPhase);
    }

    [Fact]
    public async Task DiagnosisDelegatesRevalidateCurrentSnapshotIncludingOutage() {
        AppSnapshot initial = FixtureSnapshotProvider.GetSnapshot("discovery-health");
        using var runtime = new BackendRuntime(initial);
        int checks = 0;
        string? repaired = null;
        var actions = new BackendShellActions(runtime, () => checks++, id => repaired = id);
        actions.CheckDiscovery(); actions.RepairDiscovery("obsolete");
        Assert.Equal(1, checks); Assert.Null(repaired);
        string id = initial.DiscoveryHealth!.Repairs[0].Id;
        actions.RepairDiscovery(id); Assert.Equal(id, repaired);
        Assert.True(runtime.StateEngine.Post(new AdbServerStateChangedEvent(new AdbServerStateChanged(
            AdbServerPhase.Unavailable, null, 0, 1, Reason: "fixture outage"))));
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        repaired = null;
        actions.CheckDiscovery(); actions.RepairDiscovery(id);
        Assert.Equal(1, checks); Assert.Null(repaired);
    }

    private sealed class DelayedPairClient : IHostPairClient {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HostPairResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<HostPairResult> PairAsync(PairingEndpoint endpoint, string code, CancellationToken ct) {
            Calls++; Started.SetResult();
            return Complete.Task;
        }
    }
}
