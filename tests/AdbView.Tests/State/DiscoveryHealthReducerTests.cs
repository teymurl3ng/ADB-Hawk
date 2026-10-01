namespace AdbView.Tests.State;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Xunit;

public sealed class DiscoveryHealthReducerTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static DeviceView MakeDevice(TransportKind transport, DeviceStateKind state) => new() {
        Id = "d1",
        DisplayName = "Galaxy S24",
        State = state,
        Severity = SeverityKind.Positive,
        StateLabel = "Connected",
        StatusLine = "Connected · Wi-Fi",
        Guidance = null,
        Transport = transport,
        TransportLabel = transport == TransportKind.Usb ? "USB" : "Wi-Fi",
        BatteryPercent = 80,
        MetaTemplate = "Connected {age}",
        StateSince = T0,
        ConnectedSince = T0,
        Detail = new DeviceDetailView {
            Serial = null,
            ExactModel = null,
            AndroidVersion = null,
            Address = null,
            CopyFields = ImmutableArray<CopyFieldView>.Empty,
        },
    };

    private static AppSnapshot SnapshotWith(params DeviceView[] devices) =>
        StateEngine.CreateDefaultSnapshot() with {
            GeneratedAt = T0,
            Devices = ImmutableArray.Create(devices),
        };

    private static DeviceEngineState HostState(
        bool? mdnsEnabled = true,
        bool mdnsFrameArrived = true,
        ImmutableArray<RawMdnsService>? services = null,
        bool wirelessFailed = false) =>
        DeviceEngineState.Empty with {
            ServerMdnsEnabled = mdnsEnabled,
            LatestAppliedMdnsAt = mdnsFrameArrived ? T0 : null,
            LatestMdnsServices = services ?? ImmutableArray<RawMdnsService>.Empty,
            WirelessFailed = wirelessFailed,
        };

    private static RawMdnsService Service(string instance, MdnsServiceKind kind) => new() {
        InstanceName = instance,
        ServiceType = kind == MdnsServiceKind.Pairing ? "_adb-tls-pairing._tcp" : "_adb-tls-connect._tcp",
        Kind = kind,
        Format = RawMdnsFormat.ServicesList,
    };

    [Fact]
    public void CardAppearsWhenWifiDeviceConnectedAndServicesEmpty() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var evt = new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0 + TimeSpan.FromSeconds(1));

        var result = StateReducer.ReduceWithDeviceState(current, HostState(), evt, T0 + TimeSpan.FromSeconds(1));

        var health = result.Snapshot.DiscoveryHealth;
        Assert.NotNull(health);
        Assert.Equal("Wireless discovery isn't working", health!.Title);
        Assert.Equal(
            "Galaxy S24 is connected over Wi-Fi, but ADB isn't receiving device announcements on this network.",
            health.Detail);
        Assert.Empty(health.Evidence);
        Assert.Equal("Check again", health.ActionLabel);
        Assert.True(health.CanCheckAgain);
    }

    [Fact]
    public void QuietNetworkWithoutWifiDeviceShowsNothing() {
        var current = SnapshotWith();
        var evt = new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0 + TimeSpan.FromSeconds(1));

        var result = StateReducer.ReduceWithDeviceState(current, HostState(), evt, T0 + TimeSpan.FromSeconds(1));

        Assert.Null(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void UsbOnlyDeviceNeverTriggers() {
        var current = SnapshotWith(MakeDevice(TransportKind.Usb, DeviceStateKind.Connected));
        var evt = new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0 + TimeSpan.FromSeconds(1));

        var result = StateReducer.ReduceWithDeviceState(current, HostState(), evt, T0 + TimeSpan.FromSeconds(1));

        Assert.Null(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void OfflineWifiDeviceDoesNotTrigger() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Offline));
        var evt = new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0 + TimeSpan.FromSeconds(1));

        var result = StateReducer.ReduceWithDeviceState(current, HostState(), evt, T0 + TimeSpan.FromSeconds(1));

        Assert.Null(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void NoTriggerBeforeFirstCompleteServiceList() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        // Device-list event: re-runs the trigger without asserting an mDNS complete list.
        var evt = new DeviceListReceivedEvent(
            ImmutableArray<RawDevice>.Empty,
            T0 + TimeSpan.FromSeconds(1));
        var devices = ImmutableArray.Create(new RawDevice {
            Serial = "R8VXA02SNKH",
            ProtoState = 1,
            ConnectionType = 1,
            Format = RawDeviceFormat.ProtoBinary,
        });

        var withDevice = StateReducer.ReduceWithDeviceState(
            current,
            HostState(mdnsFrameArrived: false),
            new DeviceListReceivedEvent(devices, T0),
            T0);
        var result = StateReducer.ReduceWithDeviceState(
            withDevice.Snapshot,
            withDevice.DeviceState,
            evt,
            T0 + TimeSpan.FromSeconds(1));

        Assert.Null(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void NoTriggerWhenServicesExist() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var services = ImmutableArray.Create(Service("adb-R8VXA02SNKH", MdnsServiceKind.Connect));
        var evt = new MdnsServicesReceivedEvent(services, T0 + TimeSpan.FromSeconds(1));

        var result = StateReducer.ReduceWithDeviceState(current, HostState(), evt, T0 + TimeSpan.FromSeconds(1));

        Assert.Null(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void NoTriggerWhenServerReportsMdnsDisabled() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var evt = new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0 + TimeSpan.FromSeconds(1));

        var result = StateReducer.ReduceWithDeviceState(
            current,
            HostState(mdnsEnabled: false),
            evt,
            T0 + TimeSpan.FromSeconds(1));

        Assert.Null(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void NoTriggerWhenTrackerFailed() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        // Device-list event: re-runs the trigger while WirelessFailed stays set.
        var evt = new DeviceListReceivedEvent(
            ImmutableArray<RawDevice>.Empty,
            T0 + TimeSpan.FromSeconds(1));

        var result = StateReducer.ReduceWithDeviceState(
            current,
            HostState(wirelessFailed: true),
            evt,
            T0 + TimeSpan.FromSeconds(1));

        Assert.Null(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void CheckAgainDisablesWhileMeasurementRuns() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var state = HostState();
        var runEvent = new DiscoveryCheckStartedEvent();

        var running = StateReducer.ReduceWithDeviceState(current, state, runEvent, T0);

        Assert.True(running.DeviceState.DiscoveryCheckRunning);
        Assert.NotNull(running.Snapshot.DiscoveryHealth);
        Assert.False(running.Snapshot.DiscoveryHealth!.CanCheckAgain);
    }

    [Fact]
    public void DiagnosisRendersEvidenceInDecisionOrder() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var state = HostState() with { DiscoveryCheckRunning = true };
        var diagnosis = new DiscoveryDiagnosis(
            DiscoveryProbeOutcome.Measured,
            3,
            ImmutableArray.Create("msedge"),
            ImmutableArray.Create("Tailscale"));

        var result = StateReducer.ReduceWithDeviceState(
            current,
            state,
            new DiscoveryDiagnosisReceivedEvent(diagnosis),
            T0 + TimeSpan.FromSeconds(4));

        var health = result.Snapshot.DiscoveryHealth;
        Assert.NotNull(health);
        var expectedEvidence = new[] {
            "3 device announcements are visible on this network.",
            "msedge is using the discovery port.",
            "Virtual network adapters are active (Tailscale).",
        };
        Assert.Equal(expectedEvidence, health!.Evidence.ToArray());
        Assert.True(health.CanCheckAgain);
        Assert.False(result.DeviceState.DiscoveryCheckRunning);
    }

    [Fact]
    public void DiagnosisWithMultiplePortHoldersUsesPluralLine() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var diagnosis = new DiscoveryDiagnosis(
            DiscoveryProbeOutcome.Measured,
            0,
            ImmutableArray.Create("msedge", "ChatGPT", "Spotify"),
            ImmutableArray<string>.Empty);

        var result = StateReducer.ReduceWithDeviceState(
            current,
            HostState(),
            new DiscoveryDiagnosisReceivedEvent(diagnosis),
            T0);

        var evidence = result.Snapshot.DiscoveryHealth!.Evidence;
        // Evidence order: probe line first, then port holders, then virtual adapters.
        Assert.Equal("No announcements were measured just now.", evidence[0]);
        Assert.Equal("msedge and 2 other apps are using the discovery port.", evidence[1]);
    }

    [Fact]
    public void DiagnosisWithOneAdditionalHolderUsesSingularLine() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var diagnosis = new DiscoveryDiagnosis(
            DiscoveryProbeOutcome.Measured,
            0,
            ImmutableArray.Create("msedge", "ChatGPT"),
            ImmutableArray<string>.Empty);

        var result = StateReducer.ReduceWithDeviceState(
            current,
            HostState(),
            new DiscoveryDiagnosisReceivedEvent(diagnosis),
            T0);

        Assert.Equal(
            "msedge and 1 other app are using the discovery port.",
            result.Snapshot.DiscoveryHealth!.Evidence[1]);
    }

    [Fact]
    public void UnavailableProbeRendersInconclusiveLine() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var diagnosis = new DiscoveryDiagnosis(
            DiscoveryProbeOutcome.Unavailable,
            0,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);

        var result = StateReducer.ReduceWithDeviceState(
            current,
            HostState(),
            new DiscoveryDiagnosisReceivedEvent(diagnosis),
            T0);

        Assert.Equal(
            "ADB View couldn't measure the network just now.",
            result.Snapshot.DiscoveryHealth!.Evidence.Single());
    }

    [Fact]
    public void MeasuredZeroRendersMeasuredNothingLine() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var diagnosis = new DiscoveryDiagnosis(
            DiscoveryProbeOutcome.Measured,
            0,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);

        var result = StateReducer.ReduceWithDeviceState(
            current,
            HostState(),
            new DiscoveryDiagnosisReceivedEvent(diagnosis),
            T0);

        Assert.Equal(
            "No announcements were measured just now.",
            result.Snapshot.DiscoveryHealth!.Evidence.Single());
    }

    [Fact]
    public void CardAndMomentaryEvidenceClearWhenAnyServiceReturns() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var state = HostState() with {
            LatestDiscoveryDiagnosis = new DiscoveryDiagnosis(
                DiscoveryProbeOutcome.Measured,
                2,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
        };
        var services = ImmutableArray.Create(Service("adb-R8VXA02SNKH", MdnsServiceKind.Connect));
        var evt = new MdnsServicesReceivedEvent(services, T0 + TimeSpan.FromSeconds(1));

        var result = StateReducer.ReduceWithDeviceState(current, state, evt, T0 + TimeSpan.FromSeconds(1));

        Assert.Null(result.Snapshot.DiscoveryHealth);
        Assert.Null(result.DeviceState.LatestDiscoveryDiagnosis);
        Assert.False(result.DeviceState.DiscoveryCheckRunning);
    }

    [Fact]
    public void CardClearsWhenDeviceListLosesTheWifiDevice() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var evt = new DeviceListReceivedEvent(ImmutableArray<RawDevice>.Empty, T0 + TimeSpan.FromSeconds(2));

        var result = StateReducer.ReduceWithDeviceState(current, HostState(), evt, T0 + TimeSpan.FromSeconds(2));

        Assert.Empty(result.Snapshot.Devices);
        Assert.Null(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void CardClearsWhenServerReportsMdnsDisabled() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected)) with {
            DiscoveryHealth = new DiscoveryHealthView {
                Title = "Wireless discovery isn't working",
                Detail = "Galaxy S24 is connected over Wi-Fi, but ADB isn't receiving device announcements on this network.",
                Evidence = ImmutableArray<string>.Empty,
                ActionLabel = "Check again",
                CanCheckAgain = true,
                Repairs = ImmutableArray<DiscoveryRepairCardAction>.Empty,
                ResultLine = null,
            },
        };
        var status = new AdbServerStatus { MdnsEnabled = false };
        var evt = new AdbServerStateChangedEvent(
            new AdbServerStateChanged(AdbServerPhase.Up, status, ProtocolVersion: 1, Generation: 2));

        var result = StateReducer.ReduceWithDeviceState(current, HostState(), evt, T0);

        Assert.False(result.DeviceState.ServerMdnsEnabled);
        Assert.Null(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void RevisionBumpsOnlyWhenCardChanges() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var evt = new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0 + TimeSpan.FromSeconds(1));

        var first = StateReducer.ReduceWithDeviceState(current, HostState(), evt, T0 + TimeSpan.FromSeconds(1));
        var health = first.Snapshot.DiscoveryHealth!;

        // Re-apply the same event against the produced snapshot/state: view unchanged → no bump.
        var second = StateReducer.ReduceWithDeviceState(
            first.Snapshot,
            first.DeviceState,
            new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0 + TimeSpan.FromSeconds(2)),
            T0 + TimeSpan.FromSeconds(2));

        Assert.Equal(first.Snapshot.Revision, second.Snapshot.Revision);
        Assert.Equal(health, second.Snapshot.DiscoveryHealth);
    }

    // ---------- repairs (decision 0008 amendment) ----------

    [Fact]
    public void RepairCatalogRendersActionsAndCompletedOnesLeaveTheCard() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var catalog = ImmutableArray.Create(
            new DiscoveryRepairDescriptor("restart-adb", DiscoveryRepairActionKind.RestartAdb, null),
            new DiscoveryRepairDescriptor("stop-service:Tailscale", DiscoveryRepairActionKind.StopService, "Tailscale"),
            new DiscoveryRepairDescriptor("start-service:Tailscale", DiscoveryRepairActionKind.StartService, "Tailscale"));
        var state = HostState() with {
            RepairCatalog = catalog,
            RepairResults = ImmutableDictionary.CreateRange(new[] {
                new KeyValuePair<string, DiscoveryRepairOutcome>(
                    "stop-service:Tailscale", DiscoveryRepairOutcome.Completed),
            }),
        };

        // Idempotent catalog re-post (coordinator heartbeat): same catalog, view unchanged.
        var result = StateReducer.ReduceWithDeviceState(
            current,
            state,
            new DiscoveryRepairCatalogEvent(catalog),
            T0);

        var repairs = result.Snapshot.DiscoveryHealth!.Repairs;
        Assert.Equal(2, repairs.Length); // completed stop leaves; restart + undo start remain
        Assert.Equal("Restart ADB", repairs[0].Label);
        Assert.False(repairs[0].IsAdmin);
        Assert.Equal("Start Tailscale service", repairs[1].Label);
        Assert.True(repairs[1].IsAdmin);
        Assert.All(repairs, r => Assert.False(r.IsRunning));
    }

    [Fact]
    public void RepairRunningDisablesActionsAndCheckAgain() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var state = HostState() with {
            RepairCatalog = ImmutableArray.Create(
                new DiscoveryRepairDescriptor("restart-adb", DiscoveryRepairActionKind.RestartAdb, null)),
            RunningRepairId = "restart-adb",
        };

        var result = StateReducer.ReduceWithDeviceState(
            current,
            state,
            new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0),
            T0);

        var view = result.Snapshot.DiscoveryHealth!;
        Assert.True(view.Repairs[0].IsRunning);
        Assert.Equal("Restarting ADB…", view.Repairs[0].RunningLabel);
        Assert.False(view.CanCheckAgain);
    }

    [Fact]
    public void CompletedRepairWaitsForMeasurementAndReportsStillBrokenOnce() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var state = HostState() with {
            RepairCatalog = ImmutableArray.Create(
                new DiscoveryRepairDescriptor("restart-adb", DiscoveryRepairActionKind.RestartAdb, null)),
        };
        var now = T0;

        var running = StateReducer.ReduceWithDeviceState(
            current, state, new DiscoveryRepairStartedEvent("restart-adb"), now);
        var finished = StateReducer.ReduceWithDeviceState(
            running.Snapshot,
            running.DeviceState,
            new DiscoveryRepairFinishedEvent("restart-adb", DiscoveryRepairOutcome.Completed),
            now);
        Assert.Equal("restart-adb", finished.DeviceState.AwaitingMeasureRepairId);
        Assert.Null(finished.Snapshot.DiscoveryHealth!.ResultLine);

        var measured = StateReducer.ReduceWithDeviceState(
            finished.Snapshot,
            finished.DeviceState,
            new DiscoveryDiagnosisReceivedEvent(new DiscoveryDiagnosis(
                DiscoveryProbeOutcome.Measured, 0,
                ImmutableArray<string>.Empty, ImmutableArray<string>.Empty)),
            now);
        Assert.Equal("Still not working after this action.", measured.Snapshot.DiscoveryHealth!.ResultLine);
        Assert.Null(measured.DeviceState.AwaitingMeasureRepairId);

        // Momentary: the next measurement (not following a repair) clears the line.
        var remeasured = StateReducer.ReduceWithDeviceState(
            measured.Snapshot,
            measured.DeviceState,
            new DiscoveryDiagnosisReceivedEvent(new DiscoveryDiagnosis(
                DiscoveryProbeOutcome.Measured, 1,
                ImmutableArray<string>.Empty, ImmutableArray<string>.Empty)),
            now);
        Assert.Null(remeasured.Snapshot.DiscoveryHealth!.ResultLine);
    }

    [Fact]
    public void DeclinedRepairShowsMomentaryLineWithoutMeasurement() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var state = HostState() with {
            RepairCatalog = ImmutableArray.Create(
                new DiscoveryRepairDescriptor("stop-service:Tailscale", DiscoveryRepairActionKind.StopService, "Tailscale")),
            RunningRepairId = "stop-service:Tailscale",
        };

        var finished = StateReducer.ReduceWithDeviceState(
            current,
            state,
            new DiscoveryRepairFinishedEvent("stop-service:Tailscale", DiscoveryRepairOutcome.Declined),
            T0);

        Assert.Equal(
            "Approval was declined, so nothing was changed.",
            finished.Snapshot.DiscoveryHealth!.ResultLine);
        Assert.Null(finished.DeviceState.AwaitingMeasureRepairId);
        // Declined actions return to Available (no Completed record hides them).
        Assert.False(finished.DeviceState.RepairResults.ContainsKey("stop-service:Tailscale")
            && finished.DeviceState.RepairResults["stop-service:Tailscale"] == DiscoveryRepairOutcome.Completed);
    }

    [Fact]
    public void NewRepairAttemptClearsThePreviousResultLine() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var state = HostState() with {
            RepairCatalog = ImmutableArray.Create(
                new DiscoveryRepairDescriptor("restart-adb", DiscoveryRepairActionKind.RestartAdb, null)),
            RepairResultLine = "Still not working after this action.",
        };

        var started = StateReducer.ReduceWithDeviceState(
            current,
            state,
            new DiscoveryRepairStartedEvent("restart-adb"),
            T0);

        Assert.Null(started.Snapshot.DiscoveryHealth!.ResultLine);
        Assert.Equal("restart-adb", started.DeviceState.RunningRepairId);
    }

    [Fact]
    public void SecondRepairStartWhileOneRunsIsIgnored() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var state = HostState() with { RunningRepairId = "restart-adb" };

        var result = StateReducer.ReduceWithDeviceState(
            current,
            state,
            new DiscoveryRepairStartedEvent("stop-service:Tailscale"),
            T0);

        Assert.Equal("restart-adb", result.DeviceState.RunningRepairId);
    }

    [Fact]
    public void CardClearDropsRepairState() {
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var state = HostState() with {
            RepairCatalog = ImmutableArray.Create(
                new DiscoveryRepairDescriptor("restart-adb", DiscoveryRepairActionKind.RestartAdb, null)),
            RepairResultLine = "Still not working after this action.",
            RunningRepairId = "restart-adb",
        };
        var services = ImmutableArray.Create(Service("adb-R8VXA02SNKH", MdnsServiceKind.Connect));

        var result = StateReducer.ReduceWithDeviceState(
            current,
            state,
            new MdnsServicesReceivedEvent(services, T0 + TimeSpan.FromSeconds(1)),
            T0 + TimeSpan.FromSeconds(1));

        Assert.Null(result.Snapshot.DiscoveryHealth);
        Assert.True(result.DeviceState.RepairCatalog.IsEmpty);
        Assert.True(result.DeviceState.RepairResults.IsEmpty);
        Assert.Null(result.DeviceState.RunningRepairId);
        Assert.Null(result.DeviceState.AwaitingMeasureRepairId);
        Assert.Null(result.DeviceState.RepairResultLine);
    }

    // ---------- fallback browser merge (decision 0008 follow-through) ----------

    private static RawMdnsService FallbackService(string instance, MdnsServiceKind kind) => new() {
        InstanceName = instance,
        ServiceType = kind == MdnsServiceKind.Pairing ? "_adb-tls-pairing._tcp" : "_adb-tls-connect._tcp",
        Kind = kind,
        Ipv4 = "192.168.18.248",
        Port = 41953,
        ProductModel = "SM-A065F",
        Format = RawMdnsFormat.Fallback,
    };

    [Fact]
    public void FallbackRowsFillOffersWhenAdbReportsEmpty() {
        var current = SnapshotWith(MakeDevice(TransportKind.Usb, DeviceStateKind.Connected));
        var fallback = ImmutableArray.Create(FallbackService("adb-abc123", MdnsServiceKind.Pairing));
        var now = T0 + TimeSpan.FromSeconds(1);

        var result = StateReducer.ReduceWithDeviceState(
            current,
            HostState(),
            new MdnsFallbackReceivedEvent(fallback, now),
            now);

        Assert.Single(result.Snapshot.PairingOffers);
        Assert.Null(result.Snapshot.DiscoveryHealth); // no Wi-Fi device connected: trigger absent anyway
        Assert.False(result.DeviceState.LatestFallbackServices.IsEmpty);
        // The fallback must never make the ADB source look healthy.
        Assert.Equal(T0, result.DeviceState.LatestAppliedMdnsAt);
    }

    [Fact]
    public void AdbNonEmptyListWinsOverFallbackRows() {
        var current = SnapshotWith(MakeDevice(TransportKind.Usb, DeviceStateKind.Connected));
        var adbServices = ImmutableArray.Create(FallbackService("adb-from-adb", MdnsServiceKind.Pairing));
        var fallback = ImmutableArray.Create(FallbackService("adb-from-fallback", MdnsServiceKind.Pairing));
        var now = T0 + TimeSpan.FromSeconds(1);

        var withAdb = StateReducer.ReduceWithDeviceState(
            current, HostState(), new MdnsServicesReceivedEvent(adbServices, now), now);
        var result = StateReducer.ReduceWithDeviceState(
            withAdb.Snapshot,
            withAdb.DeviceState,
            new MdnsFallbackReceivedEvent(fallback, now + TimeSpan.FromSeconds(1)),
            now + TimeSpan.FromSeconds(1));

        Assert.Single(result.Snapshot.PairingOffers);
        // Offer ids are DiscoveryKeys: "<service-type><instance>" (ENGINEERING §6.6).
        string adbOfferId = "_adb-tls-pairing._tcpadb-from-adb";
        Assert.Equal(adbOfferId, result.DeviceState.OfferBindings.Keys.Single());
    }

    [Fact]
    public void FallbackRowsDoNotSatisfyBrokenDiscoveryTrigger() {
        // Wi-Fi device connected + adb empty + healthy server: the Step 1 trigger fires even
        // when the fallback browser sees services (the card audits the ADB source).
        var current = SnapshotWith(MakeDevice(TransportKind.WiFi, DeviceStateKind.Connected));
        var fallback = ImmutableArray.Create(FallbackService("adb-abc123", MdnsServiceKind.Pairing));
        var now = T0 + TimeSpan.FromSeconds(1);

        var result = StateReducer.ReduceWithDeviceState(
            current,
            HostState(),
            new MdnsFallbackReceivedEvent(fallback, now),
            now);

        Assert.NotNull(result.Snapshot.DiscoveryHealth);
    }

    [Fact]
    public void FallbackWithdrawalClearsOffersWhenAdbStillEmpty() {
        var current = SnapshotWith(MakeDevice(TransportKind.Usb, DeviceStateKind.Connected));
        var fallback = ImmutableArray.Create(FallbackService("adb-abc123", MdnsServiceKind.Pairing));
        var now = T0 + TimeSpan.FromSeconds(1);

        var withFallback = StateReducer.ReduceWithDeviceState(
            current,
            HostState(),
            new MdnsFallbackReceivedEvent(fallback, now),
            now);
        var cleared = StateReducer.ReduceWithDeviceState(
            withFallback.Snapshot,
            withFallback.DeviceState,
            new MdnsFallbackReceivedEvent(ImmutableArray<RawMdnsService>.Empty, now + TimeSpan.FromSeconds(1)),
            now + TimeSpan.FromSeconds(1));

        Assert.Empty(cleared.Snapshot.PairingOffers);
    }

    [Fact]
    public void FallbackDoesNotClearTrackerFailureWarnings() {
        var current = SnapshotWith(MakeDevice(TransportKind.Usb, DeviceStateKind.Connected));
        var fallback = ImmutableArray.Create(FallbackService("adb-abc123", MdnsServiceKind.Pairing));
        var now = T0 + TimeSpan.FromSeconds(1);

        var failed = StateReducer.ReduceWithDeviceState(
            current,
            HostState(),
            new MdnsTrackerFailedEvent("stream EOF"),
            now);
        var withWarning = StateReducer.ReduceWithDeviceState(
            failed.Snapshot,
            failed.DeviceState,
            new WirelessDiscoveryRestoringExpiredEvent(failed.DeviceState.ActiveWirelessRestoringGeneration),
            now);
        var result = StateReducer.ReduceWithDeviceState(
            withWarning.Snapshot,
            withWarning.DeviceState,
            new MdnsFallbackReceivedEvent(fallback, now + TimeSpan.FromSeconds(1)),
            now + TimeSpan.FromSeconds(1));

        // The tracker is still failed; fallback rows must not look like tracker recovery.
        Assert.True(result.DeviceState.WirelessFailed);
        Assert.NotEmpty(result.Snapshot.Warnings);
    }
}
