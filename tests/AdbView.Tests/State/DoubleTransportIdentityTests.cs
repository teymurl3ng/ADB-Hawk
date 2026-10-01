namespace AdbView.Tests.State;

using System;
using System.Collections.Immutable;
using AdbView.Adb.Model;
using AdbView.State;
using Xunit;

/// <summary>
/// Decision 0011: two adb transports (`ip:portA` + `ip:portB`) for one physical phone must
/// collapse to ONE card when the effective discovery set (adb source, else fallback rows)
/// carries the connect row that resolves both to the same hardware serial — including when the
/// ADB server's own browse is empty and only the fallback browser saw the announcement.
/// </summary>
public sealed class DoubleTransportIdentityTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static RawDevice WifiDevice(string endpointSerial) => new() {
        Serial = endpointSerial,
        ProtoState = 1, // Connected
        ConnectionType = 2, // SOCKET → Wi-Fi
        Format = RawDeviceFormat.ProtoBinary,
    };

    private static RawMdnsService FallbackConnectRow(string instance, string ipv4, int port, string serial) => new() {
        InstanceName = instance,
        ServiceType = "_adb-tls-connect._tcp",
        Kind = MdnsServiceKind.Connect,
        Ipv4 = ipv4,
        Port = port,
        Serial = serial,
        Format = RawMdnsFormat.Fallback,
    };

    private static DeviceEngineState StateWithFallback(ImmutableArray<RawMdnsService> fallbackRows) =>
        DeviceEngineState.Empty with {
            ServerMdnsEnabled = true,
            LatestAppliedMdnsAt = T0,
            LatestMdnsServices = ImmutableArray<RawMdnsService>.Empty, // broken browse
            LatestFallbackServices = fallbackRows,
        };

    [Fact]
    public void TwoTransportsCollapseToOneCardViaFallbackRows() {
        // Same phone: two live transports on different ports; the fallback browser saw the
        // connect announcement (adb browse empty). Both resolve to hardware serial R5CW1234.
        var devices = ImmutableArray.Create(
            WifiDevice("192.168.18.248:41953"),
            WifiDevice("192.168.18.248:39001"));
        var fallback = ImmutableArray.Create(
            FallbackConnectRow("adb-R5CW1234-abc123", "192.168.18.248", 41953, "R5CW1234"),
            FallbackConnectRow("adb-R5CW1234-abc123", "192.168.18.248", 39001, "R5CW1234"));

        var result = StateReducer.ReduceWithDeviceState(
            StateEngine.CreateDefaultSnapshot(),
            StateWithFallback(fallback),
            new DeviceListReceivedEvent(devices, T0),
            T0);

        var card = Assert.Single(result.Snapshot.Devices);
        Assert.Equal(TransportKind.WiFi, card.Transport);
        Assert.Equal("wifi:R5CW1234", card.Id);
    }

    [Fact]
    public void SameScenarioWithAdbSourceRowsAlsoCollapses() {
        var devices = ImmutableArray.Create(
            WifiDevice("192.168.18.248:41953"),
            WifiDevice("192.168.18.248:39001"));
        var adbRows = ImmutableArray.Create(
            FallbackConnectRow("adb-R5CW1234-abc123", "192.168.18.248", 41953, "R5CW1234"),
            FallbackConnectRow("adb-R5CW1234-abc123", "192.168.18.248", 39001, "R5CW1234"));
        var state = StateWithFallback(ImmutableArray<RawMdnsService>.Empty) with {
            LatestMdnsServices = adbRows,
        };

        var result = StateReducer.ReduceWithDeviceState(
            StateEngine.CreateDefaultSnapshot(),
            state,
            new DeviceListReceivedEvent(devices, T0),
            T0);

        Assert.Single(result.Snapshot.Devices);
    }

    [Fact]
    public void WithoutAnyDiscoveryRowsTwoTransportsRemainTwoCards() {
        // Honest degradation: no discovery data at all → per-port identities, two cards
        // (pre-0011 behavior; unchanged).
        var devices = ImmutableArray.Create(
            WifiDevice("192.168.18.248:41953"),
            WifiDevice("192.168.18.248:39001"));

        var result = StateReducer.ReduceWithDeviceState(
            StateEngine.CreateDefaultSnapshot(),
            DeviceEngineState.Empty,
            new DeviceListReceivedEvent(devices, T0),
            T0);

        Assert.Equal(2, result.Snapshot.Devices.Length);
    }

    [Fact]
    public void UsbAndWifiSameSerialRemainTwoCards() {
        // Pinned §6.7 behavior: one card per transport across USB/Wi-Fi.
        var devices = ImmutableArray.Create(
            WifiDevice("192.168.18.248:41953"),
            new RawDevice {
                Serial = "R5CW1234",
                ProtoState = 1,
                ConnectionType = 1, // USB
                Format = RawDeviceFormat.ProtoBinary,
            });
        var fallback = ImmutableArray.Create(
            FallbackConnectRow("adb-R5CW1234-abc123", "192.168.18.248", 41953, "R5CW1234"));

        var result = StateReducer.ReduceWithDeviceState(
            StateEngine.CreateDefaultSnapshot(),
            StateWithFallback(fallback),
            new DeviceListReceivedEvent(devices, T0),
            T0);

        Assert.Equal(2, result.Snapshot.Devices.Length);
    }
}
