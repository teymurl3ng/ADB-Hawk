#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.State;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Host;
using AdbView.State;
using Xunit;

public sealed class MetadataIntegrationTests {
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeProbe : IMetadataProbe {
        public readonly List<(string Serial, MetadataCommand? Cmd, long Gen)> Calls = new();
        public Func<MetadataProbeTarget, MetadataCommand, MetadataCommandResult>? OnCommand { get; set; }
        public Func<MetadataProbeTarget, MetadataProbeResult>? OnFull { get; set; }
        public int InFlight;
        public TaskCompletionSource? BlockGate;

        public async Task<MetadataProbeResult> ProbeAsync(MetadataProbeTarget target, CancellationToken ct) {
            Interlocked.Increment(ref InFlight);
            try {
                if (BlockGate != null) {
                    await BlockGate.Task.WaitAsync(ct);
                }

                Calls.Add((target.AdbSerial, null, target.Generation));
                if (OnFull != null) {
                    return OnFull(target);
                }

                return new MetadataProbeResult(
                    target.DeviceId,
                    target.Generation,
                    80,
                    "SM-A065F",
                    "16",
                    MetadataFieldStatus.Ok,
                    MetadataFieldStatus.Ok,
                    MetadataFieldStatus.Ok,
                    MarketingName: null,
                    DeviceName: "Galaxy A06",
                    Brand: "samsung",
                    Manufacturer: "samsung",
                    MarketingNameStatus: MetadataFieldStatus.Unavailable,
                    DeviceNameStatus: MetadataFieldStatus.Ok,
                    BrandStatus: MetadataFieldStatus.Ok,
                    ManufacturerStatus: MetadataFieldStatus.Ok);
            }
            finally {
                Interlocked.Decrement(ref InFlight);
            }
        }

        public async Task<MetadataCommandResult> ProbeCommandAsync(
            MetadataProbeTarget target,
            MetadataCommand command,
            CancellationToken ct) {
            Interlocked.Increment(ref InFlight);
            try {
                if (BlockGate != null) {
                    await BlockGate.Task.WaitAsync(ct);
                }

                Calls.Add((target.AdbSerial, command, target.Generation));
                if (OnCommand != null) {
                    return OnCommand(target, command);
                }

                return command switch {
                    MetadataCommand.Battery => new MetadataCommandResult(command, MetadataFieldStatus.Ok, 70, null),
                    MetadataCommand.ExactModel => new MetadataCommandResult(command, MetadataFieldStatus.Ok, null, "SM-A065F"),
                    _ => new MetadataCommandResult(command, MetadataFieldStatus.Ok, null, "16"),
                };
            }
            finally {
                Interlocked.Decrement(ref InFlight);
            }
        }
    }

    private static RawDevice Proto(string serial, int state = 8, int connectionType = 1, string? model = "SM_A065F") =>
        new() {
            Serial = serial,
            ProtoState = state,
            ConnectionType = connectionType,
            Model = model,
            Product = "a06xx",
            Device = "a06",
            Format = RawDeviceFormat.ProtoBinary,
        };

    private static async Task<(StateEngine Engine, DeviceMetadataLifecycle Life, FakeProbe Probe, Func<DateTimeOffset> Clock)>
        CreateHarnessAsync(FakeProbe? probe = null) {
        DateTimeOffset now = T0;
        var fake = probe ?? new FakeProbe();
        var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            getUtcNow: () => now,
            delay: (_, _) => Task.CompletedTask);

        var life = new DeviceMetadataLifecycle(
            postMetadata: (gen, evt) => engine.PostMetadata(gen, evt),
            probe: fake,
            getUtcNow: () => now,
            delay: async (d, ct) => {
                // Virtual clock: advance and complete immediately for scheduled waits when tests bump now.
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
            });

        engine.MetadataTargetsChanged += life.OnTargetsChanged;
        return (engine, life, fake, () => now);
    }

    private static void SetNow(ref DateTimeOffset box, DateTimeOffset value) => box = value;

    [Fact]
    public async Task A_B_Connected_Appears_Null_Then_Enriches() {
        DateTimeOffset now = T0;
        var probe = new FakeProbe();
        probe.BlockGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(revision: 1),
            getUtcNow: () => now);
        using var life = new DeviceMetadataLifecycle(
            (gen, evt) => engine.PostMetadata(gen, evt),
            probe,
            () => now,
            (_, _) => Task.CompletedTask);
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        var before = Assert.Single(engine.CurrentSnapshot.Devices);
        Assert.Null(before.BatteryPercent);
        Assert.Null(before.Detail.ExactModel);
        Assert.Null(before.Detail.AndroidVersion);
        Assert.Equal("SM_A065F", before.DisplayName);

        probe.BlockGate.TrySetResult();
        await WaitForAsync(() => engine.CurrentSnapshot.Devices[0].BatteryPercent is not null);
        await engine.DrainAsync();
        var after = Assert.Single(engine.CurrentSnapshot.Devices);
        Assert.Equal(80, after.BatteryPercent);
        Assert.Equal("SM-A065F", after.Detail.ExactModel);
        Assert.Equal("16", after.Detail.AndroidVersion);
        Assert.Equal("Galaxy A06", after.DisplayName);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ConnectedSince, after.ConnectedSince);
    }

    [Fact]
    public async Task DisplayName_Refinement_Preserves_Identity_And_Does_Not_Reset_ConnectedSince() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        var early = Assert.Single(engine.CurrentSnapshot.Devices);
        Assert.Equal("SM_A065F", early.DisplayName);
        Assert.Null(early.Detail.ExactModel);
        DateTimeOffset? connectedSince = early.ConnectedSince;
        Assert.NotNull(connectedSince);
        long gen = engine.CurrentDeviceState.MetadataSessions[early.Id].Generation;

        engine.PostMetadata(
            gen,
            new MetadataReceivedEvent(
                early.Id,
                99,
                "SM-A065F",
                "16",
                MarketingName: null,
                DeviceName: "Galaxy A06",
                Brand: "samsung",
                Manufacturer: "samsung"));
        await engine.DrainAsync();

        var refined = Assert.Single(engine.CurrentSnapshot.Devices);
        Assert.Equal(early.Id, refined.Id);
        Assert.Equal("Galaxy A06", refined.DisplayName);
        Assert.Equal("SM-A065F", refined.Detail.ExactModel);
        Assert.Equal(connectedSince, refined.ConnectedSince);
        Assert.Equal(DeviceStateKind.Connected, refined.State);

        // Tracker tick after shell naming must not restore SM_A065F.
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0.AddSeconds(1)));
        await engine.DrainAsync();
        var afterTracker = Assert.Single(engine.CurrentSnapshot.Devices);
        Assert.Equal(early.Id, afterTracker.Id);
        Assert.Equal("Galaxy A06", afterTracker.DisplayName);
        Assert.Equal("SM-A065F", afterTracker.Detail.ExactModel);
        Assert.Equal(connectedSince, afterTracker.ConnectedSince);
    }

    [Fact]
    public async Task Duplicate_Friendly_Names_Keep_Distinct_DeviceIds() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        engine.Post(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8A"), Proto("R8B")),
            T0));
        await engine.DrainAsync();
        Assert.Equal(2, engine.CurrentSnapshot.Devices.Length);
        long genA = engine.CurrentDeviceState.MetadataSessions["usb:R8A"].Generation;
        long genB = engine.CurrentDeviceState.MetadataSessions["usb:R8B"].Generation;

        engine.PostMetadata(
            genA,
            new MetadataReceivedEvent("usb:R8A", 50, "SM-A065F", "16", DeviceName: "Galaxy A06", Brand: "samsung"));
        engine.PostMetadata(
            genB,
            new MetadataReceivedEvent("usb:R8B", 40, "SM-A065F", "16", DeviceName: "Galaxy A06", Brand: "samsung"));
        await engine.DrainAsync();

        Assert.Equal(2, engine.CurrentSnapshot.Devices.Length);
        Assert.All(engine.CurrentSnapshot.Devices, d => Assert.Equal("Galaxy A06", d.DisplayName));
        Assert.Contains(engine.CurrentSnapshot.Devices, d => d.Id == "usb:R8A");
        Assert.Contains(engine.CurrentSnapshot.Devices, d => d.Id == "usb:R8B");
        Assert.NotEqual(
            engine.CurrentSnapshot.Devices[0].Id,
            engine.CurrentSnapshot.Devices[1].Id);
    }

    [Fact]
    public async Task Usb_And_Wifi_Same_Shell_Metadata_Same_DisplayName() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        var usb = Proto("R8HW", connectionType: 1); // USB
        var wifi = new RawDevice {
            Serial = "192.168.1.20:5555",
            ProtoState = 8,
            ConnectionType = 2, // SOCKET → WiFi
            Model = "SM_A065F",
            Product = "a06xx",
            Device = "a06",
            Format = RawDeviceFormat.ProtoBinary,
        };
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(usb, wifi), T0));
        await engine.DrainAsync();

        string usbId = Assert.Single(engine.CurrentSnapshot.Devices.Where(d => d.Transport == TransportKind.Usb)).Id;
        string wifiId = Assert.Single(engine.CurrentSnapshot.Devices.Where(d => d.Transport == TransportKind.WiFi)).Id;
        Assert.NotEqual(usbId, wifiId);

        long genUsb = engine.CurrentDeviceState.MetadataSessions[usbId].Generation;
        long genWifi = engine.CurrentDeviceState.MetadataSessions[wifiId].Generation;
        engine.PostMetadata(
            genUsb,
            new MetadataReceivedEvent(
                usbId,
                90,
                "SM-A065F",
                "16",
                DeviceName: "Galaxy A06",
                Brand: "samsung"));
        engine.PostMetadata(
            genWifi,
            new MetadataReceivedEvent(
                wifiId,
                88,
                "SM-A065F",
                "16",
                DeviceName: "Galaxy A06",
                Brand: "samsung"));
        await engine.DrainAsync();

        Assert.Equal(
            "Galaxy A06",
            engine.CurrentSnapshot.Devices.Single(d => d.Id == usbId).DisplayName);
        Assert.Equal(
            "Galaxy A06",
            engine.CurrentSnapshot.Devices.Single(d => d.Id == wifiId).DisplayName);
    }

    [Fact]
    public async Task FGH_Null_Merge_Semantics() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        long gen = engine.CurrentDeviceState.MetadataSessions["usb:R8"].Generation;

        engine.PostMetadata(gen, new MetadataReceivedEvent("usb:R8", 90, "SM-A065F", "16"));
        await engine.DrainAsync();
        Assert.Equal(90, engine.CurrentSnapshot.Devices[0].BatteryPercent);

        engine.PostMetadata(gen, new MetadataReceivedEvent("usb:R8", null, null, null));
        await engine.DrainAsync();
        var d = engine.CurrentSnapshot.Devices[0];
        Assert.Null(d.BatteryPercent);
        Assert.Equal("SM-A065F", d.Detail.ExactModel);
        Assert.Equal("16", d.Detail.AndroidVersion);
    }

    [Fact]
    public async Task D_ExactModel_Not_From_Tracker_Model() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        engine.Post(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("R8", model: "TRACKER_MODEL")), T0));
        await engine.DrainAsync();
        Assert.Equal("TRACKER_MODEL", engine.CurrentSnapshot.Devices[0].DisplayName);
        Assert.Null(engine.CurrentSnapshot.Devices[0].Detail.ExactModel);
    }

    [Theory]
    [InlineData(3)] // Unauthorized
    [InlineData(6)] // Offline
    [InlineData(10)] // Recovery
    public async Task IJK_NonConnected_Never_Probed(int protoState) {
        var probe = new FakeProbe();
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        using var life = new DeviceMetadataLifecycle(
            (g, e) => engine.PostMetadata(g, e), probe, () => T0, (_, _) => Task.CompletedTask);
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8", protoState)), T0));
        await engine.DrainAsync();
        await Task.Delay(50);
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public async Task OPQ_Stale_Generation_Rejected_At_StateEngine_After_Remove_Reappear() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        long gen100 = engine.CurrentDeviceState.MetadataSessions["usb:R8"].Generation;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, T0.AddSeconds(1)));
        await engine.DrainAsync();
        Assert.Empty(engine.CurrentDeviceState.MetadataSessions);

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0.AddSeconds(2)));
        await engine.DrainAsync();
        long gen101 = engine.CurrentDeviceState.MetadataSessions["usb:R8"].Generation;
        Assert.NotEqual(gen100, gen101);
        Assert.True(gen101 > gen100);

        engine.PostMetadata(gen100, new MetadataReceivedEvent("usb:R8", 11, "OLD", "1"));
        await engine.DrainAsync();
        Assert.Null(engine.CurrentSnapshot.Devices[0].BatteryPercent);
        Assert.Null(engine.CurrentSnapshot.Devices[0].Detail.ExactModel);
    }

    [Fact]
    public async Task RST_AdbSerial_Change_New_Generation_And_Target() {
        var probe = new FakeProbe();
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        using var life = new DeviceMetadataLifecycle(
            (g, e) => engine.PostMetadata(g, e), probe, () => T0, (_, _) => Task.CompletedTask);
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        // Wi-Fi host:port serial; identity uses hardware serial from mDNS if available — use USB for Id stability
        // Force same DeviceId with different AdbSerial: wifi identity with endpoint serials.
        // Use SOCKET connection with serial host:port; without mDNS hardware serial = endpoint for Phase 5.
        // Actually for wifi:R8 we need mDNS. Simpler: use USB serial change is unusual.
        // Spec: wifi:R8VXA02SNKH with AdbSerial change. Provide mDNS with serial.
        var mdns = ImmutableArray.Create(new RawMdnsService {
            InstanceName = "adb-tls-connect-_adb-tls-connect._tcp.local",
            ServiceType = "_adb-tls-connect._tcp",
            Kind = MdnsServiceKind.Connect,
            Ipv4 = "192.168.18.248",
            Port = 5555,
            Serial = "R8VXA02SNKH",
            Format = RawMdnsFormat.ProtoText,
        });
        engine.Post(new MdnsServicesReceivedEvent(mdns, T0));
        await engine.DrainAsync();

        engine.Post(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("192.168.18.248:5555", 8, 2)), T0));
        await engine.DrainAsync();
        string id = Assert.Single(engine.CurrentSnapshot.Devices).Id;
        Assert.StartsWith("wifi:", id, StringComparison.Ordinal);
        long gen200 = engine.CurrentDeviceState.MetadataSessions[id].Generation;
        Assert.Equal("192.168.18.248:5555", engine.CurrentDeviceState.MetadataSessions[id].AdbSerial);

        await WaitForAsync(() => engine.CurrentSnapshot.Devices[0].Detail.ExactModel is not null);
        await engine.DrainAsync();
        Assert.Equal("SM-A065F", engine.CurrentSnapshot.Devices[0].Detail.ExactModel);

        // Hold the next generation's probe so we can assert the generation reset and
        // stale gen200 rejection before enrichment races in.
        probe.BlockGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var mdns2 = ImmutableArray.Create(new RawMdnsService {
            InstanceName = "adb-tls-connect-_adb-tls-connect._tcp.local",
            ServiceType = "_adb-tls-connect._tcp",
            Kind = MdnsServiceKind.Connect,
            Ipv4 = "192.168.18.249",
            Port = 5555,
            Serial = "R8VXA02SNKH",
            Format = RawMdnsFormat.ProtoText,
        });
        engine.Post(new MdnsServicesReceivedEvent(mdns2, T0.AddSeconds(1)));
        await engine.DrainAsync();

        engine.Post(new DeviceListReceivedEvent(
            ImmutableArray.Create(Proto("192.168.18.249:5555", 8, 2)), T0.AddSeconds(1)));
        await engine.DrainAsync();
        Assert.Equal(id, engine.CurrentSnapshot.Devices[0].Id);
        long gen201 = engine.CurrentDeviceState.MetadataSessions[id].Generation;
        Assert.True(gen201 > gen200);
        Assert.Equal("192.168.18.249:5555", engine.CurrentDeviceState.MetadataSessions[id].AdbSerial);
        Assert.Null(engine.CurrentSnapshot.Devices[0].BatteryPercent);
        Assert.Null(engine.CurrentSnapshot.Devices[0].Detail.ExactModel);
        Assert.Null(engine.CurrentSnapshot.Devices[0].Detail.AndroidVersion);

        engine.PostMetadata(gen200, new MetadataReceivedEvent(id, 1, "STALE", "9"));
        await engine.DrainAsync();
        Assert.Null(engine.CurrentSnapshot.Devices[0].BatteryPercent);
        Assert.Null(engine.CurrentSnapshot.Devices[0].Detail.ExactModel);
        Assert.Null(engine.CurrentSnapshot.Devices[0].Detail.AndroidVersion);

        probe.BlockGate.TrySetResult();
        await WaitForAsync(() => probe.Calls.Exists(c => c.Serial == "192.168.18.249:5555"));
        await engine.DrainAsync();
        Assert.Equal("SM-A065F", engine.CurrentSnapshot.Devices[0].Detail.ExactModel);
    }

    [Fact]
    public async Task WXYZ_AB_AC_Preserve_And_Duplicate() {
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        var first = engine.CurrentSnapshot.Devices[0];
        long gen = engine.CurrentDeviceState.MetadataSessions[first.Id].Generation;
        long rev = engine.CurrentSnapshot.Revision;

        engine.PostMetadata(gen, new MetadataReceivedEvent(first.Id, 50, "M", "15"));
        await engine.DrainAsync();
        var mid = engine.CurrentSnapshot.Devices[0];
        Assert.Equal(first.StateSince, mid.StateSince);
        Assert.Equal(first.ConnectedSince, mid.ConnectedSince);
        Assert.Equal(GlobalHealthKind.Healthy, engine.CurrentSnapshot.Health.Kind);
        Assert.Null(engine.CurrentSnapshot.Pairing);
        Assert.True(engine.CurrentSnapshot.PairingOffers.IsDefaultOrEmpty
            || engine.CurrentSnapshot.PairingOffers.IsEmpty);
        Assert.True(engine.CurrentSnapshot.Warnings.IsDefaultOrEmpty
            || engine.CurrentSnapshot.Warnings.IsEmpty);
        Assert.False(engine.CurrentSnapshot.Shell.Pinned);

        long rev2 = engine.CurrentSnapshot.Revision;
        Assert.True(rev2 > rev);

        engine.PostMetadata(gen, new MetadataReceivedEvent(first.Id, 50, "M", "15"));
        await engine.DrainAsync();
        Assert.Equal(rev2, engine.CurrentSnapshot.Revision);

        // Same generation tracker refresh preserves metadata
        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0.AddSeconds(5)));
        await engine.DrainAsync();
        Assert.Equal(50, engine.CurrentSnapshot.Devices[0].BatteryPercent);
        Assert.Equal("M", engine.CurrentSnapshot.Devices[0].Detail.ExactModel);
        Assert.Equal(gen, engine.CurrentDeviceState.MetadataSessions[first.Id].Generation);
    }

    [Fact]
    public async Task AE_AF_Visibility_Stale_30s() {
        DateTimeOffset now = T0;
        var probe = new FakeProbe();
        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => now);
        using var life = new DeviceMetadataLifecycle(
            (g, e) => engine.PostMetadata(g, e), probe, () => now, (_, _) => Task.CompletedTask);
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        await WaitForAsync(() => engine.CurrentSnapshot.Devices[0].BatteryPercent is not null);
        await engine.DrainAsync();
        int afterInitial = probe.Calls.Count;

        life.SetPresentationState(visible: true, pinned: false);
        await Task.Delay(30);
        Assert.Equal(afterInitial, probe.Calls.Count); // age 0 <= 30s

        life.SetPresentationState(visible: true, pinned: false); // repeated
        Assert.Equal(afterInitial, probe.Calls.Count);

        now = T0.AddSeconds(31);
        // Re-signal visibility transition: hide then show
        life.SetPresentationState(false, false);
        life.SetPresentationState(true, false);
        await WaitForAsync(() => probe.Calls.Count > afterInitial);
    }

    [Fact]
    public async Task AG_AH_AI_Pinned_120s_And_Stops() {
        DateTimeOffset now = T0;
        var delays = new List<TimeSpan>();
        var probe = new FakeProbe();
        var delayTcs = new Queue<TaskCompletionSource>();

        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => now);
        using var life = new DeviceMetadataLifecycle(
            (g, e) => engine.PostMetadata(g, e),
            probe,
            () => now,
            async (d, ct) => {
                delays.Add(d);
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delayTcs.Enqueue(tcs);
                await using (ct.Register(() => tcs.TrySetCanceled())) {
                    await tcs.Task;
                }
            });
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        await WaitForAsync(() => engine.CurrentSnapshot.Devices[0].BatteryPercent is not null);
        await engine.DrainAsync();

        life.SetPresentationState(true, true);
        await WaitForAsync(() => delays.Count >= 1);
        Assert.Equal(TimeSpan.FromSeconds(120), delays[0]);

        // 119.999 — do not complete timer yet; no extra battery-only beyond initial full
        int callsBefore = probe.Calls.Count;
        now = T0.AddSeconds(119).AddMilliseconds(999);
        Assert.Equal(callsBefore, probe.Calls.Count);

        now = T0.AddSeconds(120);
        delayTcs.Dequeue().TrySetResult();
        await WaitForAsync(() => probe.Calls.Count > callsBefore);

        life.SetPresentationState(false, true); // hidden stops
        int afterHide = probe.Calls.Count;
        if (delayTcs.Count > 0) {
            delayTcs.Dequeue().TrySetCanceled();
        }

        await Task.Delay(20);
        Assert.Equal(afterHide, probe.Calls.Count);

        life.SetPresentationState(true, true);
        await WaitForAsync(() => delays.Count >= 2);
        life.SetPresentationState(true, false); // unpin stops
    }

    [Fact]
    public async Task AL_Idle_Cost_Hidden_60s_Zero_After_Settle() {
        DateTimeOffset now = T0;
        var probe = new FakeProbe();
        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => now);
        using var life = new DeviceMetadataLifecycle(
            (g, e) => engine.PostMetadata(g, e), probe, () => now, (_, _) => Task.CompletedTask);
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        await WaitForAsync(() => engine.CurrentSnapshot.Devices[0].BatteryPercent is not null);
        await engine.DrainAsync();
        int settled = probe.Calls.Count;
        life.SetPresentationState(false, false);

        now = T0.AddSeconds(60);
        await Task.Delay(30);
        Assert.Equal(settled, probe.Calls.Count);
    }

    [Fact]
    public async Task AJ_AK_One_InFlight_Coalesce() {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakeProbe { BlockGate = gate };
        DateTimeOffset now = T0;
        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => now);
        using var life = new DeviceMetadataLifecycle(
            (g, e) => engine.PostMetadata(g, e), probe, () => now, (_, _) => Task.CompletedTask);
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        await WaitForAsync(() => probe.InFlight == 1);

        life.SetPresentationState(true, false);
        life.SetPresentationState(true, true);
        Assert.Equal(1, probe.InFlight);

        gate.TrySetResult();
        await WaitForAsync(() => probe.InFlight == 0);
    }

    [Fact]
    public async Task BatteryFailure_ClearsBattery_PreservesCache_NoImmediateRetryLoop() {
        DateTimeOffset now = T0;
        var delays = new List<TimeSpan>();
        var probe = new FakeProbe();

        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => now);
        using var life = new DeviceMetadataLifecycle(
            (g, e) => engine.PostMetadata(g, e),
            probe,
            () => now,
            async (d, ct) => {
                delays.Add(d);
                // Do not complete pinned waits — prove arm duration without firing.
                if (d >= TimeSpan.FromSeconds(60)) {
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await using (ct.Register(() => tcs.TrySetCanceled())) {
                        await tcs.Task;
                    }

                    return;
                }

                await Task.Yield();
                ct.ThrowIfCancellationRequested();
            });
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        await WaitForAsync(() => engine.CurrentSnapshot.Devices[0].Detail.ExactModel is not null);
        await engine.DrainAsync();
        Assert.Equal(80, engine.CurrentSnapshot.Devices[0].BatteryPercent);
        Assert.Equal("SM-A065F", engine.CurrentSnapshot.Devices[0].Detail.ExactModel);
        Assert.Equal("16", engine.CurrentSnapshot.Devices[0].Detail.AndroidVersion);

        // Subsequent battery-only refreshes fail (after probe's conceptual retry — FakeProbe is one shot).
        probe.OnCommand = (_, command) => command switch {
            MetadataCommand.Battery => new MetadataCommandResult(command, MetadataFieldStatus.Timeout, null, null),
            MetadataCommand.ExactModel => new MetadataCommandResult(command, MetadataFieldStatus.Ok, null, "SM-A065F"),
            _ => new MetadataCommandResult(command, MetadataFieldStatus.Ok, null, "16"),
        };

        int callsAfterEnrich = probe.Calls.Count;
        life.SetPresentationState(visible: true, pinned: true);
        now = T0.AddSeconds(31);
        // Re-signal visibility after age exceeds 30s while pinned.
        life.SetPresentationState(visible: false, pinned: true);
        life.SetPresentationState(visible: true, pinned: true);
        await WaitForAsync(() => engine.CurrentSnapshot.Devices[0].BatteryPercent is null);
        await engine.DrainAsync();

        Assert.Null(engine.CurrentSnapshot.Devices[0].BatteryPercent);
        Assert.Equal("SM-A065F", engine.CurrentSnapshot.Devices[0].Detail.ExactModel);
        Assert.Equal("16", engine.CurrentSnapshot.Devices[0].Detail.AndroidVersion);

        var postEnrich = probe.Calls.Skip(callsAfterEnrich).ToList();
        Assert.All(postEnrich, c => Assert.True(c.Cmd is null or MetadataCommand.Battery));
        Assert.DoesNotContain(postEnrich, c => c.Cmd is MetadataCommand.ExactModel);
        Assert.DoesNotContain(postEnrich, c => c.Cmd is MetadataCommand.AndroidVersion);

        int afterFail = probe.Calls.Count;
        Assert.Contains(delays, d => d >= TimeSpan.FromSeconds(119));
        await Task.Delay(50);
        Assert.Equal(afterFail, probe.Calls.Count);
    }

    [Fact]
    public async Task SamePercentBattery_Refresh_ResetsStaleness_NoRevision() {
        DateTimeOffset now = T0;
        var probe = new FakeProbe();
        await using var engine = new StateEngine(
            StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => now);
        using var life = new DeviceMetadataLifecycle(
            (g, e) => engine.PostMetadata(g, e), probe, () => now, (_, _) => Task.CompletedTask);
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        await WaitForAsync(() => engine.CurrentSnapshot.Devices[0].BatteryPercent is not null);
        await engine.DrainAsync();
        Assert.Equal(80, engine.CurrentSnapshot.Devices[0].BatteryPercent);
        long rev = engine.CurrentSnapshot.Revision;
        DateTimeOffset? observed1 = engine.CurrentDeviceState.MetadataSessions["usb:R8"].BatteryObservedAt;
        Assert.NotNull(observed1);

        // Same percent battery-only after age >30s.
        probe.OnCommand = (_, command) => command switch {
            MetadataCommand.Battery => new MetadataCommandResult(command, MetadataFieldStatus.Ok, 80, null),
            MetadataCommand.ExactModel => new MetadataCommandResult(command, MetadataFieldStatus.Ok, null, "SM-A065F"),
            _ => new MetadataCommandResult(command, MetadataFieldStatus.Ok, null, "16"),
        };
        int calls = probe.Calls.Count;
        now = T0.AddSeconds(31);
        life.SetPresentationState(true, false);
        await WaitForAsync(() => probe.Calls.Count > calls);
        await engine.DrainAsync();

        Assert.Equal(80, engine.CurrentSnapshot.Devices[0].BatteryPercent);
        Assert.Equal(rev, engine.CurrentSnapshot.Revision); // no snapshot churn for same %
        DateTimeOffset? observed2 = engine.CurrentDeviceState.MetadataSessions["usb:R8"].BatteryObservedAt;
        Assert.NotNull(observed2);
        Assert.True(observed2 > observed1);

        // Re-open within 30s of refreshed observation → no further battery request.
        int afterRefresh = probe.Calls.Count;
        now = observed2.Value.AddSeconds(10);
        life.SetPresentationState(false, false);
        life.SetPresentationState(true, false);
        await Task.Delay(40);
        Assert.Equal(afterRefresh, probe.Calls.Count);
    }

    [Fact]
    public async Task AP_Shutdown_Cancels_Cleanly() {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakeProbe { BlockGate = gate };
        await using var engine = new StateEngine(StateEngine.CreateDefaultSnapshot(revision: 1), getUtcNow: () => T0);
        var life = new DeviceMetadataLifecycle(
            (g, e) => engine.PostMetadata(g, e), probe, () => T0, (_, _) => Task.CompletedTask);
        engine.MetadataTargetsChanged += life.OnTargetsChanged;

        engine.Post(new DeviceListReceivedEvent(ImmutableArray.Create(Proto("R8")), T0));
        await engine.DrainAsync();
        await WaitForAsync(() => probe.InFlight == 1);

        await life.DisposeAsync();
        gate.TrySetCanceled();
        await engine.StopAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) {
            if (condition()) {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Condition not met within timeout.");
    }
}
