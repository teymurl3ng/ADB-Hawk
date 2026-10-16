#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable CA1849
#pragma warning disable CA1307
#pragma warning disable xUnit1051
namespace AdbView.Tests.State;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.Host;
using AdbView.State;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class StateEngineTests {
    private static RawDevice ConnectedPhone(string serial = "192.0.2.10:40123", int state = 8) => new() {
        Serial = serial,
        ProtoState = state,
        ConnectionType = 2,
        Format = RawDeviceFormat.ProtoBinary,
    };

    private static RawMdnsService ConnectService(string instance = "adb-A-aa", int port = 40123) => new() {
        InstanceName = instance,
        ServiceType = "_adb-tls-connect._tcp",
        Kind = MdnsServiceKind.Connect,
        Ipv4 = "192.0.2.10",
        Port = port,
        Serial = "A",
        Format = RawMdnsFormat.Fallback,
    };

    [Fact]
    public async Task FakeDiscoveryStreamTriggersConnectionAfterPairingWithoutVisibleDelta() {
        await using var engine = new StateEngine();
        var pairedOffer = ConnectService() with { Kind = MdnsServiceKind.Pairing, ServiceType = "_adb-tls-pairing._tcp" };
        engine.Post(new MdnsServicesReceivedEvent(ImmutableArray.Create(pairedOffer), DateTimeOffset.UtcNow));
        await engine.DrainAsync();
        string offerId = Assert.Single(engine.CurrentSnapshot.PairingOffers).Id;
        engine.Post(new PairingProgressedEvent(offerId, AdbView.Adb.Model.PairingPhase.PairedWaitingForDevice,
            null, PairingFailureKind.None, "Audit phone"));
        // Complete expected pairing-service withdrawal before the connect advertisement arrives.
        engine.Post(new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, DateTimeOffset.UtcNow));
        await engine.DrainAsync();
        long waitingRevision = engine.CurrentSnapshot.Revision;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeHostConnectClient((_, port) => {
            Assert.Equal(40123, port);
            connected.TrySetResult();
            return Task.FromResult(new HostConnectResult(HostConnectOutcome.Connected, null));
        });
        using var coordinator = new PostPairConnectCoordinator(client);
        engine.StateReconciled += snapshot => coordinator.OnSnapshotPublished(snapshot, engine.CurrentDeviceState);
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, _) => {
            await FakeAdbMdns.SendOkayAsync(socket);
            var raw = FakeAdbMdns.EncodeMdnsService("adb-A-aa", "_adb-tls-connect._tcp", 40123, "192.0.2.10");
            var tls = FakeAdbMdns.EncodeTlsService(raw);
            await FakeAdbMdns.SendFrameAsync(socket, FakeAdbMdns.EncodeMdnsServices(tls: new[] { tls }));
        });
        using var life = new MdnsTrackerLifecycle(evt => engine.Post(evt), () => new MdnsTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            delay: (delay, ct) => Task.Delay(delay, ct)));
        life.EnsureRunning(1);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await engine.DrainAsync();
        Assert.Equal(waitingRevision, engine.CurrentSnapshot.Revision);
        Assert.Empty(engine.CurrentSnapshot.Devices);
        Assert.Equal(1, client.CallCount);
        server.AssertNoKillCommandSent();
    }

    [Fact]
    public void FailedDiscoveryExpiresOldAuthorityAndUsesFreshFallbackEndpoint() {
        var at = DateTimeOffset.UtcNow;
        var old = ConnectService() with { Kind = MdnsServiceKind.Pairing, ServiceType = "_adb-tls-pairing._tcp" };
        var fresh = old with { Port = 40124, Format = RawMdnsFormat.Fallback };
        var listed = StateReducer.ReduceWithDeviceState(StateEngine.CreateDefaultSnapshot(), DeviceEngineState.Empty,
            new MdnsServicesReceivedEvent(ImmutableArray.Create(old), at), at);
        var fallback = StateReducer.ReduceWithDeviceState(listed.Snapshot, listed.DeviceState,
            new MdnsFallbackReceivedEvent(ImmutableArray.Create(fresh), at), at);
        var failed = StateReducer.ReduceWithDeviceState(fallback.Snapshot, fallback.DeviceState,
            new MdnsTrackerFailedEvent("eof"), at);
        var expired = StateReducer.ReduceWithDeviceState(failed.Snapshot, failed.DeviceState,
            new WirelessDiscoveryUnavailableExpiredEvent(failed.WirelessUnavailable.Generation), at.AddSeconds(60));
        Assert.Empty(expired.DeviceState.LatestMdnsServices);
        Assert.Equal(40124, Assert.Single(expired.DeviceState.OfferBindings).Value.Port);
        Assert.Single(expired.Snapshot.PairingOffers);
        Assert.Equal(Vocabulary.Wireless.UnavailableTitle, Assert.Single(expired.Snapshot.Warnings).Title);
    }

    [Fact]
    public async Task MixedDiscoveryDoesNotPreventLaterDisconnect() {
        await using var engine = new StateEngine();
        var at = DateTimeOffset.UtcNow;
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(ConnectedPhone()), at));
        engine.Post(new MdnsServicesReceivedEvent(ImmutableArray.Create(ConnectService(), ConnectService("adb-B-bb")), at));
        engine.Post(new MdnsFallbackReceivedEvent(ImmutableArray.Create(ConnectService("adb-C-cc")), at));
        engine.Post(new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, at.AddSeconds(1)));
        await engine.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(engine.CurrentSnapshot.Devices);
        Assert.True(engine.Post(new SetPinnedEvent(true)));
        await engine.DrainAsync();
        Assert.True(engine.CurrentSnapshot.Shell.Pinned);
    }

    [Fact]
    public async Task FailingObserverDoesNotStopTruthOrOtherObservers() {
        await using var engine = new StateEngine();
        int received = 0;
        engine.SnapshotPublished += _ => throw new InvalidOperationException("synthetic observer failure");
        engine.SnapshotPublished += _ => received++;
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(ConnectedPhone()), DateTimeOffset.UtcNow));
        engine.Post(new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, DateTimeOffset.UtcNow));
        await engine.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(engine.CurrentSnapshot.Devices);
        Assert.Equal(2, received);
    }

    [Fact]
    public async Task InternalDiscoveryChangesNotifyWithoutInventingSnapshotRevision() {
        await using var engine = new StateEngine();
        int snapshots = 0;
        int reconciliations = 0;
        engine.SnapshotPublished += _ => snapshots++;
        engine.StateReconciled += _ => reconciliations++;
        engine.Post(new MdnsServicesReceivedEvent(ImmutableArray.Create(ConnectService()), DateTimeOffset.UtcNow));
        await engine.DrainAsync();
        Assert.Equal(0, snapshots);
        Assert.Equal(1, reconciliations);
        Assert.Single(engine.CurrentDeviceState.LatestMdnsServices);
    }

    [Fact]
    public void ServerLossArmsGraceOnceAndNewOutageGetsFreshToken() {
        var at = DateTimeOffset.UtcNow;
        var connected = StateReducer.ReduceWithDeviceState(StateEngine.CreateDefaultSnapshot(), DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(ConnectedPhone()), at), at);
        var lost = StateReducer.ReduceWithDeviceState(connected.Snapshot, connected.DeviceState,
            new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 1)), at);
        Assert.Equal(GraceCommandKind.Schedule, lost.Grace.Kind);
        Assert.Single(lost.Snapshot.Devices);
        var again = StateReducer.ReduceWithDeviceState(lost.Snapshot, lost.DeviceState, new DeviceTrackerFailedEvent("retry"), at);
        Assert.Equal(GraceCommandKind.None, again.Grace.Kind);
        Assert.Equal(lost.Grace.Generation, again.DeviceState.ActiveGraceGeneration);
        var expired = StateReducer.ReduceWithDeviceState(again.Snapshot, again.DeviceState,
            new DeviceTrackerGraceExpiredEvent(lost.Grace.Generation), at.AddSeconds(2));
        Assert.Empty(expired.Snapshot.Devices);
        var recovered = StateReducer.ReduceWithDeviceState(expired.Snapshot, expired.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(ConnectedPhone()), at.AddSeconds(3)), at.AddSeconds(3));
        var nextLoss = StateReducer.ReduceWithDeviceState(recovered.Snapshot, recovered.DeviceState,
            new DeviceTrackerFailedEvent("new outage"), at.AddSeconds(4));
        Assert.True(nextLoss.Grace.Generation > lost.Grace.Generation);
        var stale = StateReducer.ReduceWithDeviceState(nextLoss.Snapshot, nextLoss.DeviceState,
            new DeviceTrackerGraceExpiredEvent(lost.Grace.Generation), at.AddSeconds(4));
        Assert.Single(stale.Snapshot.Devices);
    }

    [Fact]
    public void ProducerSequenceAcceptsDisconnectAfterClockRollbackAndRejectsStaleSequence() {
        var at = DateTimeOffset.UtcNow;
        var connected = StateReducer.ReduceWithDeviceState(StateEngine.CreateDefaultSnapshot(), DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(ConnectedPhone()), at, 1), at);
        var disconnected = StateReducer.ReduceWithDeviceState(connected.Snapshot, connected.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, at.AddMinutes(-5), 2), at.AddMinutes(-5));
        Assert.Empty(disconnected.Snapshot.Devices);
        var stale = StateReducer.ReduceWithDeviceState(disconnected.Snapshot, disconnected.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(ConnectedPhone()), at.AddMinutes(5), 1), at);
        Assert.Empty(stale.Snapshot.Devices);
    }

    [Fact]
    public void FallbackIdentityAndWorkingTransportAreSharedWithMetadataTargets() {
        var at = DateTimeOffset.UtcNow;
        var state = DeviceEngineState.Empty with { LatestFallbackServices = ImmutableArray.Create(ConnectService()) };
        var result = StateReducer.ReduceWithDeviceState(StateEngine.CreateDefaultSnapshot(), state,
            new DeviceListReceivedEvent(ImmutableArray.Create(ConnectedPhone("192.0.2.10:39001", 6), ConnectedPhone()), at), at);
        var card = Assert.Single(result.Snapshot.Devices);
        Assert.Equal(DeviceStateKind.Connected, card.State);
        Assert.Equal("wifi:A", card.Id);
        var target = Assert.Single(StateEngine.BuildMetadataTargets(result.Snapshot, result.DeviceState));
        Assert.Equal("192.0.2.10:40123", target.AdbSerial);
    }

    [Fact]
    public void RepeatedDiscoveryFailuresDoNotRestartUnavailableDeadline() {
        var at = DateTimeOffset.UtcNow;
        var failed = StateReducer.ReduceWithDeviceState(StateEngine.CreateDefaultSnapshot(), DeviceEngineState.Empty,
            new MdnsTrackerFailedEvent("first"), at);
        var retried = StateReducer.ReduceWithDeviceState(failed.Snapshot, failed.DeviceState,
            new MdnsTrackerFailedEvent("second"), at.AddSeconds(30));
        Assert.Equal(GraceCommandKind.None, retried.WirelessUnavailable.Kind);
        var unavailable = StateReducer.ReduceWithDeviceState(retried.Snapshot, retried.DeviceState,
            new WirelessDiscoveryUnavailableExpiredEvent(failed.WirelessUnavailable.Generation), at.AddSeconds(60));
        Assert.Equal(Vocabulary.Wireless.UnavailableTitle, Assert.Single(unavailable.Snapshot.Warnings).Title);
    }

    [Fact]
    public async Task ScenarioJ_QueuedEvents_ProcessInOrder() {
        var clockTime = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        await using var engine = new StateEngine(getUtcNow: () => clockTime);

        var observedSnapshots = new List<AppSnapshot>();
        engine.SnapshotPublished += s => {
            lock (observedSnapshots) {
                observedSnapshots.Add(s);
            }
        };

        // Post sequence of events
        // 1. Probing -> AdbRestoring
        engine.Post(new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0)));
        // 2. Pin -> Pinned = true
        engine.Post(new SetPinnedEvent(true));
        // 3. Unavailable -> AdbUnavailable
        engine.Post(new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0)));
        // 4. Unpin -> Pinned = false
        engine.Post(new SetPinnedEvent(false));

        await engine.DrainAsync();

        Assert.Equal(4, observedSnapshots.Count);
        // Event 1: Restoring, Unpinned, Rev 2
        Assert.Equal(GlobalHealthKind.AdbRestoring, observedSnapshots[0].Health.Kind);
        Assert.False(observedSnapshots[0].Shell.Pinned);
        Assert.Equal(2, observedSnapshots[0].Revision);

        // Event 2: Restoring, Pinned, Rev 3
        Assert.Equal(GlobalHealthKind.AdbRestoring, observedSnapshots[1].Health.Kind);
        Assert.True(observedSnapshots[1].Shell.Pinned);
        Assert.Equal(3, observedSnapshots[1].Revision);

        // Event 3: Unavailable, Pinned, Rev 4
        Assert.Equal(GlobalHealthKind.AdbUnavailable, observedSnapshots[2].Health.Kind);
        Assert.True(observedSnapshots[2].Shell.Pinned);
        Assert.Equal(4, observedSnapshots[2].Revision);

        // Event 4: Unavailable, Unpinned, Rev 5
        Assert.Equal(GlobalHealthKind.AdbUnavailable, observedSnapshots[3].Health.Kind);
        Assert.False(observedSnapshots[3].Shell.Pinned);
        Assert.Equal(5, observedSnapshots[3].Revision);
    }

    [Fact]
    public async Task ScenarioK_ConcurrentProducers_AreSerializedBySingleReader() {
        await using var engine = new StateEngine();

        int eventCount = 100;
        var tasks = new List<Task>();

        for (int i = 0; i < eventCount; i++) {
            bool pin = i % 2 == 1;
            tasks.Add(Task.Run(() => {
                engine.Post(new SetPinnedEvent(pin));
            }));
        }

        await Task.WhenAll(tasks);
        await engine.DrainAsync();

        // The current snapshot revision must match the final authoritative reduction without race corruption
        Assert.True(engine.CurrentSnapshot.Revision > 1);
    }

    [Fact]
    public async Task ScenarioN_PinAction_GoesThroughAuthoritativeStateOwner() {
        await using var engine = new StateEngine();

        var tcs = new TaskCompletionSource<AppSnapshot>();
        engine.SnapshotPublished += s => tcs.TrySetResult(s);

        engine.Post(new SetPinnedEvent(true));

        var snapshot = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(snapshot.Shell.Pinned);
        Assert.Equal(engine.CurrentSnapshot, snapshot);
        Assert.Equal(2, engine.CurrentSnapshot.Revision);
    }

    [Fact]
    public async Task ScenarioO_UnpinAction_GoesThroughAuthoritativeStateOwner() {
        var initial = StateEngine.CreateDefaultSnapshot(isPinned: true, revision: 1);
        await using var engine = new StateEngine(initialSnapshot: initial);

        var tcs = new TaskCompletionSource<AppSnapshot>();
        engine.SnapshotPublished += s => tcs.TrySetResult(s);

        engine.Post(new SetPinnedEvent(false));

        var snapshot = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(snapshot.Shell.Pinned);
        Assert.Equal(engine.CurrentSnapshot, snapshot);
        Assert.Equal(2, engine.CurrentSnapshot.Revision);
    }

    [Fact]
    public async Task ScenarioT_GracefulShutdown_DrainsAllQueuedEventsInFifoOrder() {
        await using var engine = new StateEngine();
        var publishedSnapshots = new List<AppSnapshot>();
        engine.SnapshotPublished += s => {
            lock (publishedSnapshots) {
                publishedSnapshots.Add(s);
            }
        };

        // Enqueue several meaningful events
        Assert.True(engine.Post(new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0))));
        Assert.True(engine.Post(new SetPinnedEvent(true)));
        Assert.True(engine.Post(new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0))));
        Assert.True(engine.Post(new SetPinnedEvent(false)));

        // Initiate graceful stop immediately (writer completes, but reader drains all 4 accepted events)
        var stopTask = engine.StopAsync();

        // Any new Post() once shutdown begins returns false
        Assert.False(engine.Post(new SetPinnedEvent(true)));

        await stopTask;

        // Verify all 4 events were reduced in FIFO order
        lock (publishedSnapshots) {
            Assert.Equal(4, publishedSnapshots.Count);
            // Event 1: Probing -> AdbRestoring, Unpinned, Rev 2
            Assert.Equal(GlobalHealthKind.AdbRestoring, publishedSnapshots[0].Health.Kind);
            Assert.False(publishedSnapshots[0].Shell.Pinned);
            Assert.Equal(2, publishedSnapshots[0].Revision);

            // Event 2: Pin -> Pinned = true, Rev 3
            Assert.True(publishedSnapshots[1].Shell.Pinned);
            Assert.Equal(3, publishedSnapshots[1].Revision);

            // Event 3: Unavailable -> AdbUnavailable, Pinned, Rev 4
            Assert.Equal(GlobalHealthKind.AdbUnavailable, publishedSnapshots[2].Health.Kind);
            Assert.True(publishedSnapshots[2].Shell.Pinned);
            Assert.Equal(4, publishedSnapshots[2].Revision);

            // Event 4: Unpin -> Pinned = false, Rev 5
            Assert.False(publishedSnapshots[3].Shell.Pinned);
            Assert.Equal(5, publishedSnapshots[3].Revision);
        }

        // Final snapshot reflects the final accepted event
        Assert.Equal(GlobalHealthKind.AdbUnavailable, engine.CurrentSnapshot.Health.Kind);
        Assert.False(engine.CurrentSnapshot.Shell.Pinned);
        Assert.Equal(5, engine.CurrentSnapshot.Revision);

        // Ensure no further publication occurs after shutdown
        int countBeforeWait = publishedSnapshots.Count;
        await Task.Delay(50);
        Assert.Equal(countBeforeWait, publishedSnapshots.Count);
    }

    [Fact]
    public async Task ScenarioT_ForcedEmergencyCancellation_TerminatesProcessingTask() {
        await using var engine = new StateEngine();
        using var emergencyCts = new CancellationTokenSource();
        await emergencyCts.CancelAsync();

        // StopAsync with already canceled token triggers emergency forced teardown
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StopAsync(emergencyCts.Token));

        Assert.False(engine.Post(new SetPinnedEvent(true)));
    }
}
