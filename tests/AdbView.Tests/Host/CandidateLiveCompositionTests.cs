namespace AdbView.Tests.Host;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.Bridge;
using AdbView.Host;
using AdbView.NativeUi.Shell;
using AdbView.Notifications;
using AdbView.Presentation;
using AdbView.State;
using AdbView.Tests.Fakes;
using AdbView.WinUiMigration.Host;
using Xunit;

public sealed class CandidateLiveCompositionTests {
    [Fact]
    public void LiveClockUsesActualClockAndFixtureClockRemainsDeterministic() {
        DateTimeOffset now = FixtureSnapshotProvider.T0.AddDays(30);
        DateTimeOffset since = now.AddMinutes(-3);
        Assert.Equal("Connected 3m ago", AgeFormatter.Format("Connected {age}", since,
            CandidateClock.Read(true, TimeSpan.FromSeconds(1), () => now)));
        Assert.Equal(FixtureSnapshotProvider.T0.AddSeconds(1), CandidateClock.Read(false, TimeSpan.FromSeconds(1),
            () => throw new InvalidOperationException("Fixture must not read the live clock.")));
    }

    [Fact]
    public void LiveOptionsExcludeAllFixtureAndStartupInputs() {
        CandidateOptions options = CandidateOptions.Parse(["--live"]);
        Assert.True(options.Live); Assert.Equal(CandidateOptions.LiveIdentity, options.InstanceName);
        Assert.NotEqual(CandidateOptions.IdentityPrefix, options.InstanceName); Assert.Null(options.ResultPath);
        foreach (string argument in new[] { "--live", "--fixture=empty", "--instance=" + Guid.NewGuid().ToString("N"),
            "--verify=" + Path.GetFullPath("result.json") }) {
            Assert.Throws<ArgumentException>(() => CandidateOptions.Parse(["--live", argument]));
        }
        CandidateOptions fixture = CandidateOptions.Parse(["--fixture=empty", "--instance=" + Guid.NewGuid().ToString("N")]);
        Assert.False(fixture.Live); Assert.StartsWith(CandidateOptions.IdentityPrefix, fixture.InstanceName, StringComparison.Ordinal);
        Assert.False(options.StartupSilent);
        Assert.True(CandidateOptions.Parse(["--live", "--startup"]).StartupSilent);
    }

    [Fact]
    public async Task RealBackendBurstReachesOneMailboxSlotBeforeAnyUiWorkRuns() {
        using var runtime = new BackendRuntime(FixtureSnapshotProvider.GetSnapshot("empty"));
        var queue = new ConcurrentQueue<Action>();
        var rendered = new List<AppSnapshot>();
        using var mailbox = new SnapshotMailbox(action => { queue.Enqueue(action); return true; }, rendered.Add);
        using var session = Session(runtime, mailbox);
        for (int index = 0; index < 1000; index++) { Assert.True(runtime.StateEngine.Post(new SetPinnedEvent(index % 2 == 0))); }
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.Empty(rendered); Assert.Single(queue);
        Assert.True(queue.TryDequeue(out var drain)); drain();
        AppSnapshot latest = Assert.Single(rendered);
        Assert.Same(runtime.StateEngine.CurrentSnapshot, latest);
        Assert.Equal(1001, latest.Revision); Assert.False(latest.Shell.Pinned); Assert.Empty(queue);
    }

    [Fact]
    public async Task PinAndAppearanceRoundTripThroughTheReducerBeforeRendering() {
        using var runtime = new BackendRuntime(FixtureSnapshotProvider.GetSnapshot("empty"));
        var queue = new ConcurrentQueue<Action>();
        AppSnapshot? rendered = null;
        using var mailbox = new SnapshotMailbox(action => { queue.Enqueue(action); return true; }, snapshot => rendered = snapshot);
        using var session = Session(runtime, mailbox);
        session.RequestPin();
        session.PostAppearance(new ShellAppearanceChangedEvent(ThemeKind.Light, false, true, true));
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.Null(rendered); Assert.True(runtime.StateEngine.CurrentSnapshot.Shell.Pinned);
        Assert.True(queue.TryDequeue(out var drain)); drain();
        Assert.NotNull(rendered); Assert.True(rendered.Shell.Pinned); Assert.Equal(ThemeKind.Light, rendered.Shell.Theme);
        Assert.True(rendered.Shell.ForcedColors); Assert.True(rendered.Shell.ReducedMotion);
    }

    [Fact]
    public async Task MailboxDispatchFailureReachesTheLiveFatalBoundary() {
        using var runtime = new BackendRuntime(FixtureSnapshotProvider.GetSnapshot("empty"));
        using var mailbox = new SnapshotMailbox(_ => false, _ => throw new InvalidOperationException("Must never render."));
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new LiveBackendSession(mailbox, NullNotificationSink.Instance, exception => failed.TrySetResult(exception),
            runtime, _ => { /* Deliberately no services or external I/O in this test. */ });
        session.RequestPin();
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Exception failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.False(mailbox.Publish(runtime.StateEngine.CurrentSnapshot));
    }

    [Fact]
    public void NotificationMasterStateIsAppliedBeforeBackendStartAndShutdownIsOwned() {
        using var runtime = new BackendRuntime(FixtureSnapshotProvider.GetSnapshot("empty"));
        using var mailbox = new SnapshotMailbox(_ => true, _ => { });
        using var session = new LiveBackendSession(mailbox, NullNotificationSink.Instance, UnexpectedFailure,
            runtime, _ => { /* No socket services. */ }, notificationsEnabled: false);
        Assert.False(runtime.NotificationCoordinator!.NotificationsEnabled);
        session.SetNotificationsEnabled(true); Assert.True(runtime.NotificationCoordinator.NotificationsEnabled);
        session.SetNotificationsEnabled(false); Assert.False(runtime.NotificationCoordinator.NotificationsEnabled);
        var lifetime = runtime.LifetimeToken;
        session.Start(); session.Start(); session.Dispose(); session.Dispose();
        Assert.True(lifetime.IsCancellationRequested); Assert.True(runtime.NotificationCoordinator.State.ShuttingDown);
        Assert.False(runtime.StateEngine.Post(new SetPinnedEvent(true)));
        Assert.Throws<ObjectDisposedException>(session.Start);
    }

    [Fact]
    public void NotificationIntentsAreLosslessAndPendingCallbacksCannotOutliveDisposal() {
        var queue = new Queue<Action>(); var delivered = new List<NotificationIntent>();
        using var sink = new DispatchedNotificationSink(action => { queue.Enqueue(action); return true; }, delivered.Add, UnexpectedFailure);
        var first = new NotificationIntent(new NotificationKey(NotificationKind.DeviceConnected, "first"), "Title", "Body", NotificationSeverity.Info);
        var second = first with { Key = new NotificationKey(NotificationKind.DeviceDisconnected, "second") };
        sink.Emit(first); sink.Emit(second); Assert.Equal(2, queue.Count);
        queue.Dequeue()(); queue.Dequeue()(); Assert.Equal([first, second], delivered);
        sink.Emit(first); sink.Dispose(); queue.Dequeue()(); sink.Emit(second);
        Assert.Equal(2, delivered.Count); Assert.Empty(queue);
    }

    [Fact]
    public void NotificationDispatcherRejectionIsReported() {
        Exception? failure = null;
        using var sink = new DispatchedNotificationSink(_ => false,
            _ => throw new InvalidOperationException("Cannot deliver."), exception => failure = exception);
        sink.Emit(new NotificationIntent(new NotificationKey(NotificationKind.AdbUnavailable, "fixture"), "Title", "Body", NotificationSeverity.Error));
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public async Task SessionReconcilesConnectionDiscoveryEvenWithoutVisualPublication() {
        var server = new FakeAdbServer(); await using var ownedServer = server.ConfigureAwait(true);
        server.SetHandler(async (socket, _) => {
            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            var raw = FakeAdbMdns.EncodeMdnsService("adb-A-aa", "_adb-tls-connect._tcp", 40123, "192.0.2.10");
            await FakeAdbMdns.SendFrameAsync(socket, FakeAdbMdns.EncodeMdnsServices(tls: [FakeAdbMdns.EncodeTlsService(raw)])).ConfigureAwait(false);
        });
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeHostConnectClient((host, port) => {
            Assert.Equal("192.0.2.10", host); Assert.Equal(40123, port); connected.TrySetResult();
            return Task.FromResult(new HostConnectResult(HostConnectOutcome.Connected, null));
        });
        using var runtime = new BackendRuntime(FixtureSnapshotProvider.GetSnapshot("empty"));
        int queued = 0;
        using var mailbox = new SnapshotMailbox(_ => { Interlocked.Increment(ref queued); return true; }, _ => { });
        using var session = new LiveBackendSession(mailbox, NullNotificationSink.Instance, UnexpectedFailure, runtime,
            owner => owner.InitializeServices(mdnsTrackerFactory: () => new MdnsTracker(connectionFactory: () => new AdbHostConnection(server.EndPoint)),
                postPairConnectClient: client));
        var offer = new RawMdnsService {
            InstanceName = "adb-A-aa",
            ServiceType = "_adb-tls-pairing._tcp",
            Kind = MdnsServiceKind.Pairing,
            Ipv4 = "192.0.2.10",
            Port = 40123,
            Serial = "A",
            Format = RawMdnsFormat.Fallback
        };
        Assert.True(runtime.StateEngine.Post(new MdnsServicesReceivedEvent([offer], FixtureSnapshotProvider.T0)));
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        string offerId = Assert.Single(runtime.StateEngine.CurrentSnapshot.PairingOffers).Id;
        Assert.True(runtime.StateEngine.Post(new PairingProgressedEvent(offerId, AdbView.Adb.Model.PairingPhase.PairedWaitingForDevice,
            null, PairingFailureKind.None, "Fixture phone")));
        Assert.True(runtime.StateEngine.Post(new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, FixtureSnapshotProvider.T0)));
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        long revision = runtime.StateEngine.CurrentSnapshot.Revision;
        runtime.MdnsTrackerLifecycle!.EnsureRunning(1);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.Equal(revision, runtime.StateEngine.CurrentSnapshot.Revision); Assert.Empty(runtime.StateEngine.CurrentSnapshot.Devices);
        Assert.Equal(1, client.CallCount); Assert.Equal(1, Volatile.Read(ref queued)); server.AssertNoKillCommandSent();
    }

    private static LiveBackendSession Session(BackendRuntime runtime, SnapshotMailbox mailbox) =>
        new(mailbox, NullNotificationSink.Instance, UnexpectedFailure, runtime,
            _ => { /* Tests exercise StateEngine without starting real services. */ });
    private static void UnexpectedFailure(Exception exception) => throw new InvalidOperationException("Unexpected live failure.", exception);
}
