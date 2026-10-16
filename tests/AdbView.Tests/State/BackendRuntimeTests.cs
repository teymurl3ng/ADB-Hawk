namespace AdbView.Tests.State;

using System.Collections.Immutable;
using System.Threading;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.Bridge;
using AdbView.Host;
using AdbView.Notifications;
using AdbView.State;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class BackendRuntimeTests {
    [Fact]
    public async Task FixtureBackendKeepsAuthoritativePinAndSilentNotificationBaseline() {
        var initial = FixtureSnapshotProvider.GetSnapshot("connected-usb");
        using var runtime = new BackendRuntime(initial);
        var sink = new FakeNotificationSink();
        runtime.InitializeNotifications(initial, () => FixtureSnapshotProvider.T0,
            (delay, token) => Task.Delay(delay, token), sink, () => false, notificationsEnabled: false);
        Assert.Same(initial, runtime.StateEngine.CurrentSnapshot);
        Assert.Null(runtime.Supervisor);
        Assert.Null(runtime.PairingSession);
        Assert.True(runtime.NotificationCoordinator!.State.BaselineEstablished);
        Assert.Empty(sink.Emitted);

        Assert.True(runtime.StateEngine.Post(new SetPinnedEvent(true)));
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.True(runtime.StateEngine.CurrentSnapshot.Shell.Pinned);
        Assert.Equal(initial.Revision + 1, runtime.StateEngine.CurrentSnapshot.Revision);
        Assert.Empty(sink.Emitted);
    }

    [Fact]
    public void BackendShutdownCancelsLifetimeAndRejectsFurtherDomainWork() {
        using var runtime = new BackendRuntime(FixtureSnapshotProvider.GetSnapshot("empty"));
        var lifetime = runtime.LifetimeToken;
        runtime.InitializeNotifications(runtime.StateEngine.CurrentSnapshot, () => FixtureSnapshotProvider.T0,
            (delay, token) => Task.Delay(delay, token), NullNotificationSink.Instance, () => false, true);
        runtime.BeginShutdown();
        Assert.True(lifetime.IsCancellationRequested);
        Assert.True(runtime.NotificationCoordinator!.State.ShuttingDown);
        runtime.Dispose();
        Assert.False(runtime.StateEngine.Post(new SetPinnedEvent(true)));
        runtime.BeginShutdown();
    }

    [Fact]
    public async Task BackendReconciliationConnectsWithoutVisualSnapshotPublication() {
        var server = new FakeAdbServer();
        await using var ownedServer = server.ConfigureAwait(true);
        server.SetHandler(async (socket, _) => {
            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            var raw = FakeAdbMdns.EncodeMdnsService("adb-A-aa", "_adb-tls-connect._tcp", 40123, "192.0.2.10");
            var tls = FakeAdbMdns.EncodeTlsService(raw);
            await FakeAdbMdns.SendFrameAsync(socket, FakeAdbMdns.EncodeMdnsServices(tls: [tls])).ConfigureAwait(false);
        });
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeHostConnectClient((host, port) => {
            Assert.Equal("192.0.2.10", host);
            Assert.Equal(40123, port);
            connected.TrySetResult();
            return Task.FromResult(new HostConnectResult(HostConnectOutcome.Connected, null));
        });
        using var runtime = new BackendRuntime(FixtureSnapshotProvider.GetSnapshot("empty"));
        runtime.InitializeServices(
            mdnsTrackerFactory: () => new MdnsTracker(connectionFactory: () => new AdbHostConnection(server.EndPoint)),
            postPairConnectClient: client);
        runtime.StateEngine.StateReconciled += runtime.Reconcile;
        var offer = new RawMdnsService {
            InstanceName = "adb-A-aa",
            ServiceType = "_adb-tls-pairing._tcp",
            Kind = MdnsServiceKind.Pairing,
            Ipv4 = "192.0.2.10",
            Port = 40123,
            Serial = "A",
            Format = RawMdnsFormat.Fallback,
        };
        Assert.True(runtime.StateEngine.Post(new MdnsServicesReceivedEvent([offer], FixtureSnapshotProvider.T0)));
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        string offerId = Assert.Single(runtime.StateEngine.CurrentSnapshot.PairingOffers).Id;
        Assert.True(runtime.StateEngine.Post(new PairingProgressedEvent(offerId,
            AdbView.Adb.Model.PairingPhase.PairedWaitingForDevice, null, PairingFailureKind.None, "Fixture phone")));
        Assert.True(runtime.StateEngine.Post(new MdnsServicesReceivedEvent(
            ImmutableArray<RawMdnsService>.Empty, FixtureSnapshotProvider.T0)));
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        long revision = runtime.StateEngine.CurrentSnapshot.Revision;
        int published = 0;
        runtime.StateEngine.SnapshotPublished += _ => Interlocked.Increment(ref published);
        runtime.MdnsTrackerLifecycle!.EnsureRunning(1);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.Equal(revision, runtime.StateEngine.CurrentSnapshot.Revision);
        Assert.Equal(0, Volatile.Read(ref published));
        Assert.Empty(runtime.StateEngine.CurrentSnapshot.Devices);
        Assert.Equal(1, client.CallCount);
        server.AssertNoKillCommandSent();
    }
}
