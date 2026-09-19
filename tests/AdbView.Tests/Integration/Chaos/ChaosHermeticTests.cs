#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable CA1849
#pragma warning disable xUnit1051

namespace AdbView.Tests.Integration.Chaos;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.Host;
using AdbView.Notifications;
using AdbView.State;
using AdbView.Tests.Fakes;
using AdbView.Tests.Notifications;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

/// <summary>
/// Phase 9 hermetic chaos: ENGINEERING.md §11 rows that need no real Explorer/WebView kill.
/// </summary>
public sealed class ChaosHermeticTests {
    private sealed class CollectingSink : ILogEventSink {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class CountingSink : INotificationSink {
        public List<NotificationIntent> Emitted { get; } = new();

        public void Emit(NotificationIntent intent) => Emitted.Add(intent);
    }

    private static RawDevice Proto(string serial, string? model = "Phone") =>
        new() {
            Serial = serial,
            ProtoState = 8,
            StateToken = null,
            ConnectionType = 1,
            Product = null,
            Model = model,
            Device = null,
            Format = RawDeviceFormat.ProtoBinary,
        };

    [Fact]
    public async Task Row26_Backlog_Warns_Once_Then_RateLimits() {
        var collect = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(collect)
            .CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try {
            var clock = new VirtualClock(NotificationFixtures.T0);
            await using var engine = new StateEngine(
                StateEngine.CreateDefaultSnapshot(revision: 1),
                getUtcNow: () => clock.Now,
                delay: clock.Delay);

            for (int i = 0; i < StateEngine.BacklogWarnThreshold * 20; i++) {
                engine.Post(new DeviceTrackerFailedEvent("chaos-flood"));
            }

            // Give the writer side a chance to observe the peak before drain catches up.
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(
                collect.Events.Exists(e =>
                    e.RenderMessage(CultureInfo.InvariantCulture).Contains("StateEngine backlog", StringComparison.Ordinal)),
                "expected backlog warning");

            int warnings = collect.Events.FindAll(e =>
                e.RenderMessage(CultureInfo.InvariantCulture).Contains("StateEngine backlog", StringComparison.Ordinal)).Count;
            for (int i = 0; i < 100; i++) {
                engine.Post(new DeviceTrackerFailedEvent("chaos-flood-2"));
            }

            await Task.Yield();
            int warningsAfter = collect.Events.FindAll(e =>
                e.RenderMessage(CultureInfo.InvariantCulture).Contains("StateEngine backlog", StringComparison.Ordinal)).Count;
            Assert.Equal(warnings, warningsAfter);

            await engine.DrainAsync();
        }
        finally {
            Log.Logger = previous;
        }
    }

    [Fact]
    public async Task Row5_Tracker_Grace_Retains_Then_Clears() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1));

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), DateTimeOffset.UtcNow));
        await engine.DrainAsync();
        Assert.Single(engine.CurrentSnapshot.Devices);

        engine.Post(new DeviceTrackerFailedEvent("stream-eof"));
        await engine.DrainAsync();
        Assert.Single(engine.CurrentSnapshot.Devices);
        long activeGen = engine.CurrentDeviceState.ActiveGraceGeneration;
        Assert.True(activeGen > 0);

        engine.Post(new DeviceTrackerGraceExpiredEvent(activeGen));
        await engine.DrainAsync();
        Assert.Empty(engine.CurrentSnapshot.Devices);
    }

    [Fact]
    public async Task Mut2_Stale_Metadata_Generation_Dropped() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1));
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), DateTimeOffset.UtcNow));
        await engine.DrainAsync();

        string id = engine.CurrentSnapshot.Devices[0].Id;
        long gen = engine.CurrentDeviceState.MetadataSessions[id].Generation;
        long rev = engine.CurrentSnapshot.Revision;

        engine.PostMetadata(gen - 1, new MetadataReceivedEvent(id, 50, "SM-X", "16"));
        await engine.DrainAsync();
        Assert.Equal(rev, engine.CurrentSnapshot.Revision);
        Assert.Null(engine.CurrentSnapshot.Devices[0].BatteryPercent);
    }

    [Fact]
    public async Task Mut8_Mdns_Outage_Does_Not_Flip_Global_Health_To_Unavailable() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1));
        engine.Post(new AdbServerStateChangedEvent(
            new AdbServerStateChanged(AdbServerPhase.Up, null, 41, 1)));
        engine.Post(new MdnsTrackerFailedEvent("mdns-down"));
        await engine.DrainAsync();

        Assert.NotEqual(GlobalHealthKind.AdbUnavailable, engine.CurrentSnapshot.Health.Kind);
    }

    [Fact]
    public void Lifecycle_Default_Factory_Wires_Without_Throwing() {
        using var life = new DeviceTrackerLifecycle(_ => { });
        using var mdns = new MdnsTrackerLifecycle(_ => { });
        Assert.False(life.IsRunning);
        Assert.False(mdns.IsRunning);
    }

    [Fact]
    public async Task Mut1_Single_Tracker_Lifecycle_Per_Generation() {
        var starts = 0;
        using var life = new DeviceTrackerLifecycle(
            _ => { },
            trackerFactory: () => {
                starts++;
                return new DeviceTracker(
                    connectionFactory: () => throw new System.IO.IOException("no socket"),
                    delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
            });

        life.OnServerStateChanged(new AdbServerStateChanged(AdbServerPhase.Up, null, 41, 7));
        life.OnServerStateChanged(new AdbServerStateChanged(AdbServerPhase.Up, null, 41, 7));
        Assert.Equal(1, starts);
        Assert.Equal(7, life.RunningGeneration);
        Assert.True(life.IsRunning);

        life.OnServerStateChanged(new AdbServerStateChanged(AdbServerPhase.Up, null, 41, 8));
        Assert.Equal(2, starts);
        Assert.Equal(8, life.RunningGeneration);
    }

    [Fact]
    public async Task Mut6_Shutdown_Suppresses_Late_Notification_Emit() {
        var clock = new VirtualClock(NotificationFixtures.T0);
        var sink = new CountingSink();
        await using var coord = new NotificationCoordinator(() => clock.Now, clock.Delay, sink);
        coord.AcceptSnapshot(NotificationFixtures.Empty(1));
        await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));

        coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithDevices(
            NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected)));
        coord.BeginShutdown();
        await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(sink.Emitted);
    }

    [Fact]
    public async Task Mut7_Resume_Rebaseline_No_Storm() {
        var clock = new VirtualClock(NotificationFixtures.T0);
        var sink = new CountingSink();
        await using var coord = new NotificationCoordinator(() => clock.Now, clock.Delay, sink);
        coord.AcceptSnapshot(NotificationFixtures.Empty(1));
        await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
        coord.AcceptSnapshot(NotificationFixtures.Empty(2).WithDevices(
            NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected)));
        await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => sink.Emitted.Count >= 1);
        sink.Emitted.Clear();

        var snap = NotificationFixtures.Empty(3).WithDevices(
            NotificationFixtures.Device("usb:R8", "Phone", DeviceStateKind.Connected),
            NotificationFixtures.Device("wifi:W", "Phone", DeviceStateKind.Connected));
        coord.RebaselineForResume(snap);
        coord.AcceptSnapshot(snap);
        await clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(sink.Emitted);
    }

    [Fact]
    public async Task Hermetic_Churn_Soak_TwoThousand_List_Cycles() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1));
        var list = ImmutableArray.Create(Proto("R8"));
        var empty = ImmutableArray<RawDevice>.Empty;

        for (int i = 0; i < 2000; i++) {
            engine.Post(new DeviceListReceivedEvent(i % 2 == 0 ? list : empty, DateTimeOffset.UtcNow));
            if (i % 50 == 0) {
                await engine.DrainAsync();
            }
        }

        await engine.DrainAsync();
        Assert.True(engine.CurrentSnapshot.Revision >= 1);
        Assert.True(engine.PendingCount < StateEngine.BacklogWarnThreshold);
    }

    private static byte[] CreateServerStatusPayload(string version = "37.0.1") {
        var ms = new List<byte>();
        byte[] verBytes = Encoding.UTF8.GetBytes(version);
        ms.Add(0x2A);
        ms.Add((byte)verBytes.Length);
        ms.AddRange(verBytes);
        return ms.ToArray();
    }

    [Fact]
    public async Task Row1_Supervisor_Starts_Absent_Server_Then_Up() {
        await using var server = new FakeAdbServer();
        bool serverLive = false;
        server.SetHandler(async (socket, cmd) => {
            if (!serverLive) {
                socket.Close();
                return;
            }

            if (cmd == "host:version") {
                await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
            }
            else if (cmd == "host:server-status") {
                byte[] proto = CreateServerStatusPayload();
                byte[] header = Encoding.ASCII.GetBytes($"OKAY{proto.Length:X4}");
                await socket.SendAsync(header, SocketFlags.None);
                await socket.SendAsync(proto, SocketFlags.None);
            }
        });

        var runner = new ChaosRunner(() => {
            serverLive = true;
            return new ProcessResult(0, "* daemon started successfully *", string.Empty, false);
        });
        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: new ChaosLocator(Path.GetFullPath(@"C:\Android\platform-tools\adb.exe")),
            processRunner: runner,
            getUtcNow: () => new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

        var state = await supervisor.RunIterationAsync();
        Assert.Equal(AdbServerPhase.Up, state.Phase);
        Assert.Equal(1, runner.CallCount);
        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task Row3_Supervisor_Recover_After_Probe_Outage_No_Kill() {
        await using var server = new FakeAdbServer();
        bool isUp = true;
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        server.SetHandler(async (socket, cmd) => {
            if (!isUp) {
                socket.Close();
                return;
            }

            if (cmd == "host:version") {
                await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
            }
            else if (cmd == "host:server-status") {
                byte[] proto = CreateServerStatusPayload();
                byte[] header = Encoding.ASCII.GetBytes($"OKAY{proto.Length:X4}");
                await socket.SendAsync(header, SocketFlags.None);
                await socket.SendAsync(proto, SocketFlags.None);
            }
        });

        var supervisor = new AdbServerSupervisor(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            locator: new ChaosLocator(Path.GetFullPath(@"C:\Android\platform-tools\adb.exe")),
            processRunner: new ChaosRunner(() => new ProcessResult(0, "ok", string.Empty, false)),
            getUtcNow: () => now);

        Assert.Equal(AdbServerPhase.Up, (await supervisor.RunIterationAsync()).Phase);
        isUp = false;
        now = now.AddSeconds(100);
        Assert.Equal(AdbServerPhase.Restoring, (await supervisor.RunIterationAsync()).Phase);
        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task Row8_Mdns_Failure_Posts_Without_AdbUnavailable() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1));
        engine.Post(new AdbServerStateChangedEvent(
            new AdbServerStateChanged(AdbServerPhase.Up, null, 41, 1)));
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), DateTimeOffset.UtcNow));
        engine.Post(new MdnsTrackerFailedEvent("stream-eof"));
        await engine.DrainAsync();
        Assert.NotEqual(GlobalHealthKind.AdbUnavailable, engine.CurrentSnapshot.Health.Kind);
        Assert.Single(engine.CurrentSnapshot.Devices);
    }

    [Fact]
    public async Task Row11_Wrong_Pair_Code_Fails_Without_Logging_Code() {
        var collect = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(collect)
            .CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try {
            await using var server = new FakeAdbServer();
            server.SetHandler(async (socket, cmd) => {
                Assert.True(FakeAdbPair.TryParsePairCommand(cmd, out _));
                await FakeAdbPair.SendServiceAcceptedAsync(socket).ConfigureAwait(false);
                await FakeAdbPair.SendFailResultAsync(socket, "Failed: Wrong password").ConfigureAwait(false);
            });

            var endpoint = PairingEndpoint.TryCreate("192.168.1.10", 37100, out var ep)
                ? ep!
                : throw new InvalidOperationException("endpoint");
            var client = new HostPairClient(server.EndPoint, requestTimeout: TimeSpan.FromSeconds(3));
            var result = await client.PairAsync(endpoint, "123456", CancellationToken.None);
            Assert.False(result.Success);
            Assert.DoesNotContain(
                collect.Events,
                e => e.RenderMessage(CultureInfo.InvariantCulture).Contains("123456", StringComparison.Ordinal));
            Assert.DoesNotContain("123456", result.DiagnosticReason ?? "", StringComparison.Ordinal);
        }
        finally {
            Log.Logger = previous;
        }
    }

    private sealed class ChaosLocator(string? path) : IAdbExecutableLocator {
        public string? Locate() => path;
    }

    private sealed class ChaosRunner(Func<ProcessResult> onStart) : IAdbProcessRunner {
        public int CallCount { get; private set; }

        public Task<ProcessResult> StartServerAsync(string executablePath, CancellationToken ct = default) {
            CallCount++;
            return Task.FromResult(onStart());
        }
    }

    private static async Task WaitUntilAsync(Func<bool> pred, TimeSpan? timeout = null) {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!pred()) {
            if (DateTime.UtcNow > limit) {
                throw new TimeoutException("WaitUntilAsync timed out");
            }

            await Task.Delay(10);
        }
    }
}
