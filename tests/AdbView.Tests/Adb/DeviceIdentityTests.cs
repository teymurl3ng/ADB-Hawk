#pragma warning disable CA1707
namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AdbView.Adb;
using AdbView.Adb.Model;
using Xunit;

public sealed class DeviceIdentityTests {
    private static RawDevice Proto(
        string serial,
        int state,
        int? connectionType = 1,
        string? product = null,
        string? model = null,
        string? device = null) =>
        new() {
            Serial = serial,
            ProtoState = state,
            StateToken = null,
            ConnectionType = connectionType,
            Product = product,
            Model = model,
            Device = device,
            Format = RawDeviceFormat.ProtoBinary,
        };

    private static RawDevice Text(
        string serial,
        string stateToken,
        string? product = null,
        string? model = null,
        string? device = null) =>
        new() {
            Serial = serial,
            ProtoState = null,
            StateToken = stateToken,
            ConnectionType = null,
            Product = product,
            Model = model,
            Device = device,
            Format = RawDeviceFormat.TrackDevicesL,
        };

    // ---------- A–L proto state mapping ----------

    [Theory]
    [InlineData(8, (int)DeviceSemanticState.Connected)]       // A DEVICE
    [InlineData(3, (int)DeviceSemanticState.Unauthorized)]    // B
    [InlineData(6, (int)DeviceSemanticState.Offline)]         // C
    [InlineData(10, (int)DeviceSemanticState.Recovery)]       // D
    [InlineData(7, (int)DeviceSemanticState.Bootloader)]      // E
    [InlineData(11, (int)DeviceSemanticState.Sideload)]       // F
    [InlineData(12, (int)DeviceSemanticState.Rescue)]         // G
    [InlineData(2, (int)DeviceSemanticState.Authorizing)]     // H
    [InlineData(1, (int)DeviceSemanticState.Connecting)]      // I
    [InlineData(5, (int)DeviceSemanticState.Detached)]        // J
    [InlineData(0, (int)DeviceSemanticState.Unknown)]         // K ANY
    [InlineData(4, (int)DeviceSemanticState.Unknown)]         // K NOPERMISSION
    [InlineData(9, (int)DeviceSemanticState.Unknown)]         // K HOST
    [InlineData(99, (int)DeviceSemanticState.Unknown)]        // L future
    public void ProtoState_MapsToSemanticState(int protoState, int expected) {
        var identity = new DeviceIdentity();
        var n = identity.Normalize(Proto("S1", protoState));
        Assert.Equal((DeviceSemanticState)expected, n.SemanticState);
    }

    // ---------- M–P text state mapping ----------

    [Theory]
    [InlineData("device", (int)DeviceSemanticState.Connected)]
    [InlineData("unauthorized", (int)DeviceSemanticState.Unauthorized)]
    [InlineData("offline", (int)DeviceSemanticState.Offline)]
    [InlineData("recovery", (int)DeviceSemanticState.Recovery)]
    [InlineData("bootloader", (int)DeviceSemanticState.Bootloader)]
    [InlineData("sideload", (int)DeviceSemanticState.Sideload)]
    [InlineData("rescue", (int)DeviceSemanticState.Rescue)]
    [InlineData("authorizing", (int)DeviceSemanticState.Authorizing)]
    [InlineData("connecting", (int)DeviceSemanticState.Connecting)]
    [InlineData("detached", (int)DeviceSemanticState.Detached)]
    [InlineData("future_state_xyz", (int)DeviceSemanticState.Unknown)]
    public void TextState_MapsToSemanticState(string token, int expected) {
        var identity = new DeviceIdentity();
        var n = identity.Normalize(Text("S1", token));
        Assert.Equal((DeviceSemanticState)expected, n.SemanticState);
    }

    [Fact]
    public void UnknownTextState_DoesNotThrow() {
        var identity = new DeviceIdentity();
        var n = identity.Normalize(Text("S1", "not-a-real-state"));
        Assert.Equal(DeviceSemanticState.Unknown, n.SemanticState);
    }

    // ---------- Q–T transport ----------

    [Fact]
    public void ExplicitUsb_ConnectionType_MapsToUsb() {
        var n = new DeviceIdentity().Normalize(Proto("S1", 8, connectionType: 1));
        Assert.Equal(DeviceTransport.Usb, n.Transport);
        Assert.Equal("usb:S1", n.Id);
    }

    [Fact]
    public void ExplicitSocket_ConnectionType_MapsToWiFi() {
        var n = new DeviceIdentity().Normalize(Proto("S1", 8, connectionType: 2));
        Assert.Equal(DeviceTransport.WiFi, n.Transport);
        Assert.Equal("wifi:S1", n.Id);
    }

    [Fact]
    public void ExplicitUnknown_ConnectionType_MapsToUnknown() {
        var n = new DeviceIdentity().Normalize(Proto("S1", 8, connectionType: 0));
        Assert.Equal(DeviceTransport.Unknown, n.Transport);
        Assert.Equal("unk:S1", n.Id);
    }

    [Fact]
    public void ExplicitConnectionType_BeatsSerialShape_HostPortRemainsUnknownTransport() {
        // §6.7 rule 1: absent connection_type → Unknown (no invented Wi-Fi from host:port).
        var text = new DeviceIdentity().Normalize(Text("192.168.0.2:37000", "device"));
        Assert.Equal(DeviceTransport.Unknown, text.Transport);
        Assert.Equal("unk:192.168.0.2:37000", text.Id);

        // Explicit USB on a host:port-shaped serial still wins.
        var usb = new DeviceIdentity().Normalize(Proto("192.168.0.2:37000", 8, connectionType: 1));
        Assert.Equal(DeviceTransport.Usb, usb.Transport);
        Assert.Equal("usb:192.168.0.2:37000", usb.Id);
    }

    [Fact]
    public void TextFallback_AbsentConnectionType_IsUnknownTransport() {
        var n = new DeviceIdentity().Normalize(Text("R8VXA02SNKH", "device"));
        Assert.Null(Text("R8VXA02SNKH", "device").ConnectionType);
        Assert.Equal(DeviceTransport.Unknown, n.Transport);
    }

    /// <summary>
    /// §6.7 rule 1: absent → Unknown on every text-path serial shape.
    /// §6.11 does not define an implementable transport-from-serial algorithm.
    /// </summary>
    [Theory]
    [InlineData("R8VXA02SNKH", "R8VXA02SNKH", "unk:R8VXA02SNKH")]
    [InlineData("192.168.0.2:37000", "192.168.0.2:37000", "unk:192.168.0.2:37000")]
    [InlineData("[2001:db8::1]:5555", "[2001:db8::1]:5555", "unk:[2001:db8::1]:5555")]
    [InlineData("adb-R8VXA02SNKH-xyz1", "R8VXA02SNKH", "unk:R8VXA02SNKH")]
    [InlineData("emulator-5554", "emulator-5554", "unk:emulator-5554")]
    public void TextFallback_AllSerialShapes_TransportUnknown(
        string serial,
        string expectedHardware,
        string expectedId) {
        var n = new DeviceIdentity().Normalize(Text(serial, "device"));
        Assert.Equal(DeviceTransport.Unknown, n.Transport);
        Assert.Equal(expectedHardware, n.HardwareSerial);
        Assert.Equal(expectedId, n.Id);
    }

    [Fact]
    public void ExplicitUsb_OverridesEndpointLookingSerial() {
        var n = new DeviceIdentity().Normalize(Proto("10.0.0.5:5555", 8, connectionType: 1));
        Assert.Equal(DeviceTransport.Usb, n.Transport);
        Assert.Equal("usb:10.0.0.5:5555", n.Id);
    }

    [Fact]
    public void ExplicitSocket_OverridesOrdinaryLookingSerial() {
        var n = new DeviceIdentity().Normalize(Proto("R8VXA02SNKH", 8, connectionType: 2));
        Assert.Equal(DeviceTransport.WiFi, n.Transport);
        Assert.Equal("wifi:R8VXA02SNKH", n.Id);
    }

    [Fact]
    public void ExplicitUsb_OverridesAdbMdnsStyleSerial() {
        var n = new DeviceIdentity().Normalize(Proto("adb-R8VXA02SNKH-xyz1", 8, connectionType: 1));
        Assert.Equal(DeviceTransport.Usb, n.Transport);
        Assert.Equal("R8VXA02SNKH", n.HardwareSerial);
        Assert.Equal("usb:R8VXA02SNKH", n.Id);
    }

    [Fact]
    public void ExplicitSocket_OverridesEmulatorSerial() {
        var n = new DeviceIdentity().Normalize(Proto("emulator-5554", 8, connectionType: 2));
        Assert.Equal(DeviceTransport.WiFi, n.Transport);
        Assert.Equal("wifi:emulator-5554", n.Id);
    }

    // ---------- U–Z identity ----------

    [Fact]
    public void OrdinaryHardwareSerial_Preserved() {
        var n = new DeviceIdentity().Normalize(Proto("R8VXA02SNKH", 8, connectionType: 1));
        Assert.Equal("R8VXA02SNKH", n.HardwareSerial);
        Assert.Equal("usb:R8VXA02SNKH", n.Id);
    }

    [Fact]
    public void AdbMdnsStyleSerial_ExtractsHardwareSerial() {
        var n = new DeviceIdentity().Normalize(Proto("adb-R8VXA02SNKH-xyz1", 8, connectionType: 2));
        Assert.Equal("R8VXA02SNKH", n.HardwareSerial);
        Assert.Equal("wifi:R8VXA02SNKH", n.Id);
    }

    [Fact]
    public void AdbMdnsServiceInstanceWithDnsSdSuffix_ExtractsHardwareSerial() {
        var n = new DeviceIdentity().Normalize(Proto(
            "adb-R8VXA02SNKH-28BYjJ (2)._adb-tls-connect._tcp",
            8,
            connectionType: 2));
        Assert.Equal("R8VXA02SNKH", n.HardwareSerial);
        Assert.Equal("wifi:R8VXA02SNKH", n.Id);
        Assert.Equal("adb-R8VXA02SNKH-28BYjJ (2)._adb-tls-connect._tcp", n.AdbSerial);
    }

    [Fact]
    public void HostPort_RemainsHostPort_InPhase5() {
        var n = new DeviceIdentity().Normalize(Proto("10.0.0.5:5555", 8, connectionType: 2));
        Assert.Equal("10.0.0.5:5555", n.HardwareSerial);
        Assert.Equal("wifi:10.0.0.5:5555", n.Id);
    }

    [Fact]
    public void Ipv6BracketedEndpoint_PreservedAsHardwareSerial() {
        var n = new DeviceIdentity().Normalize(Proto("[2001:db8::1]:5555", 8, connectionType: 2));
        Assert.Equal("[2001:db8::1]:5555", n.HardwareSerial);
        Assert.Equal("wifi:[2001:db8::1]:5555", n.Id);
    }

    [Fact]
    public void EmulatorSerial_PreservedVerbatim() {
        var n = new DeviceIdentity().Normalize(Proto("emulator-5554", 8, connectionType: 2));
        Assert.Equal("emulator-5554", n.HardwareSerial);
        Assert.Equal("wifi:emulator-5554", n.Id);
    }

    [Fact]
    public void SamePhysicalSerial_UsbAndWifi_ProduceDistinctIds() {
        var identity = new DeviceIdentity();
        var usb = identity.Normalize(Proto("ABC123", 8, connectionType: 1));
        var wifi = identity.Normalize(Proto("ABC123", 8, connectionType: 2));
        Assert.Equal("usb:ABC123", usb.Id);
        Assert.Equal("wifi:ABC123", wifi.Id);
        Assert.NotEqual(usb.Id, wifi.Id);
    }

    // ---------- AB display name ----------

    [Fact]
    public void DisplayName_ModelBeatsProductBeatsSerial() {
        var identity = new DeviceIdentity();
        Assert.Equal(
            "SM_A065F",
            identity.Normalize(Proto("S", 8, product: "a06xx", model: "SM_A065F")).DisplayName);
        Assert.Equal(
            "a06xx",
            identity.Normalize(Proto("S", 8, product: "a06xx", model: null)).DisplayName);
        Assert.Equal(
            "S",
            identity.Normalize(Proto("S", 8, product: null, model: null)).DisplayName);
    }

    // ---------- AC unknown diagnostic once ----------

    [Fact]
    public void UnknownStateDiagnostic_OncePerDistinctValue() {
        var logs = new List<string>();
        var identity = new DeviceIdentity(logs.Add);

        identity.Normalize(Text("A", "weird_one"));
        identity.Normalize(Text("B", "weird_one"));
        identity.Normalize(Proto("C", 99));
        identity.Normalize(Proto("D", 99));
        identity.Normalize(Text("E", "weird_two"));

        Assert.Equal(3, logs.Count);
        Assert.Contains("text:weird_one", logs);
        Assert.Contains("proto:99", logs);
        Assert.Contains("text:weird_two", logs);
    }

    [Fact]
    public void KnownUnknownMappings_AnyNoPermissionHost_DoNotDiagnose() {
        var logs = new List<string>();
        var identity = new DeviceIdentity(logs.Add);
        identity.Normalize(Proto("A", 0));
        identity.Normalize(Proto("B", 4));
        identity.Normalize(Proto("C", 9));
        Assert.Empty(logs);
    }

    // ---------- AD no timestamps ----------

    [Fact]
    public void NormalizedDevice_HasNoTimestampProperties() {
        var props = typeof(NormalizedDevice).GetProperties();
        Assert.DoesNotContain(props, p => p.Name is "StateSince" or "ConnectedSince");
        Assert.DoesNotContain(props, p => p.Name.EndsWith("Since", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(props, p => p.PropertyType == typeof(DateTimeOffset));
        Assert.DoesNotContain(props, p => p.PropertyType == typeof(DateTimeOffset?));
        Assert.DoesNotContain(props, p => p.PropertyType == typeof(DateTime));
        Assert.DoesNotContain(props, p => p.PropertyType == typeof(DateTime?));
    }

    // ---------- AE no State/UI deps (source scan covered by boundaries; smoke here) ----------

    [Fact]
    public void NormalizeList_PreservesDuplicateIds_WithoutMergingOrSuffix() {
        var identity = new DeviceIdentity();
        var list = identity.NormalizeList(ImmutableArray.Create(
            Proto("SAME", 8, connectionType: 1),
            Proto("SAME", 8, connectionType: 1)));

        Assert.Equal(2, list.Length);
        Assert.Equal("usb:SAME", list[0].Id);
        Assert.Equal("usb:SAME", list[1].Id);
    }

    [Fact]
    public void NormalizeList_UsbAndWifiSameSerial_BothPreserved() {
        var identity = new DeviceIdentity();
        var list = identity.NormalizeList(ImmutableArray.Create(
            Proto("SAME", 8, connectionType: 1),
            Proto("SAME", 8, connectionType: 2)));

        Assert.Equal(2, list.Length);
        Assert.Equal("usb:SAME", list[0].Id);
        Assert.Equal("wifi:SAME", list[1].Id);
    }

    [Fact]
    public void ProductModelDevice_PreservedRaw() {
        var n = new DeviceIdentity().Normalize(
            Proto("S", 8, product: "a06xx", model: "SM_A065F", device: "a06"));
        Assert.Equal("a06xx", n.Product);
        Assert.Equal("SM_A065F", n.Model);
        Assert.Equal("a06", n.Device);
    }

    // ---------- ENGINEERING §16.1 identity matrix ----------

    [Theory]
    [InlineData("R8VXA02SNKH", 1, "R8VXA02SNKH", "usb:R8VXA02SNKH", (int)DeviceTransport.Usb)]
    [InlineData("adb-R8VXA02SNKH-xyz1", 2, "R8VXA02SNKH", "wifi:R8VXA02SNKH", (int)DeviceTransport.WiFi)]
    [InlineData("192.168.0.2:37000", 2, "192.168.0.2:37000", "wifi:192.168.0.2:37000", (int)DeviceTransport.WiFi)]
    [InlineData("10.0.0.5:5555", 0, "10.0.0.5:5555", "unk:10.0.0.5:5555", (int)DeviceTransport.Unknown)]
    [InlineData("[2001:db8::1]:5555", 2, "[2001:db8::1]:5555", "wifi:[2001:db8::1]:5555", (int)DeviceTransport.WiFi)]
    [InlineData("emulator-5554", 2, "emulator-5554", "wifi:emulator-5554", (int)DeviceTransport.WiFi)]
    [InlineData("emulator-5554", 1, "emulator-5554", "usb:emulator-5554", (int)DeviceTransport.Usb)]
    public void Engineering_16_1_IdentityCases(
        string serial,
        int connectionType,
        string expectedHardware,
        string expectedId,
        int expectedTransport) {
        var n = new DeviceIdentity().Normalize(Proto(serial, 8, connectionType: connectionType));
        Assert.Equal(expectedHardware, n.HardwareSerial);
        Assert.Equal(expectedId, n.Id);
        Assert.Equal((DeviceTransport)expectedTransport, n.Transport);
    }

    [Fact]
    public void Engineering_16_1_IpPort_WithoutMdns_KeepsEndpointIdentity() {
        // Phase 5: no mDNS connect lookup — host:port stays hardware identity.
        var n = new DeviceIdentity().Normalize(Proto("192.168.1.10:5555", 8, connectionType: 2));
        Assert.Equal("192.168.1.10:5555", n.HardwareSerial);
        Assert.Equal("wifi:192.168.1.10:5555", n.Id);
    }

    [Fact]
    public void Engineering_16_1_DuplicateSerialsAcrossTransports() {
        var identity = new DeviceIdentity();
        var usb = identity.Normalize(Proto("DUPE", 8, connectionType: 1));
        var wifiFromAdb = identity.Normalize(Proto("adb-DUPE-abcd", 8, connectionType: 2));
        Assert.Equal("usb:DUPE", usb.Id);
        Assert.Equal("wifi:DUPE", wifiFromAdb.Id);
        Assert.Equal(usb.HardwareSerial, wifiFromAdb.HardwareSerial);
        Assert.NotEqual(usb.Id, wifiFromAdb.Id);
    }
}
