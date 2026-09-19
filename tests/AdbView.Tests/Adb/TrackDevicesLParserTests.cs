#pragma warning disable CA1707
namespace AdbView.Tests.Adb;

using System;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using Xunit;

public sealed class TrackDevicesLParserTests {
    [Fact]
    public void V_ZeroDevices_EmptyPayload() {
        Assert.Empty(TrackDevicesLParser.Parse(""));
        Assert.Empty(TrackDevicesLParser.Parse("   \n  "));
    }

    [Fact]
    public void V_OneDevice_ParsesFields() {
        string text = "R8VXA02SNKH            device product:a06xx model:SM_A065F device:a06 transport_id:2\n";
        var list = TrackDevicesLParser.Parse(text);

        Assert.Single(list);
        Assert.Equal("R8VXA02SNKH", list[0].Serial);
        Assert.Equal("device", list[0].StateToken);
        Assert.Null(list[0].ProtoState);
        Assert.Null(list[0].ConnectionType);
        Assert.Equal("a06xx", list[0].Product);
        Assert.Equal("SM_A065F", list[0].Model);
        Assert.Equal("a06", list[0].Device);
        Assert.Equal(RawDeviceFormat.TrackDevicesL, list[0].Format);
    }

    [Fact]
    public void V_MultipleDevices_PreserveOrder() {
        string text =
            "AAA\tdevice product:p1 model:m1 device:d1\n" +
            "192.168.1.5:5555\tunauthorized\n" +
            "emulator-5554\toffline product:p3\n";
        var list = TrackDevicesLParser.Parse(text);

        Assert.Equal(3, list.Length);
        Assert.Equal("AAA", list[0].Serial);
        Assert.Equal("device", list[0].StateToken);
        Assert.Equal("192.168.1.5:5555", list[1].Serial);
        Assert.Equal("unauthorized", list[1].StateToken);
        Assert.Equal("emulator-5554", list[2].Serial);
        Assert.Equal("offline", list[2].StateToken);
        Assert.Equal("p3", list[2].Product);
    }

    [Fact]
    public void W_UnknownStateToken_Preserved() {
        var list = TrackDevicesLParser.Parse("S1\tfuture_state_xyz\n");
        Assert.Equal("future_state_xyz", list[0].StateToken);
    }

    [Fact]
    public void X_DoesNotInventTransport() {
        var list = TrackDevicesLParser.Parse("192.168.0.2:37000\tdevice product:p model:m device:d\n");
        Assert.Null(list[0].ConnectionType);

        var emu = TrackDevicesLParser.Parse("emulator-5554\tdevice\n");
        Assert.Null(emu[0].ConnectionType);
    }

    [Fact]
    public void MalformedLine_MissingState_Throws() {
        Assert.Throws<AdbProtocolException>(() => TrackDevicesLParser.Parse("ONLYSERIAL\n"));
    }
}
