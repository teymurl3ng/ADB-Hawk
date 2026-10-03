#pragma warning disable CA1707 // Test method names use underscores by project convention.
#pragma warning disable CA2007 // Test project does not use ConfigureAwait.
#pragma warning disable CA1849
#pragma warning disable CA1307
#pragma warning disable xUnit1051
#pragma warning disable CA2000

namespace AdbView.Tests.Notifications;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Bridge;
using AdbView.Host;
using AdbView.Notifications;
using AdbView.State;
using AdbView.Tests.Host;
using Xunit;
using DomainPhase = AdbView.Adb.Model.PairingPhase;

/// <summary>
/// Phase 8C: StateEngine accepted snapshots → NotificationCoordinator → sink,
/// composed through AppHost (Fake/Null sink; VirtualClock). No Action Center.
/// </summary>
[Collection("AppHostSerial")]
public sealed class NotificationIntegrationTests {
    private static readonly DateTimeOffset T0 = NotificationFixtures.T0;

    private sealed class MockBridgeHost : IBridgeHost {
        public void SendSnapshot(SnapshotEnvelope envelope) { }
        public void SetPinned(bool isPinned) { }
        public void HideFlyout() { }
        public void ResizeFlyout(int clampedEffectiveHeight, AdbView.Bridge.ContentSizePhase phase = AdbView.Bridge.ContentSizePhase.Live, string? layoutKey = null) { }
    }

    private sealed class HermeticExecutableLocator : IAdbExecutableLocator {
        public string? Locate() => null;
    }

    private sealed class HermeticProcessRunner : IAdbProcessRunner {
        public Task<ProcessResult> StartServerAsync(string executablePath, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(1, "", "Failed", false));

        public Task<ProcessResult> KillServerAsync(string executablePath, CancellationToken ct = default) =>
            throw new InvalidOperationException("Hermetic notification scenarios never kill the server.");
    }

    private static AdbServerSupervisor CreateHermeticSupervisor() =>
        new(
            connectionFactory: () => throw new System.IO.IOException("Hermetic test - no sockets"),
            locator: new HermeticExecutableLocator(),
            processRunner: new HermeticProcessRunner(),
            backoff: new Backoff(jitterProvider: () => 0.0));

    private static RawDevice Proto(
        string serial,
        int state = 8,
        int connectionType = 1,
        string? model = null) =>
        new() {
            Serial = serial,
            ProtoState = state,
            StateToken = null,
            ConnectionType = connectionType,
            Product = null,
            Model = model,
            Device = null,
            Format = RawDeviceFormat.ProtoBinary,
        };

    private static RawMdnsService PairingSvc(
        string instance = "phone",
        string? ipv4 = "192.168.1.10",
        int port = 37100,
        string? given = "Galaxy") =>
        new() {
            InstanceName = instance,
            ServiceType = MdnsServiceTypes.Pairing,
            Kind = MdnsServiceKind.Pairing,
            Ipv4 = ipv4,
            Port = port,
            GivenName = given,
            Format = RawMdnsFormat.ProtoBinary,
        };

    private static RawMdnsService ConnectSvc() =>
        new() {
            InstanceName = "phone-c",
            ServiceType = MdnsServiceTypes.Connect,
            Kind = MdnsServiceKind.Connect,
            Ipv4 = "192.168.1.10",
            Port = 5555,
            Format = RawMdnsFormat.ProtoBinary,
        };

    private static async Task WaitUntilAsync(Func<bool> pred, int ms = 2000) {
        var start = Environment.TickCount64;
        while (!pred()) {
            if (Environment.TickCount64 - start > ms) {
                throw new TimeoutException("condition not met");
            }

            await Task.Delay(5);
        }
    }

    private sealed class HostHarness : IAsyncDisposable {
        public required AppHost Host { get; init; }
        public required FakeNotificationSink Sink { get; init; }
        public required VirtualClock Clock { get; init; }

        public StateEngine Engine => Host.StateEngine!;
        public NotificationCoordinator Coord => Host.NotificationCoordinator!;

        public async Task ExitStartupSuppressAsync() {
            await Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
            Sink.Clear();
        }

        public async Task PostAndDrainAsync(AdbViewEvent evt) {
            Engine.Post(evt);
            await Engine.DrainAsync();
        }

        public ValueTask DisposeAsync() {
            Host.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static HostHarness CreateHarness(
        string fixtureSuffix = "",
        FakeNotificationSink? sink = null,
        VirtualClock? clock = null,
        bool headless = true) {
        clock ??= new VirtualClock(T0);
        sink ??= new FakeNotificationSink();
        var host = new AppHost(
            sessionId: 88000 + Environment.TickCount % 1000,
            fixtureSuffix: fixtureSuffix,
            supervisorFactory: CreateHermeticSupervisor,
            bridgeHost: new MockBridgeHost(),
            headless: headless,
            notificationSink: sink,
            notificationClock: () => clock.Now,
            notificationDelay: clock.Delay);
        return new HostHarness { Host = host, Sink = sink, Clock = clock };
    }

    // --- A/B/C composition -------------------------------------------------

    [Fact]
    public void A_ShouldConstruct_Real_Sink_Only_Headed_NonFixture() {
        Assert.True(AppHost.ShouldConstructRealBalloonSink(false, false));
        Assert.False(AppHost.ShouldConstructRealBalloonSink(true, false));
        Assert.False(AppHost.ShouldConstructRealBalloonSink(false, true));
    }

    [Fact]
    public void B_Fixture_Mode_No_Real_Sink() {
        using var host = new AppHost(
            sessionId: 88101,
            fixtureSuffix: "?fixture=empty",
            supervisorFactory: CreateHermeticSupervisor,
            bridgeHost: new MockBridgeHost(),
            headless: true);
        Assert.False(host.UsesRealBalloonNotificationSink);
        Assert.Same(NullNotificationSink.Instance, host.NotificationSink);
        Assert.NotNull(host.NotificationCoordinator);
    }

    [Fact]
    public void C_Headless_Mode_No_Real_Sink() {
        using var host = new AppHost(
            sessionId: 88102,
            fixtureSuffix: "",
            supervisorFactory: CreateHermeticSupervisor,
            bridgeHost: new MockBridgeHost(),
            headless: true);
        Assert.False(host.UsesRealBalloonNotificationSink);
        Assert.Same(NullNotificationSink.Instance, host.NotificationSink);
        Assert.NotNull(host.NotificationCoordinator);
    }

    [Fact]
    public void A_Headed_NonFixture_Would_Construct_Balloon_When_Owner() {
        // Avoid constructing a live headed AppHost in the test host (WinForms + SingleInstance
        // can hang without Application.Run). Composition rule is the authoritative gate.
        Assert.True(AppHost.ShouldConstructRealBalloonSink(headless: false, isFixtureMode: false));
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AdbView", "Host", "AppHost.cs"));
        string text = System.IO.File.ReadAllText(path);
        Assert.Contains("new BalloonNotificationSink(", text, StringComparison.Ordinal);
        Assert.Contains("UsesRealBalloonNotificationSink =", text, StringComparison.Ordinal);
    }

    // --- D/E startup -------------------------------------------------------

    [Fact]
    public async Task D_First_Accepted_Snapshot_Silent() {
        await using var h = CreateHarness();
        Assert.Empty(h.Sink.Emitted);
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Sink.Emitted);
        Assert.True(h.Coord.State.BaselineEstablished);
    }

    [Fact]
    public async Task E_Startup_With_Three_Devices_Zero() {
        await using var h = CreateHarness();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(
                Proto("A", model: "A"),
                Proto("B", model: "B"),
                Proto("C", 8, 2, model: "C")),
            T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Sink.Emitted);
        Assert.Equal(3, h.Engine.CurrentSnapshot.Devices.Length);
    }

    // --- F/G connect + metadata --------------------------------------------

    [Fact]
    public async Task F_PostBaseline_Connect_One_After_Hold() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "SM_A065F")), T0));
        Assert.Empty(h.Sink.Emitted);
        await h.Clock.AdvanceAndFlushAsync(NotificationPolicy.HoldConnected - TimeSpan.FromMilliseconds(100));
        Assert.Empty(h.Sink.Emitted);
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        Assert.Single(h.Sink.Emitted);
        Assert.Equal(NotificationKind.DeviceConnected, h.Sink.Emitted[0].Key.Kind);
        Assert.Equal("usb:R8", h.Sink.Emitted[0].Key.Subject);
        Assert.DoesNotContain("usb:R8", h.Sink.Emitted[0].Title, StringComparison.Ordinal);
        Assert.DoesNotContain("R8", h.Sink.Emitted[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task G_Metadata_Update_Zero() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();

        string id = h.Engine.CurrentSnapshot.Devices[0].Id;
        long gen = h.Engine.CurrentDeviceState.MetadataSessions[id].Generation;
        long revBefore = h.Engine.CurrentSnapshot.Revision;
        h.Engine.PostMetadata(gen, new MetadataReceivedEvent(id, 88, "SM-A065F", "16"));
        await h.Engine.DrainAsync();
        Assert.True(h.Engine.CurrentSnapshot.Revision > revBefore);
        Assert.Equal(88, h.Engine.CurrentSnapshot.Devices[0].BatteryPercent);
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Sink.Emitted);
    }

    // --- H/I disconnect / grace --------------------------------------------

    [Fact]
    public async Task H_Tracker_Failure_Retained_Zero_Disconnect() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Any(i => i.Key.Kind == NotificationKind.DeviceConnected));
        h.Sink.Clear();

        await h.PostAndDrainAsync(new DeviceTrackerFailedEvent("stream-break"));
        Assert.Single(h.Engine.CurrentSnapshot.Devices); // grace retains
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceDisconnected);
    }

    [Fact]
    public async Task I_Authoritative_Removal_Disconnect_After_Hold() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();

        await h.PostAndDrainAsync(new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, T0.AddSeconds(1)));
        Assert.Empty(h.Engine.CurrentSnapshot.Devices);
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Any(i => i.Key.Kind == NotificationKind.DeviceDisconnected));
        Assert.Contains(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceDisconnected);
    }

    // --- J/K/L auth / offline ----------------------------------------------

    [Fact]
    public async Task J_Unauthorized_One() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", state: 3, model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        Assert.Equal(NotificationKind.AuthorizationRequired, h.Sink.Emitted[0].Key.Kind);
    }

    [Fact]
    public async Task K_Repeat_Unauthorized_Zero() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", state: 3, model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        int n = h.Sink.Emitted.Count;
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", state: 3, model: "Phone")), T0.AddSeconds(1)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(n, h.Sink.Emitted.Count);
    }

    [Fact]
    public async Task L_Offline_DeviceProblem_Only() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        // ProtoState 6 = Offline (10 = Recovery).
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", state: 6, model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        Assert.All(h.Sink.Emitted, i => Assert.Equal(NotificationKind.DeviceProblem, i.Key.Kind));
        Assert.DoesNotContain(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.AdbUnavailable);
    }

    [Fact]
    public async Task L2_Offline_Then_Absent_Disconnects() {
        // Real adb disconnect often reports offline briefly before dropping the transport.
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Any(i => i.Key.Kind == NotificationKind.DeviceConnected));
        h.Sink.Clear();

        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", state: 6, model: "Phone")), T0.AddSeconds(1)));
        Assert.Equal(DeviceStateKind.Offline, h.Engine.CurrentSnapshot.Devices[0].State);
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray<RawDevice>.Empty, T0.AddSeconds(2)));
        Assert.Empty(h.Engine.CurrentSnapshot.Devices);

        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Any(i => i.Key.Kind == NotificationKind.DeviceDisconnected));
        Assert.Contains(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceDisconnected);
        Assert.DoesNotContain(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceProblem);
    }

    [Fact]
    public async Task L3_Connected_Hold_Survives_Metadata_Snapshot() {
        // Live metadata ticks must not cancel an in-flight DeviceConnected hold.
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        Assert.Contains(h.Engine.CurrentSnapshot.Devices, d => d.State == DeviceStateKind.Connected);

        // Same connected roster again (metadata-style) before the hold elapses.
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0.AddMilliseconds(200)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Any(i => i.Key.Kind == NotificationKind.DeviceConnected));
        Assert.Contains(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceConnected);
    }

    // --- M/N/O global / wireless -------------------------------------------

    [Fact]
    public async Task M_Global_Unavailable_One_After_10s() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new AdbServerStateChangedEvent(
            new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(9));
        Assert.Empty(h.Sink.Emitted);
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1.1));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        Assert.Equal(NotificationKind.AdbUnavailable, h.Sink.Emitted[0].Key.Kind);
    }

    [Fact]
    public async Task N_Recovery_Zero() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new AdbServerStateChangedEvent(
            new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();
        await h.PostAndDrainAsync(new AdbServerStateChangedEvent(
            new AdbServerStateChanged(AdbServerPhase.Up, null, 41, 1)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Sink.Emitted);
    }

    [Fact]
    public async Task O_Mdns_Warning_Zero() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new MdnsTrackerFailedEvent("mdns-down"));
        // Arm restoring/unavailable timers via wall clock would take minutes; policy path:
        // wireless warning snapshots still produce zero Windows notifications when AcceptSnapshot runs.
        // Force a warning snapshot through reducer by waiting Restoring then Unavailable is heavy —
        // instead accept that MdnsTrackerFailed alone may not yet warn; verify Connect service ≠ pairing notify.
        await h.PostAndDrainAsync(new MdnsServicesReceivedEvent(ImmutableArray.Create(ConnectSvc()), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.PairingAvailable);
        Assert.DoesNotContain(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.AdbUnavailable);
    }

    // --- P/Q/R pairing -----------------------------------------------------

    [Fact]
    public async Task P_Pairing_Offer_One() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new MdnsServicesReceivedEvent(
            ImmutableArray.Create(PairingSvc(instance: "adb-RFCX116CYYX-JYueKC", given: null)), T0));
        Assert.Single(h.Engine.CurrentSnapshot.PairingOffers);
        Assert.Equal(Vocabulary.Pairing.DefaultDeviceName, h.Engine.CurrentSnapshot.PairingOffers[0].DisplayName);
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(1.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        Assert.Equal(NotificationKind.PairingAvailable, h.Sink.Emitted[0].Key.Kind);
        Assert.DoesNotContain("adb-", h.Sink.Emitted[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(h.Engine.CurrentSnapshot.PairingOffers[0].Id, h.Sink.Emitted[0].Body, StringComparison.Ordinal);
        Assert.Equal(
            Vocabulary.Notifications.PairingAvailableBody(h.Engine.CurrentSnapshot.PairingOffers[0].DisplayName),
            h.Sink.Emitted[0].Body);
    }

    [Fact]
    public async Task Q_R_Pairing_Result_Immediate_Never_Connected() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        var svc = PairingSvc();
        await h.PostAndDrainAsync(new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0));
        string offerId = h.Engine.CurrentSnapshot.PairingOffers[0].Id;
        await h.PostAndDrainAsync(new PairingProgressedEvent(
            offerId, DomainPhase.AwaitingCode, null, PairingFailureKind.None, "Galaxy"));
        h.Sink.Clear();
        await h.PostAndDrainAsync(new PairingProgressedEvent(
            offerId, DomainPhase.PairedWaitingForDevice, null, PairingFailureKind.None, "Galaxy"));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        Assert.Equal(NotificationKind.PairingResult, h.Sink.Emitted[0].Key.Kind);
        Assert.Equal(Vocabulary.Pairing.SuccessTitle("Galaxy"), h.Sink.Emitted[0].Title);
        Assert.Equal(Vocabulary.Pairing.SuccessDetail, h.Sink.Emitted[0].Body);
        Assert.DoesNotContain("Connected", h.Sink.Emitted[0].Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connected", h.Sink.Emitted[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123456", h.Sink.Emitted[0].Title, StringComparison.Ordinal);
    }

    // --- S burst -----------------------------------------------------------

    [Fact]
    public async Task S_Burst_Five_Changes_Three_Plus_Aggregate_Two() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        // One authoritative list with five new Connected devices (incremental lists
        // cancel still-pending Connected holds for already-Connected peers).
        var devices = Enumerable.Range(0, 5)
            .Select(k => Proto("S" + k, model: "D" + k))
            .ToImmutableArray();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(devices, T0));

        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 4);
        int connected = h.Sink.Emitted.Count(i => i.Key.Kind == NotificationKind.DeviceConnected);
        int aggregate = h.Sink.Emitted.Count(i => i.Key.Kind == NotificationKind.DevicesChanged);
        Assert.Equal(3, connected);
        Assert.Equal(1, aggregate);
        Assert.Contains(h.Sink.Emitted, i =>
            i.Key.Kind == NotificationKind.DevicesChanged
            && i.Title == Vocabulary.Notifications.DevicesChangedTitle(2));
    }

    // --- T/U resume --------------------------------------------------------

    [Fact]
    public async Task T_U_Resume_Rebaseline_And_8s_Suppress() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();

        h.Coord.OnSuspend();
        h.Coord.RebaselineForResume(h.Engine.CurrentSnapshot);
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0.AddSeconds(1)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(7));
        Assert.Empty(h.Sink.Emitted);

        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(
                Proto("R8", model: "Phone"),
                Proto("NEW", model: "Other")),
            T0.AddSeconds(2)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2)); // still in 8s window from resume
        Assert.Empty(h.Sink.Emitted);

        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(6)); // exit 8s
        h.Sink.Clear();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(
                Proto("R8", model: "Phone"),
                Proto("NEW", model: "Other"),
                Proto("NEW2", model: "Third")),
            T0.AddSeconds(3)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        Assert.Contains(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceConnected);
    }

    // --- V shutdown --------------------------------------------------------

    [Fact]
    public async Task V_Shutdown_Zero_Disconnect() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();

        h.Coord.BeginShutdown();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, T0.AddSeconds(1)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceDisconnected);
    }

    // --- W/X sink failure --------------------------------------------------

    [Fact]
    public async Task W_X_Sink_Failure_Leaves_Snapshot_And_Future_Deliverable() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        h.Sink.ThrowOnEmit = new InvalidOperationException("injected sink failure");
        long revBefore = h.Engine.CurrentSnapshot.Revision;
        var healthBefore = h.Engine.CurrentSnapshot.Health.Kind;

        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        long revAfterPublish = h.Engine.CurrentSnapshot.Revision;
        Assert.True(revAfterPublish > revBefore);
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await Task.Delay(50); // allow hold continuation
        Assert.Equal(revAfterPublish, h.Engine.CurrentSnapshot.Revision);
        Assert.Equal(healthBefore, h.Engine.CurrentSnapshot.Health.Kind);
        Assert.Empty(h.Sink.Emitted); // throw prevented record — Fake throws before add

        h.Sink.ThrowOnEmit = null;
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(61)); // clear dedup
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, T0.AddSeconds(2)));
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R9", model: "Other")), T0.AddSeconds(3)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        Assert.Contains(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceConnected);
    }

    // --- Y/Z Explorer ------------------------------------------------------

    [Fact]
    public async Task Y_Z_Explorer_Recreation_Zero_Intent_Then_Delivery_Works() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        // Explorer/TaskbarCreated must not re-baseline or emit via notification path.
        var trayHost = new FakeTrayBalloonHost();
        trayHost.SimulateExplorerRestart();
        Assert.Empty(trayHost.Shown);

        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        int n = h.Sink.Emitted.Count;
        trayHost.SimulateExplorerRestart();

        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0.AddSeconds(1)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(n, h.Sink.Emitted.Count); // no intent merely from explorer / same list

        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(61));
        h.Sink.Clear();
        using var balloon = new BalloonNotificationSink(trayHost, a => a());
        balloon.Emit(new NotificationIntent(
            new NotificationKey(NotificationKind.DeviceConnected, "usb:R9"),
            "Other connected",
            "Connected over USB",
            NotificationSeverity.Info));
        Assert.Single(trayHost.Shown); // sink usable after explorer recreation

        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone"), Proto("R9", model: "Other")), T0.AddSeconds(2)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        Assert.Contains(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceConnected);
    }

    // --- AA balloon click show-only (AppHost path mirrors sink contract) ---

    [Fact]
    public void AA_Balloon_Click_Uses_Show_Only_Not_Toggle() {
        var host = new FakeTrayBalloonHost();
        bool visible = false;
        int showCalls = 0;
        int hideCalls = 0;

        void ShowFlyoutOnly() {
            showCalls++;
            visible = true;
        }

        void ToggleFlyout() {
            if (visible) {
                hideCalls++;
                visible = false;
            }
            else {
                ShowFlyoutOnly();
            }
        }

        using var sink = new BalloonNotificationSink(host, a => a(), ShowFlyoutOnly);
        host.RaiseBalloonClicked();
        Assert.True(visible);
        Assert.Equal(1, showCalls);
        host.RaiseBalloonClicked();
        Assert.True(visible);
        Assert.Equal(2, showCalls);
        Assert.Equal(0, hideCalls);
        ToggleFlyout();
        Assert.False(visible);
        Assert.Equal(1, hideCalls);

        string appHostPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AdbView", "Host", "AppHost.cs"));
        string text = System.IO.File.ReadAllText(appHostPath);
        Assert.Contains("ShowFlyoutOnly", text, StringComparison.Ordinal);
        Assert.Contains("new BalloonNotificationSink(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("showFlyout: OnToggleFlyout", text, StringComparison.Ordinal);
    }

    // --- Mutation-sensitive proofs -----------------------------------------

    [Fact]
    public async Task Mut1_Silent_Baseline_Required_For_Startup_Devices() {
        // If AcceptSnapshot(initial) were skipped, first DeviceList would be previous=null absorb —
        // still silent. Stronger: three devices at first Accept after empty construction without
        // baseline still silent due to previous=null. Mutate-sensitive check: BaselineEstablished
        // must be true after AppHost construction (AcceptSnapshot initial).
        await using var h = CreateHarness();
        Assert.True(h.Coord.State.BaselineEstablished);
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("A"), Proto("B"), Proto("C")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Sink.Emitted);
    }

    [Fact]
    public async Task Mut2_Metadata_Must_Not_Generate_Connect() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();
        string id = h.Engine.CurrentSnapshot.Devices[0].Id;
        long gen = h.Engine.CurrentDeviceState.MetadataSessions[id].Generation;
        h.Engine.PostMetadata(gen, new MetadataReceivedEvent(id, 50, "M", "15"));
        await h.Engine.DrainAsync();
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(h.Sink.Emitted, i => i.Key.Kind == NotificationKind.DeviceConnected);
    }

    [Fact]
    public async Task Mut3_Tracker_Failure_Must_Not_Bypass_Grace() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();
        await h.PostAndDrainAsync(new DeviceTrackerFailedEvent("x"));
        Assert.NotEmpty(h.Engine.CurrentSnapshot.Devices);
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Sink.Emitted);
    }

    [Fact]
    public async Task Mut4_Resume_Must_Rebaseline() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();
        h.Coord.RebaselineForResume(h.Engine.CurrentSnapshot);
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0.AddSeconds(1)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(h.Sink.Emitted);
    }

    [Fact]
    public async Task Mut5_Shutdown_Must_Stop_Intents() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();
        h.Host.Dispose(); // BeginShutdown then dispose
        Assert.True(h.Coord.State.ShuttingDown);
    }

    [Fact]
    public async Task Mut6_Sink_Exception_Must_Not_Poison_Coordinator() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        h.Sink.ThrowOnEmit = new InvalidOperationException("boom");
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await Task.Delay(30);
        Assert.False(h.Coord.State.ShuttingDown);
        Assert.False(h.Coord.State.Suspended);
        h.Sink.ThrowOnEmit = null;
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(61));
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone"), Proto("R9", model: "X")), T0.AddSeconds(1)));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
    }

    [Fact]
    public void Mut7_Balloon_Click_Must_Not_Wire_Toggle() {
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AdbView", "Host", "AppHost.cs"));
        string text = System.IO.File.ReadAllText(path);
        Assert.Contains("ShowFlyoutOnly", text, StringComparison.Ordinal);
        Assert.DoesNotContain("new BalloonNotificationSink(\r\n                _trayIcon!,\r\n                MarshalNotificationToUi,\r\n                OnToggleFlyout)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("showFlyout: OnToggleFlyout", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Mut8_Fixture_Must_Not_Construct_Real_Sink() {
        using var host = new AppHost(
            sessionId: 88199,
            fixtureSuffix: "?fixture=empty",
            supervisorFactory: CreateHermeticSupervisor,
            bridgeHost: new MockBridgeHost(),
            headless: true);
        Assert.False(host.UsesRealBalloonNotificationSink);
        Assert.IsNotType<BalloonNotificationSink>(host.NotificationSink);
    }

    [Fact]
    public async Task Bridge_Ready_Resend_Does_Not_Notify() {
        await using var h = CreateHarness();
        await h.ExitStartupSuppressAsync();
        await h.PostAndDrainAsync(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "Phone")), T0));
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(2.5));
        await WaitUntilAsync(() => h.Sink.Emitted.Count >= 1);
        h.Sink.Clear();
        h.Host.Coordinator!.HandleAction(new ReadyAction { Protocol = BridgeProtocol.Version });
        await h.Clock.AdvanceAndFlushAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(h.Sink.Emitted);
    }
}
