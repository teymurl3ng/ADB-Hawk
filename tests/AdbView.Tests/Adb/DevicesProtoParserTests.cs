#pragma warning disable CA1707
namespace AdbView.Tests.Adb;

using System;
using System.IO;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class DevicesProtoParserTests {
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Adb");

    // Ensure fixtures are copied: use relative from test project source via find
    private static byte[] LoadUsbFixture() {
        string[] candidates =
        [
            Path.Combine(FixtureDir, "track-devices-proto-binary-usb-a06.bin"),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..",
                "Fixtures", "Adb", "track-devices-proto-binary-usb-a06.bin")),
        ];
        foreach (var path in candidates) {
            if (File.Exists(path)) {
                return File.ReadAllBytes(path);
            }
        }
        throw new FileNotFoundException("USB proto-binary fixture not found.");
    }

    [Fact]
    public void A_EmptyPayload_ReturnsEmptyList() {
        Assert.Empty(DevicesProtoParser.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void B_OneDevice_ParsesRequiredFields() {
        byte[] device = FakeAdbTrackDevices.EncodeDevice(
            serial: "SERIAL1",
            state: 8,
            product: "prod",
            model: "Model_X",
            device: "dev",
            connectionType: 1);
        byte[] payload = FakeAdbTrackDevices.EncodeDevices(device);

        var list = DevicesProtoParser.Parse(payload);

        Assert.Single(list);
        Assert.Equal("SERIAL1", list[0].Serial);
        Assert.Equal(8, list[0].ProtoState);
        Assert.Null(list[0].StateToken);
        Assert.Equal(1, list[0].ConnectionType);
        Assert.Equal("prod", list[0].Product);
        Assert.Equal("Model_X", list[0].Model);
        Assert.Equal("dev", list[0].Device);
        Assert.Equal(RawDeviceFormat.ProtoBinary, list[0].Format);
    }

    [Fact]
    public void C_MultipleDevices_PreserveSourceOrder() {
        byte[] d1 = FakeAdbTrackDevices.EncodeDevice("A", state: 8, connectionType: 1);
        byte[] d2 = FakeAdbTrackDevices.EncodeDevice("B", state: 3, connectionType: 2);
        byte[] d3 = FakeAdbTrackDevices.EncodeDevice("C", state: 6, connectionType: 0);
        var list = DevicesProtoParser.Parse(FakeAdbTrackDevices.EncodeDevices(d1, d2, d3));

        Assert.Equal(3, list.Length);
        Assert.Equal("A", list[0].Serial);
        Assert.Equal("B", list[1].Serial);
        Assert.Equal("C", list[2].Serial);
    }

    [Fact]
    public void D_UnknownFields_SkippedSafely() {
        byte[] device = FakeAdbTrackDevices.EncodeDevice(
            serial: "S",
            state: 8,
            connectionType: 1,
            extraFields: [(99, "future"u8.ToArray())]);
        // also unknown Devices-level field
        var ms = new System.Collections.Generic.List<byte>();
        ms.AddRange(FakeAdbTrackDevices.EncodeDevices(device));
        // field 50 wire 0 varint 7
        ms.Add(0x90); // (50 << 3) = 400 = 0x190 → multi-byte: tag = 50<<3|0 = 400
        ms.Add(0x03);
        ms.Add(7);

        var list = DevicesProtoParser.Parse(ms.ToArray());
        Assert.Single(list);
        Assert.Equal("S", list[0].Serial);
    }

    [Fact]
    public void E_ReorderedFields_Parse() {
        // state before serial, connection_type before model
        var ms = new System.Collections.Generic.List<byte>();
        // state=8 tag 0x10
        ms.Add(0x10); ms.Add(8);
        // serial
        byte[] serial = "R1"u8.ToArray();
        ms.Add(0x0A); ms.Add((byte)serial.Length); ms.AddRange(serial);
        // connection_type USB=1
        ms.Add(0x38); ms.Add(1);
        // model
        byte[] model = "M"u8.ToArray();
        ms.Add(0x2A); ms.Add((byte)model.Length); ms.AddRange(model);

        var list = DevicesProtoParser.Parse(FakeAdbTrackDevices.EncodeDevices(ms.ToArray()));
        Assert.Equal("R1", list[0].Serial);
        Assert.Equal(8, list[0].ProtoState);
        Assert.Equal(1, list[0].ConnectionType);
        Assert.Equal("M", list[0].Model);
    }

    [Fact]
    public void F_UnknownStateEnum_Preserved() {
        byte[] device = FakeAdbTrackDevices.EncodeDevice("S", state: 99, connectionType: 1);
        var list = DevicesProtoParser.Parse(FakeAdbTrackDevices.EncodeDevices(device));
        Assert.Equal(99, list[0].ProtoState);
    }

    [Fact]
    public void G_UnknownConnectionType_Preserved() {
        byte[] device = FakeAdbTrackDevices.EncodeDevice("S", state: 8, connectionType: 42);
        var list = DevicesProtoParser.Parse(FakeAdbTrackDevices.EncodeDevices(device));
        Assert.Equal(42, list[0].ConnectionType);
    }

    [Fact]
    public void H_TruncatedNestedDevice_Rejected() {
        // Devices.device claims length 10 but only 2 bytes follow
        byte[] truncated = [0x0A, 0x0A, 0x0A, 0x08];
        var ex = Assert.Throws<AdbProtocolException>(() => DevicesProtoParser.Parse(truncated));
        Assert.Equal(AdbErrorCode.UnexpectedEof, ex.ErrorCode);
    }

    [Fact]
    public void I_MalformedVarint_Rejected() {
        // Devices.device tag ok, then nested starts with invalid 10-byte varint
        byte[] bad =
        [
            0x0A, 0x0B, // device length 11
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        ];
        Assert.Throws<AdbProtocolException>(() => DevicesProtoParser.Parse(bad));
    }

    [Fact]
    public void LiveUsbFixture_MatchesObservedFields() {
        byte[] payload = LoadUsbFixture();
        var list = DevicesProtoParser.Parse(payload);

        Assert.Single(list);
        Assert.Equal("R8VXA02SNKH", list[0].Serial);
        Assert.Equal(8, list[0].ProtoState); // DEVICE
        Assert.Equal(1, list[0].ConnectionType); // USB
        Assert.Equal("a06xx", list[0].Product);
        Assert.Equal("SM_A065F", list[0].Model);
        Assert.Equal("a06", list[0].Device);
    }
}
