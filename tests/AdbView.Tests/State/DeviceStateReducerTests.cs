#pragma warning disable CA1707
#pragma warning disable CA2007
namespace AdbView.Tests.State;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using AdbView.State;
using Xunit;

public sealed class DeviceStateReducerTests {
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static RawDevice Proto(
        string serial,
        int state = 8,
        int connectionType = 1,
        string? model = null,
        string? product = null) =>
        new() {
            Serial = serial,
            ProtoState = state,
            StateToken = null,
            ConnectionType = connectionType,
            Product = product,
            Model = model,
            Device = null,
            Format = RawDeviceFormat.ProtoBinary,
        };

    private static ReduceResult Reduce(
        AppSnapshot current,
        DeviceEngineState deviceState,
        AdbViewEvent evt,
        DateTimeOffset? now = null) =>
        StateReducer.ReduceWithDeviceState(current, deviceState, evt, now ?? T0);

    [Fact]
    public void A_EmptyList_ClearsDevices() {
        var withDevice = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S1")), T0)).Snapshot;

        var next = Reduce(
            withDevice,
            new DeviceEngineState { LatestAppliedListAt = T0 },
            new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, T0.AddSeconds(1)),
            T0.AddSeconds(1));

        Assert.Empty(next.Snapshot.Devices);
        Assert.Null(next.Snapshot.DeviceCountLabel);
        Assert.Equal(TrayStatusKind.Idle, next.Snapshot.Health.Tray);
    }

    [Theory]
    [InlineData(8, (int)DeviceStateKind.Connected, (int)SeverityKind.Positive, "Connected")]
    [InlineData(3, (int)DeviceStateKind.Unauthorized, (int)SeverityKind.Attention, "Authorization required")]
    [InlineData(6, (int)DeviceStateKind.Offline, (int)SeverityKind.Failure, "Device offline")]
    [InlineData(10, (int)DeviceStateKind.Recovery, (int)SeverityKind.Informational, "Recovery")]
    [InlineData(7, (int)DeviceStateKind.Bootloader, (int)SeverityKind.Informational, "Bootloader")]
    [InlineData(11, (int)DeviceStateKind.Sideload, (int)SeverityKind.Informational, "Sideload")]
    [InlineData(12, (int)DeviceStateKind.Rescue, (int)SeverityKind.Informational, "Rescue")]
    [InlineData(2, (int)DeviceStateKind.Authorizing, (int)SeverityKind.Attention, "Authorizing")]
    [InlineData(1, (int)DeviceStateKind.Connecting, (int)SeverityKind.Informational, "Connecting")]
    [InlineData(5, (int)DeviceStateKind.Detached, (int)SeverityKind.Neutral, "Detached")]
    [InlineData(0, (int)DeviceStateKind.Unknown, (int)SeverityKind.Neutral, "Unknown state")]
    public void B_to_M_StateMapping(int protoState, int expectedState, int expectedSeverity, string label) {
        var result = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S1", protoState)), T0));

        var d = Assert.Single(result.Snapshot.Devices);
        Assert.Equal((DeviceStateKind)expectedState, d.State);
        Assert.Equal((SeverityKind)expectedSeverity, d.Severity);
        Assert.Equal(label, d.StateLabel);
        Assert.Equal($"{label} · USB", d.StatusLine);
    }

    [Fact]
    public void B_ConnectedUsb_OneDeviceView() {
        var result = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8", 8, 1, model: "SM_A065F")), T0));

        var d = Assert.Single(result.Snapshot.Devices);
        Assert.Equal("usb:R8", d.Id);
        Assert.Equal(TransportKind.Usb, d.Transport);
        Assert.Equal("USB", d.TransportLabel);
        Assert.Equal(TrayStatusKind.Connected, result.Snapshot.Health.Tray);
    }

    [Fact]
    public void C_ConnectedWifi_OneDeviceView() {
        var result = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8", 8, 2, model: "SM_A065F")), T0));

        var d = Assert.Single(result.Snapshot.Devices);
        Assert.Equal("wifi:R8", d.Id);
        Assert.Equal(TransportKind.WiFi, d.Transport);
        Assert.Equal(TrayStatusKind.Connected, result.Snapshot.Health.Tray);
        Assert.Equal("Connected: SM_A065F (Wi-Fi)", result.Snapshot.Health.TrayTooltip);
    }

    [Fact]
    public void D_Unauthorized_Guidance() {
        var d = Assert.Single(Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 3)), T0)).Snapshot.Devices);
        Assert.Equal(Vocabulary.Device.UnauthorizedGuidance, d.Guidance);
    }

    [Fact]
    public void E_Offline_Guidance() {
        var d = Assert.Single(Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 6)), T0)).Snapshot.Devices);
        Assert.Equal(Vocabulary.Device.OfflineGuidance, d.Guidance);
    }

    [Fact]
    public void N_DisplayName_Uses5BResult() {
        var d = Assert.Single(Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(
                ImmutableArray.Create(Proto("S", 8, 1, model: "SM_A065F", product: "a06xx")),
                T0)).Snapshot.Devices);
        Assert.Equal("SM_A065F", d.DisplayName);
    }

    [Fact]
    public void O_MetadataFields_NullWhenUnavailable() {
        var d = Assert.Single(Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0)).Snapshot.Devices);
        Assert.Null(d.BatteryPercent);
        Assert.Null(d.Detail.AndroidVersion);
        Assert.Null(d.Detail.ExactModel);
        Assert.Null(d.Detail.Address);
    }

    [Fact]
    public void P_SameListTwice_NoRevision() {
        var first = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0));

        var second = Reduce(
            first.Snapshot,
            first.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0.AddSeconds(5)),
            T0.AddSeconds(5));

        Assert.Equal(first.Snapshot.Revision, second.Snapshot.Revision);
        Assert.Equal(first.Snapshot.Devices[0].StateSince, second.Snapshot.Devices[0].StateSince);
    }

    [Fact]
    public void Q_DeviceAddition_OneRevision() {
        var first = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("A")), T0));

        var second = Reduce(
            first.Snapshot,
            first.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("A"), Proto("B")), T0.AddSeconds(1)),
            T0.AddSeconds(1));

        Assert.Equal(first.Snapshot.Revision + 1, second.Snapshot.Revision);
        Assert.Equal(2, second.Snapshot.Devices.Length);
    }

    [Fact]
    public void R_ValidListRemoval_Immediate() {
        var first = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("A"), Proto("B")), T0));

        var second = Reduce(
            first.Snapshot,
            first.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("A")), T0.AddSeconds(1)),
            T0.AddSeconds(1));

        Assert.Single(second.Snapshot.Devices);
        Assert.Equal("usb:A", second.Snapshot.Devices[0].Id);
    }

    [Fact]
    public void S_OutOfOrderAt_Ignored() {
        var first = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("NEW")), T0.AddSeconds(10)));

        var stale = Reduce(
            first.Snapshot,
            first.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("OLD")), T0),
            T0.AddSeconds(11));

        Assert.Equal(first.Snapshot.Revision, stale.Snapshot.Revision);
        Assert.Equal("usb:NEW", Assert.Single(stale.Snapshot.Devices).Id);
    }

    [Fact]
    public void T_U_SameStateRefresh_PreservesTimestamps() {
        var first = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0));

        var second = Reduce(
            first.Snapshot,
            first.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0.AddMinutes(1)),
            T0.AddMinutes(1));

        Assert.Equal(first.Snapshot.Devices[0].StateSince, second.Snapshot.Devices[0].StateSince);
        Assert.Equal(first.Snapshot.Devices[0].ConnectedSince, second.Snapshot.Devices[0].ConnectedSince);
    }

    [Fact]
    public void V_W_StateTransition_UpdatesTimestamps() {
        var connected = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 8)), T0));

        var unauthorized = Reduce(
            connected.Snapshot,
            connected.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 3)), T0.AddSeconds(30)),
            T0.AddSeconds(30));

        Assert.Equal(T0.AddSeconds(30), unauthorized.Snapshot.Devices[0].StateSince);
        Assert.Null(unauthorized.Snapshot.Devices[0].ConnectedSince);

        var reconnected = Reduce(
            unauthorized.Snapshot,
            unauthorized.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 8)), T0.AddSeconds(60)),
            T0.AddSeconds(60));

        Assert.Equal(T0.AddSeconds(60), reconnected.Snapshot.Devices[0].StateSince);
        Assert.Equal(T0.AddSeconds(60), reconnected.Snapshot.Devices[0].ConnectedSince);
    }

    [Fact]
    public async Task X_Y_Z_AA_GraceRetentionAndStaleExpiry() {
        var delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = new List<(TimeSpan Delay, CancellationToken Ct)>();

        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            getUtcNow: () => T0,
            delay: async (delay, ct) => {
                delays.Add((delay, ct));
                // Must observe cancellation: a bare WhenAny completion after Cancel
                // would return successfully and StateEngine would Post a stale grace expiry.
                await delayGate.Task.WaitAsync(ct);
            });

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0));
        await engine.DrainAsync();
        var afterList = engine.CurrentSnapshot;
        Assert.Single(afterList.Devices);
        long rev = afterList.Revision;

        engine.Post(new DeviceTrackerFailedEvent("stream ended"));
        await engine.DrainAsync();
        Assert.Equal(rev, engine.CurrentSnapshot.Revision);
        Assert.Single(engine.CurrentSnapshot.Devices);
        Assert.True(
            SpinWait.SpinUntil(() => delays.Count > 0, TimeSpan.FromSeconds(2)),
            "grace delay was not scheduled");
        Assert.Equal(TimeSpan.FromSeconds(2), delays[0].Delay);

        // Y: recovery before grace fires
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0.AddSeconds(1)));
        await engine.DrainAsync();
        Assert.Single(engine.CurrentSnapshot.Devices);
        Assert.Equal(afterList.Devices[0].StateSince, engine.CurrentSnapshot.Devices[0].StateSince);

        // Stale grace from first failure must not clear
        long staleGen = 1;
        engine.Post(new DeviceTrackerGraceExpiredEvent(staleGen));
        await engine.DrainAsync();
        Assert.Single(engine.CurrentSnapshot.Devices);

        // Z: new failure then expire matching generation
        delays.Clear();
        delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Post(new DeviceTrackerFailedEvent("again"));
        await engine.DrainAsync();
        long activeGen = engine.CurrentDeviceState.ActiveGraceGeneration;
        Assert.True(activeGen > 0);

        // Fire grace by posting expiry directly (deterministic; avoid waiting on delay)
        engine.Post(new DeviceTrackerGraceExpiredEvent(activeGen));
        await engine.DrainAsync();
        Assert.Empty(engine.CurrentSnapshot.Devices);
        Assert.Equal(TrayStatusKind.Idle, engine.CurrentSnapshot.Health.Tray);
    }

    [Fact]
    public void AB_HealthEvent_PreservesDevices() {
        var withDevices = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0));

        var health = Reduce(
            withDevices.Snapshot,
            withDevices.DeviceState,
            new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Restoring, null, 0, 1)),
            T0.AddSeconds(1));

        Assert.Single(health.Snapshot.Devices);
        Assert.Equal(GlobalHealthKind.AdbRestoring, health.Snapshot.Health.Kind);
    }

    [Fact]
    public void AC_PinEvent_PreservesDevices() {
        var withDevices = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0));

        var pinned = Reduce(
            withDevices.Snapshot,
            withDevices.DeviceState,
            new SetPinnedEvent(true),
            T0.AddSeconds(1));

        Assert.Single(pinned.Snapshot.Devices);
        Assert.True(pinned.Snapshot.Shell.Pinned);
    }

    [Fact]
    public void AC2_DeviceList_PreservesPinnedShell() {
        var pinned = StateEngine.CreateDefaultSnapshot(isPinned: true, revision: 1);
        var withDevices = Reduce(
            pinned,
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0));

        Assert.True(withDevices.Snapshot.Shell.Pinned);
        Assert.Single(withDevices.Snapshot.Devices);
    }

    [Fact]
    public void AD_DeviceEvent_PreservesHealthKind() {
        var unavailable = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0)));

        var withDevices = Reduce(
            unavailable.Snapshot,
            unavailable.DeviceState,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S")), T0.AddSeconds(1)),
            T0.AddSeconds(1));

        Assert.Equal(GlobalHealthKind.AdbUnavailable, withDevices.Snapshot.Health.Kind);
        Assert.Equal(TrayStatusKind.Failure, withDevices.Snapshot.Health.Tray);
        Assert.Single(withDevices.Snapshot.Devices);
    }

    [Fact]
    public void AE_DeviceCountLabel() {
        Assert.Null(Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, T0)).Snapshot.DeviceCountLabel);

        Assert.Null(Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("A")), T0)).Snapshot.DeviceCountLabel);

        Assert.Equal("2 devices", Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("A"), Proto("B")), T0)).Snapshot.DeviceCountLabel);
    }

    [Fact]
    public void AF_to_AJ_TrayAggregation() {
        Assert.Equal(TrayStatusKind.Idle, StateEngine.CreateDefaultSnapshot().Health.Tray);

        Assert.Equal(
            TrayStatusKind.Connected,
            Reduce(StateEngine.CreateDefaultSnapshot(revision: 1), DeviceEngineState.Empty,
                new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 8)), T0)).Snapshot.Health.Tray);

        Assert.Equal(
            TrayStatusKind.Attention,
            Reduce(StateEngine.CreateDefaultSnapshot(revision: 1), DeviceEngineState.Empty,
                new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 3)), T0)).Snapshot.Health.Tray);

        Assert.Equal(
            TrayStatusKind.Failure,
            Reduce(StateEngine.CreateDefaultSnapshot(revision: 1), DeviceEngineState.Empty,
                new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0)))
                .Snapshot.Health.Tray);

        Assert.Equal(
            TrayStatusKind.Special,
            Reduce(StateEngine.CreateDefaultSnapshot(revision: 1), DeviceEngineState.Empty,
                new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 10)), T0)).Snapshot.Health.Tray);
    }

    [Fact]
    public void AK_TooltipExactStrings() {
        Assert.Equal(
            Vocabulary.Health.NoDevicesTrayTooltip,
            StateEngine.CreateDefaultSnapshot().Health.TrayTooltip);

        Assert.Equal(
            Vocabulary.Health.AdbUnavailableTrayTooltip,
            Reduce(StateEngine.CreateDefaultSnapshot(revision: 1), DeviceEngineState.Empty,
                new AdbServerStateChangedEvent(new AdbServerStateChanged(AdbServerPhase.Unavailable, null, 0, 0)))
                .Snapshot.Health.TrayTooltip);

        var connected = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 8, 2, model: "Galaxy")), T0));
        Assert.Equal("Connected: Galaxy (Wi-Fi)", connected.Snapshot.Health.TrayTooltip);

        var auth = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(ImmutableArray.Create(Proto("S", 3, 1, model: "Pixel 8")), T0));
        Assert.Equal("Authorization required: Pixel 8", auth.Snapshot.Health.TrayTooltip);
    }

    [Fact]
    public void AL_TooltipTruncation() {
        string longName = new string('A', 200);
        string tip = TrayStatusAggregator.TruncateAtWordBoundary(
            $"Connected: {longName} (USB)",
            127);
        Assert.True(tip.Length <= 127);
        Assert.DoesNotContain("USB", tip, StringComparison.Ordinal); // cut before end
    }

    [Fact]
    public void AQ_DuplicateId_DropsLaterDeterministically() {
        var result = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(
                ImmutableArray.Create(
                    Proto("SAME", 8, 1, model: "First"),
                    Proto("SAME", 8, 1, model: "Second")),
                T0));

        var d = Assert.Single(result.Snapshot.Devices);
        Assert.Equal("First", d.DisplayName);
        Assert.Equal("usb:SAME", d.Id);
    }

    [Fact]
    public void Ordering_UsbBeforeWifi_Stable() {
        var result = Reduce(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(
                ImmutableArray.Create(
                    Proto("B", 8, 2),
                    Proto("A", 8, 1)),
                T0));

        Assert.Equal(2, result.Snapshot.Devices.Length);
        Assert.Equal(TransportKind.Usb, result.Snapshot.Devices[0].Transport);
        Assert.Equal(TransportKind.WiFi, result.Snapshot.Devices[1].Transport);
    }
}
