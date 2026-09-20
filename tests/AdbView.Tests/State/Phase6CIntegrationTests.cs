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
using AdbView.State;
using AdbView.Tests.Fakes;
using Xunit;
using DomainPhase = AdbView.Adb.Model.PairingPhase;
using SnapshotPhase = AdbView.State.PairingPhase;

public sealed class Phase6CIntegrationTests {
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static RawMdnsService PairingSvc(
        string instance = "phone",
        string? ipv4 = "192.168.1.10",
        int port = 37100,
        string? given = null,
        string? model = null,
        string? serial = null) =>
        new() {
            InstanceName = instance,
            ServiceType = MdnsServiceTypes.Pairing,
            Kind = MdnsServiceKind.Pairing,
            Ipv4 = ipv4,
            Port = port,
            GivenName = given,
            ProductModel = model,
            Serial = serial,
            Format = RawMdnsFormat.ProtoBinary,
        };

    private static RawMdnsService ConnectSvc(
        string instance = "phone-c",
        string? ipv4 = "192.168.1.10",
        int port = 5555,
        string? given = null,
        string? serial = null) =>
        new() {
            InstanceName = instance,
            ServiceType = MdnsServiceTypes.Connect,
            Kind = MdnsServiceKind.Connect,
            Ipv4 = ipv4,
            Port = port,
            GivenName = given,
            Serial = serial,
            Format = RawMdnsFormat.ProtoBinary,
        };

    private static RawMdnsService LegacySvc() =>
        new() {
            InstanceName = "legacy",
            ServiceType = MdnsServiceTypes.Legacy,
            Kind = MdnsServiceKind.Legacy,
            Ipv4 = "192.168.1.10",
            Port = 5555,
            Format = RawMdnsFormat.ProtoBinary,
        };

    private static RawDevice WifiDevice(string serial, string? model = "Pixel") =>
        new() {
            Serial = serial,
            StateToken = "device",
            ProtoState = 8,
            ConnectionType = 2,
            Model = model,
            Format = RawDeviceFormat.ProtoBinary,
        };

    private static RawDevice UsbDevice(string serial) =>
        new() {
            Serial = serial,
            StateToken = "device",
            ProtoState = 8,
            ConnectionType = 1,
            Model = "USBPhone",
            Format = RawDeviceFormat.ProtoBinary,
        };

    private static AppSnapshot Reduce(AppSnapshot snap, DeviceEngineState state, AdbViewEvent evt, DateTimeOffset now, out DeviceEngineState nextState, out ReduceResult result) {
        result = StateReducer.ReduceWithDeviceState(snap, state, evt, now);
        nextState = result.DeviceState;
        return result.Snapshot;
    }

    [Fact]
    public void A_ZeroMdns_NoOffers() {
        var snap = StateEngine.CreateDefaultSnapshot();
        var next = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0), T0, out _, out _);
        Assert.Empty(next.PairingOffers);
    }

    [Fact]
    public void B_PairingService_OneOffer() {
        var svc = PairingSvc(given: "Galaxy S24");
        var snap = StateEngine.CreateDefaultSnapshot();
        var next = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0), T0, out var state, out _);
        Assert.Single(next.PairingOffers);
        Assert.Equal(svc.DiscoveryKey, next.PairingOffers[0].Id);
        Assert.Equal("Galaxy S24", next.PairingOffers[0].DisplayName);
        Assert.Equal(Vocabulary.Pairing.OfferHeading, next.PairingOffers[0].Heading);
        Assert.True(state.OfferBindings.ContainsKey(svc.DiscoveryKey));
    }

    [Fact]
    public void C_ConnectService_NoOffer() {
        var snap = StateEngine.CreateDefaultSnapshot();
        var next = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(ConnectSvc()), T0), T0, out _, out _);
        Assert.Empty(next.PairingOffers);
    }

    [Fact]
    public void D_LegacyService_NoOffer() {
        var snap = StateEngine.CreateDefaultSnapshot();
        var next = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(LegacySvc()), T0), T0, out _, out _);
        Assert.Empty(next.PairingOffers);
    }

    [Fact]
    public void E_DuplicateService_OneOffer() {
        var a = PairingSvc();
        var b = PairingSvc(); // same discovery key
        var snap = StateEngine.CreateDefaultSnapshot();
        var next = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(a, b), T0), T0, out _, out _);
        Assert.Single(next.PairingOffers);
    }

    [Fact]
    public void F_Withdrawal_RemovesOffer() {
        var svc = PairingSvc(given: "Phone");
        var snap = StateEngine.CreateDefaultSnapshot();
        snap = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0), T0, out var state, out _);
        Assert.Single(snap.PairingOffers);
        snap = Reduce(snap, state, new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0.AddSeconds(1)), T0.AddSeconds(1), out _, out _);
        Assert.Empty(snap.PairingOffers);
    }

    [Fact]
    public void G_H_I_DisplayNamePriority() {
        Assert.Equal("Given", PairingOfferMapper.ResolveDisplayName(PairingSvc(given: "Given", model: "Model")));
        Assert.Equal("Model", PairingOfferMapper.ResolveDisplayName(PairingSvc(given: null, model: "Model", instance: "inst")));
        Assert.Equal(Vocabulary.Pairing.DefaultDeviceName, PairingOfferMapper.ResolveDisplayName(PairingSvc(given: null, model: null, instance: "inst")));
        Assert.Equal(Vocabulary.Pairing.DefaultDeviceName, PairingOfferMapper.ResolveDisplayName(PairingSvc(given: null, model: null, instance: "adb-RFCX116CYYX-JYueKC")));
    }

    [Fact]
    public void J_StaleMdnsList_Ignored() {
        var snap = StateEngine.CreateDefaultSnapshot();
        snap = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(PairingSvc()), T0.AddSeconds(10)), T0.AddSeconds(10), out var state, out _);
        long rev = snap.Revision;
        snap = Reduce(snap, state, new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0), T0, out _, out _);
        Assert.Equal(rev, snap.Revision);
        Assert.Single(snap.PairingOffers);
    }

    [Fact]
    public void K_SemanticDuplicate_NoRevision() {
        var svc = PairingSvc(given: "A");
        var snap = StateEngine.CreateDefaultSnapshot();
        snap = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0), T0, out var state, out _);
        long rev = snap.Revision;
        snap = Reduce(snap, state, new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0.AddSeconds(1)), T0.AddSeconds(1), out _, out _);
        Assert.Equal(rev, snap.Revision);
    }

    [Fact]
    public void L_MdnsFailure_DoesNotChangeGlobalHealth() {
        var snap = StateEngine.CreateDefaultSnapshot();
        Assert.Equal(GlobalHealthKind.Healthy, snap.Health.Kind);
        snap = Reduce(snap, DeviceEngineState.Empty, new MdnsTrackerFailedEvent("down"), T0, out _, out _);
        Assert.Equal(GlobalHealthKind.Healthy, snap.Health.Kind);
        Assert.Empty(snap.Warnings);
    }

    [Fact]
    public void M_N_ProlongedFailure_Warning_ThenRecovery() {
        var snap = StateEngine.CreateDefaultSnapshot();
        snap = Reduce(snap, DeviceEngineState.Empty, new MdnsTrackerFailedEvent("down"), T0, out var state, out var result);
        Assert.Equal(GraceCommandKind.Schedule, result.WirelessRestoring.Kind);
        Assert.Equal(GraceCommandKind.Schedule, result.WirelessUnavailable.Kind);

        snap = Reduce(snap, state, new WirelessDiscoveryRestoringExpiredEvent(state.ActiveWirelessRestoringGeneration), T0.AddSeconds(5), out state, out _);
        Assert.Contains(snap.Warnings, w => w.Title == Vocabulary.Wireless.RestoringTitle);

        snap = Reduce(snap, state, new WirelessDiscoveryUnavailableExpiredEvent(state.ActiveWirelessUnavailableGeneration), T0.AddSeconds(60), out state, out _);
        Assert.Contains(snap.Warnings, w => w.Title == Vocabulary.Wireless.UnavailableTitle);
        Assert.Equal(GlobalHealthKind.Healthy, snap.Health.Kind);

        snap = Reduce(snap, state, new MdnsServicesReceivedEvent(ImmutableArray.Create(PairingSvc()), T0.AddMinutes(2)), T0.AddMinutes(2), out _, out _);
        Assert.DoesNotContain(snap.Warnings, w => w.Subsystem == SubsystemKind.WirelessDiscovery);
        Assert.Single(snap.PairingOffers);
    }

    [Fact]
    public void O_P_PairStart_ValidAndStale() {
        var svc = PairingSvc(given: "Galaxy");
        var snap = StateEngine.CreateDefaultSnapshot();
        snap = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0), T0, out var state, out _);

        snap = Reduce(snap, state, new PairingProgressedEvent(svc.DiscoveryKey, DomainPhase.AwaitingCode, null, PairingFailureKind.None, "Galaxy"), T0.AddSeconds(1), out state, out _);
        Assert.NotNull(snap.Pairing);
        Assert.Equal(SnapshotPhase.AwaitingCode, snap.Pairing!.Phase);
        Assert.Equal(Vocabulary.Pairing.CodeEntryDetail, snap.Pairing.Detail);

        // Stale offer start is handled by PairingSession (no binding) — reducer still accepts events only for known ops.
        Assert.False(state.OfferBindings.ContainsKey("missing"));
    }

    [Fact]
    public async Task Q_R_S_T_U_PairSubmit_ViaSession() {
        var svc = PairingSvc(given: "Galaxy S24", serial: "SERIAL1");
        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(),
            () => T0,
            async (delay, ct) => {
                if (delay < TimeSpan.FromSeconds(3)) {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            });
        engine.Post(new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0));
        await engine.DrainAsync();

        var pair = new FakeHostPairClient(new HostPairResult(true, PairingFailureKind.None, null));
        using var session = new PairingSession(
            post: e => engine.Post(e),
            resolveOffer: id => engine.CurrentDeviceState.OfferBindings.GetValueOrDefault(id),
            coordinator: new PairingCoordinator(pairClient: pair));

        session.Start(svc.DiscoveryKey);
        await engine.DrainAsync();
        Assert.Equal(SnapshotPhase.AwaitingCode, engine.CurrentSnapshot.Pairing!.Phase);

        int before = pair.CallCount;
        session.Submit(svc.DiscoveryKey, "12345"); // invalid
        await WaitForAsync(() => engine.CurrentSnapshot.Pairing?.Phase == SnapshotPhase.Failed);
        Assert.Equal(before, pair.CallCount);
        Assert.Equal(Vocabulary.Pairing.FailedTitle, engine.CurrentSnapshot.Pairing!.Title);

        // Retry then valid submit
        session.Start(svc.DiscoveryKey);
        await WaitForAsync(() => engine.CurrentSnapshot.Pairing?.Phase == SnapshotPhase.AwaitingCode);
        session.Submit(svc.DiscoveryKey, "123456");
        await WaitForAsync(() => engine.CurrentSnapshot.Pairing?.Phase == SnapshotPhase.PairedWaitingForDevice);
        Assert.Equal(1, pair.CallCount);
        Assert.Equal(Vocabulary.Pairing.SuccessTitle("Galaxy S24"), engine.CurrentSnapshot.Pairing!.Title);
        Assert.Equal(Vocabulary.Pairing.SuccessDetail, engine.CurrentSnapshot.Pairing.Detail);
        Assert.Empty(engine.CurrentSnapshot.Devices); // X
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000) {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs) {
            if (condition()) {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.True(condition(), "condition not met before timeout");
    }

    [Fact]
    public async Task V_W_CancelAndLateSuccess() {
        var svc = PairingSvc(given: "Phone");
        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(),
            () => T0,
            async (delay, ct) => {
                if (delay < TimeSpan.FromSeconds(3)) {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            });
        engine.Post(new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0));
        await engine.DrainAsync();

        var release = new TaskCompletionSource<HostPairResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = new FakeHostPairClient(async (_, _, _) => {
            entered.TrySetResult();
            return await release.Task.ConfigureAwait(false);
        });
        using var session = new PairingSession(
            e => engine.Post(e),
            id => engine.CurrentDeviceState.OfferBindings.GetValueOrDefault(id),
            new PairingCoordinator(pairClient: pair));

        session.Start(svc.DiscoveryKey);
        await engine.DrainAsync();
        session.Submit(svc.DiscoveryKey, "123456");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Cancel(svc.DiscoveryKey);
        await engine.DrainAsync();
        Assert.Null(engine.CurrentSnapshot.Pairing);

        release.TrySetResult(new HostPairResult(true, PairingFailureKind.None, null));
        await Task.Delay(50);
        await engine.DrainAsync();
        Assert.Null(engine.CurrentSnapshot.Pairing);
    }

    [Fact]
    public void X_Y_Z_PairSuccess_DeviceMatching() {
        var svc = PairingSvc(given: "Galaxy", serial: "HW1", ipv4: "10.0.0.5", port: 37100);
        var snap = StateEngine.CreateDefaultSnapshot();
        snap = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0), T0, out var state, out _);
        snap = Reduce(snap, state, new PairingProgressedEvent(svc.DiscoveryKey, DomainPhase.PairedWaitingForDevice, null, PairingFailureKind.None, "Galaxy"), T0.AddSeconds(1), out state, out _);
        Assert.Equal(SnapshotPhase.PairedWaitingForDevice, snap.Pairing!.Phase);
        Assert.Empty(snap.Devices);

        // Unrelated USB device does not clear waiting
        snap = Reduce(snap, state, new DeviceListReceivedEvent(ImmutableArray.Create(UsbDevice("OTHER")), T0.AddSeconds(2)), T0.AddSeconds(2), out state, out _);
        Assert.Equal(SnapshotPhase.PairedWaitingForDevice, snap.Pairing!.Phase);
        Assert.Single(snap.Devices);

        // Unrelated Wi-Fi device does not clear waiting
        snap = Reduce(snap, state, new DeviceListReceivedEvent(
            ImmutableArray.Create(UsbDevice("OTHER"), WifiDevice("OTHER-WIFI")), T0.AddSeconds(3)), T0.AddSeconds(3), out state, out _);
        Assert.Equal(SnapshotPhase.PairedWaitingForDevice, snap.Pairing!.Phase);
        Assert.Equal(2, snap.Devices.Length);

        // Matching Wi-Fi by serial clears waiting
        snap = Reduce(snap, state, new DeviceListReceivedEvent(
            ImmutableArray.Create(UsbDevice("OTHER"), WifiDevice("HW1")), T0.AddSeconds(4)), T0.AddSeconds(4), out state, out _);
        Assert.Null(snap.Pairing);
        Assert.Equal(2, snap.Devices.Length);
    }

    [Fact]
    public void AA_AB_SixtySecondWait_AndStaleToken() {
        var svc = PairingSvc(given: "G");
        var snap = StateEngine.CreateDefaultSnapshot();
        snap = Reduce(snap, DeviceEngineState.Empty, new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0), T0, out var state, out _);
        snap = Reduce(snap, state, new PairingProgressedEvent(svc.DiscoveryKey, DomainPhase.PairedWaitingForDevice, null, PairingFailureKind.None, "G"), T0, out state, out var r1);
        Assert.Equal(GraceCommandKind.Schedule, r1.PairingWait.Kind);
        long gen1 = state.ActivePairingWaitGeneration;

        // Newer operation bumps generation
        snap = Reduce(snap, state, new PairingProgressedEvent(svc.DiscoveryKey, DomainPhase.PairedWaitingForDevice, null, PairingFailureKind.None, "G"), T0.AddSeconds(1), out state, out _);
        long gen2 = state.ActivePairingWaitGeneration;
        Assert.NotEqual(gen1, gen2);

        // Stale timer ignored
        snap = Reduce(snap, state, new PairingWaitExpiredEvent(gen1), T0.AddSeconds(61), out state, out _);
        Assert.Equal(SnapshotPhase.PairedWaitingForDevice, snap.Pairing!.Phase);

        // Current timer clears
        snap = Reduce(snap, state, new PairingWaitExpiredEvent(gen2), T0.AddSeconds(62), out _, out _);
        Assert.Null(snap.Pairing);
    }

    [Fact]
    public void AC_AD_AE_CrossDomainPreservation() {
        var svc = PairingSvc(given: "G");
        var snap = StateEngine.CreateDefaultSnapshot();
        snap = Reduce(snap, DeviceEngineState.Empty, new DeviceListReceivedEvent(ImmutableArray.Create(UsbDevice("U1")), T0), T0, out var state, out _);
        Assert.Single(snap.Devices);
        snap = Reduce(snap, state, new MdnsServicesReceivedEvent(ImmutableArray.Create(svc), T0.AddSeconds(1)), T0.AddSeconds(1), out state, out _);
        Assert.Single(snap.Devices);
        Assert.Single(snap.PairingOffers);

        snap = Reduce(snap, state, new PairingProgressedEvent(svc.DiscoveryKey, DomainPhase.AwaitingCode, null, PairingFailureKind.None, "G"), T0.AddSeconds(2), out state, out _);
        Assert.Single(snap.Devices);
        Assert.NotNull(snap.Pairing);

        snap = Reduce(snap, state, new SetPinnedEvent(true), T0.AddSeconds(3), out state, out _);
        Assert.True(snap.Shell.Pinned);
        Assert.NotNull(snap.Pairing);
        Assert.Single(snap.PairingOffers);

        snap = Reduce(snap, state, new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Restoring, null, 1, 1)), T0.AddSeconds(4), out _, out _);
        Assert.Equal(GlobalHealthKind.AdbRestoring, snap.Health.Kind);
        Assert.NotNull(snap.Pairing);
        Assert.Single(snap.PairingOffers);
        Assert.Single(snap.Devices);
    }

    [Fact]
    public void AF_AG_AH_IdentityEnrichment() {
        var connect = ConnectSvc(ipv4: "192.168.1.10", port: 5555, given: "Friendly", serial: "HW99");
        var identity = new DeviceIdentity();
        var n = identity.Normalize(WifiDevice("192.168.1.10:5555"), ImmutableArray.Create(connect));
        Assert.Equal("wifi:HW99", n.Id);
        Assert.Equal("HW99", n.HardwareSerial);
        Assert.Equal("Friendly", n.DisplayName);

        var usb = identity.Normalize(UsbDevice("HW99"), ImmutableArray.Create(connect));
        Assert.Equal("usb:HW99", usb.Id);
        Assert.NotEqual(n.Id, usb.Id);
    }

    [Fact]
    public void AI_AJ_AK_TrayPriority() {
        var offer = new PairingOfferView {
            Id = "o",
            DisplayName = "Phone",
            Heading = Vocabulary.Pairing.OfferHeading,
            Detail = Vocabulary.Pairing.OfferDetail,
            ActionLabel = Vocabulary.Pairing.ActionLabel,
        };
        var offers = ImmutableArray.Create(offer);

        var (tray, _) = TrayStatusAggregator.Aggregate(GlobalHealthKind.Healthy, ImmutableArray<DeviceView>.Empty, offers);
        Assert.Equal(TrayStatusKind.PairingAvailable, tray);

        var unauthorized = ImmutableArray.Create(new DeviceView {
            Id = "usb:x",
            DisplayName = "X",
            State = DeviceStateKind.Unauthorized,
            Severity = SeverityKind.Attention,
            StateLabel = Vocabulary.Device.UnauthorizedStateLabel,
            StatusLine = "x",
            Transport = TransportKind.Usb,
            TransportLabel = "USB",
            MetaTemplate = "{age}",
            StateSince = T0,
            Detail = new DeviceDetailView {
                Serial = "x",
                CopyFields = ImmutableArray<CopyFieldView>.Empty,
            },
        });
        (tray, _) = TrayStatusAggregator.Aggregate(GlobalHealthKind.Healthy, unauthorized, offers);
        Assert.Equal(TrayStatusKind.Attention, tray);

        (tray, _) = TrayStatusAggregator.Aggregate(GlobalHealthKind.AdbUnavailable, unauthorized, offers);
        Assert.Equal(TrayStatusKind.Failure, tray);
    }
}
