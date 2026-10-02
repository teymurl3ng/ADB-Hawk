namespace AdbView.Tests.State;

#pragma warning disable CA1707

using System;
using System.Collections.Immutable;
using AdbView.Adb.Model;
using AdbView.State;
using Xunit;
using DomainPhase = AdbView.State.PairingPhase;

/// <summary>
/// Decision 0014: Android closes the pairing advertisement the moment pairing completes, so an
/// offer withdrawal during an in-flight (Pairing) or successful (PairedWaitingForDevice)
/// operation is expected phone behavior. The reducer must preserve the pairing card, the
/// preserved pairing host, and the armed wait — the pre-success (AwaitingCode) withdrawal still
/// cancels.
/// </summary>
public sealed class PairingWithdrawalTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 11, 26, 0, TimeSpan.Zero);

    private static RawMdnsService PairingRow(string instance, string ipv4, int port) => new() {
        InstanceName = instance,
        ServiceType = "_adb-tls-pairing._tcp",
        Kind = MdnsServiceKind.Pairing,
        Ipv4 = ipv4,
        Port = port,
        Format = RawMdnsFormat.Fallback,
    };

    private static RawMdnsService ConnectRow(string instance, string ipv4, int port) => new() {
        InstanceName = instance,
        ServiceType = "_adb-tls-connect._tcp",
        Kind = MdnsServiceKind.Connect,
        Ipv4 = ipv4,
        Port = port,
        Format = RawMdnsFormat.Fallback,
    };

    private static DeviceEngineState ReducedTo(AppSnapshot current, DeviceEngineState state, AdbViewEvent evt) {
        var result = StateReducer.ReduceWithDeviceState(current, state, evt, T0);
        return result.DeviceState;
    }

    private static (AppSnapshot Snapshot, DeviceEngineState State) AdvanceToWaiting() {
        var snapshot = StateEngine.CreateDefaultSnapshot();
        var state = DeviceEngineState.Empty;
        var pairing = PairingRow("adb-R8VXA02SNKH-pair1", "192.168.0.53", 45321);
        var offerId = "_adb-tls-pairing._tcp\u001fadb-R8VXA02SNKH-pair1";

        // Offer appears.
        var mdnsFrame = new MdnsServicesReceivedEvent(ImmutableArray.Create(pairing), T0);
        (snapshot, state) = (snapshot with { }, ReducedTo(snapshot, state, mdnsFrame));
        snapshot = StateReducer.Reduce(snapshot, mdnsFrame, T0);

        // Pairing succeeds → wait armed, host preserved.
        var success = new PairingProgressedEvent(
            offerId,
            AdbView.Adb.Model.PairingPhase.PairedWaitingForDevice,
            null,
            AdbView.Adb.Model.PairingFailureKind.None,
            "Galaxy A06");
        var result = StateReducer.ReduceWithDeviceState(snapshot, state, success, T0.AddSeconds(1));

        return (result.Snapshot, result.DeviceState);
    }

    [Fact]
    public void Setup_AdvanceToWaitingHasCardHostAndArmedWait() {
        var (snapshot, state) = AdvanceToWaiting();

        Assert.NotNull(snapshot.Pairing);
        Assert.Equal(DomainPhase.PairedWaitingForDevice, snapshot.Pairing!.Phase);
        Assert.Equal("192.168.0.53", state.ActivePairingHost);
        Assert.True(state.ActivePairingWaitGeneration > 0);
    }

    [Fact]
    public void WithdrawalWhileWaiting_PreservesCardHostAndWait() {
        var (snapshot, state) = AdvanceToWaiting();

        // Next mdns frame: pairing row gone (Android closed the advertisement), nothing new.
        var result = StateReducer.ReduceWithDeviceState(
            snapshot,
            state,
            new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0.AddSeconds(2)),
            T0.AddSeconds(2));

        Assert.NotNull(result.Snapshot.Pairing);
        Assert.Equal(DomainPhase.PairedWaitingForDevice, result.Snapshot.Pairing!.Phase);
        Assert.Equal("192.168.0.53", result.DeviceState.ActivePairingHost);
        Assert.True(result.DeviceState.ActivePairingWaitGeneration > 0);
        Assert.Equal(GraceCommandKind.None, result.PairingWait.Kind);
    }

    [Fact]
    public void WithdrawalWhileWaiting_ThenConnectRowAppears_StateStillValidForPostPairConnect() {
        var (snapshot, state) = AdvanceToWaiting();
        var afterWithdrawal = StateReducer.ReduceWithDeviceState(
            snapshot,
            state,
            new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0.AddSeconds(2)),
            T0.AddSeconds(2));

        // The connect row appears on the same host (adb source, or fallback rows) — the
        // post-pair connect coordinator requires ActivePairingHost + a Connect-kind row.
        var connect = ConnectRow("adb-R8VXA02SNKH-28BYjJ", "192.168.0.53", 45533);
        var frame = new MdnsServicesReceivedEvent(ImmutableArray.Create(connect), T0.AddSeconds(3));
        var result = StateReducer.ReduceWithDeviceState(
            afterWithdrawal.Snapshot,
            afterWithdrawal.DeviceState,
            frame,
            T0.AddSeconds(3));

        Assert.NotNull(result.Snapshot.Pairing);
        Assert.Equal(DomainPhase.PairedWaitingForDevice, result.Snapshot.Pairing!.Phase);
        Assert.Equal("192.168.0.53", result.DeviceState.ActivePairingHost);
        // The connect row is present in the adb-source list the coordinator reads.
        Assert.Contains(result.DeviceState.LatestMdnsServices, s =>
            s.Kind == MdnsServiceKind.Connect
            && string.Equals(s.Ipv4, "192.168.0.53", StringComparison.Ordinal)
            && s.Port == 45533);
    }

    [Fact]
    public void WithdrawalDuringPairingPhase_PreservesCardAndHostForTheInFlightResult() {
        var snapshot = StateEngine.CreateDefaultSnapshot();
        var state = DeviceEngineState.Empty;
        var pairing = PairingRow("adb-R8VXA02SNKH-pair1", "192.168.0.53", 45321);
        var frame = new MdnsServicesReceivedEvent(ImmutableArray.Create(pairing), T0);
        var withOffer = StateReducer.ReduceWithDeviceState(snapshot, state, frame, T0);

        var pairingEvent = new PairingProgressedEvent(
            "_adb-tls-pairing._tcp\u001fadb-R8VXA02SNKH-pair1",
            AdbView.Adb.Model.PairingPhase.Pairing,
            null,
            AdbView.Adb.Model.PairingFailureKind.None,
            "Galaxy A06");
        var started = StateReducer.ReduceWithDeviceState(withOffer.Snapshot, withOffer.DeviceState, pairingEvent, T0);

        // Withdrawal while host:pair is in flight (attempt 3's exact shape).
        var result = StateReducer.ReduceWithDeviceState(
            started.Snapshot,
            started.DeviceState,
            new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0.AddSeconds(1)),
            T0.AddSeconds(1));

        Assert.NotNull(result.Snapshot.Pairing);
        Assert.Equal(DomainPhase.Pairing, result.Snapshot.Pairing!.Phase);
        Assert.Equal("192.168.0.53", result.DeviceState.ActivePairingHost);
    }

    [Fact]
    public void WithdrawalWhileAwaitingCode_StillCancels() {
        var snapshot = StateEngine.CreateDefaultSnapshot();
        var state = DeviceEngineState.Empty;
        var pairing = PairingRow("adb-R8VXA02SNKH-pair1", "192.168.0.53", 45321);
        var frame = new MdnsServicesReceivedEvent(ImmutableArray.Create(pairing), T0);
        snapshot = StateReducer.Reduce(snapshot, frame, T0);
        var awaiting = StateReducer.ReduceWithDeviceState(
            snapshot,
            state,
            new PairingProgressedEvent(
                "_adb-tls-pairing._tcp\u001fadb-R8VXA02SNKH-pair1",
                AdbView.Adb.Model.PairingPhase.AwaitingCode,
                null,
                AdbView.Adb.Model.PairingFailureKind.None,
                "Galaxy A06"),
            T0);
        Assert.Equal(DomainPhase.AwaitingCode, awaiting.Snapshot.Pairing!.Phase);

        var result = StateReducer.ReduceWithDeviceState(
            awaiting.Snapshot,
            awaiting.DeviceState,
            new MdnsServicesReceivedEvent(ImmutableArray<RawMdnsService>.Empty, T0.AddSeconds(1)),
            T0.AddSeconds(1));

        Assert.Null(result.Snapshot.Pairing);
        Assert.Null(result.DeviceState.ActivePairingHost);
        Assert.Equal(GraceCommandKind.Cancel, result.PairingWait.Kind);
    }
}
