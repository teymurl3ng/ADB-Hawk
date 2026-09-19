#pragma warning disable CA1707

namespace AdbView.Tests.State;

using System;
using System.Collections.Immutable;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Xunit;

/// <summary>Defect 4: transport selector ≠ display Serial.</summary>
public sealed class DeviceViewMapperSerialTests {
    private static readonly DeviceIdentity Identity = new();

    [Fact]
    public void Usb_Shows_Hardware_Serial() {
        var raw = Raw("R8VXA02SNKH", connectionType: 1);
        var view = Map(raw);
        Assert.Equal("R8VXA02SNKH", view.Detail.Serial);
        Assert.Null(view.Detail.Address);
        Assert.Contains(view.Detail.CopyFields, f => f.Key == "serial" && f.Value == "R8VXA02SNKH");
    }

    [Fact]
    public void Wifi_Endpoint_Only_Shows_Address_Omits_Serial() {
        var raw = Raw("192.168.18.248:5555", connectionType: 2);
        var view = Map(raw);
        Assert.Null(view.Detail.Serial);
        Assert.Equal("192.168.18.248:5555", view.Detail.Address);
        Assert.DoesNotContain(view.Detail.CopyFields, f => f.Key == "serial");
        Assert.Contains(view.Detail.CopyFields, f => f.Key == "address" && f.Value == "192.168.18.248:5555");
        Assert.StartsWith("wifi:", view.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void Wifi_With_Real_Hardware_Serial_Shows_Both() {
        var mdns = ImmutableArray.Create(new RawMdnsService {
            InstanceName = "phone",
            ServiceType = MdnsServiceTypes.Connect,
            Kind = MdnsServiceKind.Connect,
            Ipv4 = "192.168.18.248",
            Ipv6 = ImmutableArray<string>.Empty,
            Port = 5555,
            Serial = "R8VXA02SNKH",
            Format = RawMdnsFormat.ProtoBinary,
        });
        var raw = Raw("192.168.18.248:5555", connectionType: 2);
        var normalized = Identity.Normalize(raw, mdns);
        Assert.Equal("R8VXA02SNKH", normalized.HardwareSerial);
        Assert.Equal("192.168.18.248:5555", normalized.AdbSerial);

        var view = DeviceViewMapper.MapList(
            ImmutableArray.Create(raw),
            ImmutableArray<DeviceView>.Empty,
            DateTimeOffset.UtcNow,
            Identity,
            mdns)[0];

        Assert.Equal("R8VXA02SNKH", view.Detail.Serial);
        Assert.Equal("192.168.18.248:5555", view.Detail.Address);
        Assert.Contains(view.Detail.CopyFields, f => f.Key == "serial");
        Assert.Contains(view.Detail.CopyFields, f => f.Key == "address");
    }

    [Fact]
    public void AdbSerial_Remains_Endpoint_For_Metadata_Even_When_Display_Serial_Omitted() {
        var raw = Raw("10.0.0.5:5555", connectionType: 2);
        var n = Identity.Normalize(raw);
        Assert.Equal("10.0.0.5:5555", n.AdbSerial);
        var view = Map(raw);
        Assert.Null(view.Detail.Serial);
        Assert.Equal(n.AdbSerial, view.Detail.Address);
    }

    [Fact]
    public void Modern_Adb_Mdns_Instance_Serial_Shows_Extracted_Hardware_Serial() {
        // adb-(hardwareSerial)-suffix form extracts real hardware serial (ENGINEERING §6.7).
        var raw = Raw("adb-R8VXA02SNKH-abc123", connectionType: 2);
        var n = Identity.Normalize(raw);
        Assert.Equal("R8VXA02SNKH", n.HardwareSerial);
        var view = Map(raw);
        Assert.Equal("R8VXA02SNKH", view.Detail.Serial);
        Assert.Null(view.Detail.Address); // AdbSerial is not host:port
    }

    [Fact]
    public void Usb_And_Wifi_Coexist_As_Distinct_Cards() {
        var usb = Raw("R8VXA02SNKH", connectionType: 1);
        var wifi = Raw("192.168.18.248:5555", connectionType: 2);
        var list = DeviceViewMapper.MapList(
            ImmutableArray.Create(usb, wifi),
            ImmutableArray<DeviceView>.Empty,
            DateTimeOffset.UtcNow,
            Identity);
        Assert.Equal(2, list.Length);
        Assert.NotEqual(list[0].Id, list[1].Id);
        Assert.Equal(TransportKind.Usb, list[0].Transport);
        Assert.Equal(TransportKind.WiFi, list[1].Transport);
        Assert.Equal("R8VXA02SNKH", list[0].Detail.Serial);
        Assert.Null(list[1].Detail.Serial);
        Assert.Equal("192.168.18.248:5555", list[1].Detail.Address);
    }

    private static DeviceView Map(RawDevice raw) =>
        DeviceViewMapper.MapList(
            ImmutableArray.Create(raw),
            ImmutableArray<DeviceView>.Empty,
            DateTimeOffset.UtcNow,
            Identity)[0];

    private static RawDevice Raw(string serial, int connectionType) => new() {
        Serial = serial,
        ProtoState = 8,
        StateToken = null,
        ConnectionType = connectionType,
        Product = null,
        Model = "Phone",
        Device = null,
        Format = RawDeviceFormat.ProtoBinary,
    };
}
