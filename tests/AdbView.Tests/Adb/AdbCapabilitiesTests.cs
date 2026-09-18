#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Protocol;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class AdbCapabilitiesTests {
    private static byte[] CreateServerStatusPayload(
        string version = "37.0.1",
        int mdnsBackend = 1,
        bool mdnsEnabled = true,
        string logPath = "/tmp/adb.log",
        string exePath = "/bin/adb") {
        var ms = new List<byte>();

        // field 3 (mdns_backend, wire 0): tag = (3 << 3) | 0 = 0x18
        ms.Add(0x18);
        ms.Add((byte)mdnsBackend);

        // field 5 (version, wire 2): tag = (5 << 3) | 2 = 0x2A
        byte[] verBytes = Encoding.UTF8.GetBytes(version);
        ms.Add(0x2A);
        ms.Add((byte)verBytes.Length);
        ms.AddRange(verBytes);

        // field 7 (executable_absolute_path, wire 2): tag = (7 << 3) | 2 = 0x3A
        byte[] exeBytes = Encoding.UTF8.GetBytes(exePath);
        ms.Add(0x3A);
        ms.Add((byte)exeBytes.Length);
        ms.AddRange(exeBytes);

        // field 8 (log_absolute_path, wire 2): tag = (8 << 3) | 2 = 0x42
        byte[] logBytes = Encoding.UTF8.GetBytes(logPath);
        ms.Add(0x42);
        ms.Add((byte)logBytes.Length);
        ms.AddRange(logBytes);

        // field 12 (mdns_enabled, wire 0): tag = (12 << 3) | 0 = 0x60
        ms.Add(0x60);
        ms.Add(mdnsEnabled ? (byte)1 : (byte)0);

        return ms.ToArray();
    }

    [Fact]
    public void Parse_ServerStatusValidPayload_ExtractsExpectedProperties() {
        // Test L: server-status valid payload
        byte[] payload = CreateServerStatusPayload(
            version: "37.0.1-12345",
            mdnsBackend: 1, // Bonjour
            mdnsEnabled: true,
            logPath: "C:\\adb.log",
            exePath: "C:\\platform-tools\\adb.exe");

        var status = AdbServerStatus.Parse(payload);

        Assert.Equal("37.0.1-12345", status.Version);
        Assert.Equal(AdbMdnsBackend.Bonjour, status.MdnsBackend);
        Assert.True(status.MdnsEnabled);
        Assert.Equal("C:\\adb.log", status.LogPath);
        Assert.Equal("C:\\platform-tools\\adb.exe", status.ExecutablePath);
    }

    [Fact]
    public void Parse_ServerStatusLibadbmdnsBackend_ExtractsExpectedProperties() {
        byte[] payload = CreateServerStatusPayload(
            version: "37.0.1",
            mdnsBackend: 3, // Libadbmdns (default in platform-tools 37.0.1)
            mdnsEnabled: true);

        var status = AdbServerStatus.Parse(payload);

        Assert.Equal("37.0.1", status.Version);
        Assert.Equal(AdbMdnsBackend.Libadbmdns, status.MdnsBackend);
        Assert.True(status.MdnsEnabled);
    }

    [Fact]
    public void Parse_ServerStatusReorderedFields_ParsesIdentically() {
        // Test M: server-status reordered fields
        var ms = new List<byte>();

        // Put field 12 first (mdns_enabled = true)
        ms.Add(0x60);
        ms.Add(1);

        // Then field 8 (log_path)
        byte[] logBytes = Encoding.UTF8.GetBytes("/var/log/adb.log");
        ms.Add(0x42);
        ms.Add((byte)logBytes.Length);
        ms.AddRange(logBytes);

        // Then field 5 (version)
        byte[] verBytes = Encoding.UTF8.GetBytes("37.0.0");
        ms.Add(0x2A);
        ms.Add((byte)verBytes.Length);
        ms.AddRange(verBytes);

        // Then field 3 (mdns_backend = 2 / Openscreen)
        ms.Add(0x18);
        ms.Add(2);

        var status = AdbServerStatus.Parse(ms.ToArray());

        Assert.Equal("37.0.0", status.Version);
        Assert.Equal(AdbMdnsBackend.Openscreen, status.MdnsBackend);
        Assert.True(status.MdnsEnabled);
        Assert.Equal("/var/log/adb.log", status.LogPath);
    }

    [Fact]
    public void Parse_ServerStatusUnknownFutureFields_SkippedSafely() {
        // Test N: server-status unknown future fields
        var ms = new List<byte>();

        // Known field 5 (version = "38.0.0")
        byte[] verBytes = Encoding.UTF8.GetBytes("38.0.0");
        ms.Add(0x2A);
        ms.Add((byte)verBytes.Length);
        ms.AddRange(verBytes);

        // Unknown future field 99 (wire 2 length-delimited): tag = (99 << 3) | 2 = 794 = 0x9A, 0x06
        byte[] futureBytes = "future"u8.ToArray();
        ms.Add(0x9A);
        ms.Add(0x06);
        ms.Add((byte)futureBytes.Length);
        ms.AddRange(futureBytes);

        // Unknown future field 100 (wire 0 varint): tag = (100 << 3) | 0 = 800 = 0xA0, 0x06
        ms.Add(0xA0);
        ms.Add(0x06);
        ms.Add(42);

        // Known field 12 (mdns_enabled = false)
        ms.Add(0x60);
        ms.Add(0);

        var status = AdbServerStatus.Parse(ms.ToArray());

        Assert.Equal("38.0.0", status.Version);
        Assert.False(status.MdnsEnabled);
    }

    [Fact]
    public void Parse_ServerStatusMalformedKnownField_ThrowsMalformedFrame() {
        // Test O: server-status malformed known field (e.g. version field 5 encoded with wire type 0 instead of 2)
        byte tag = (5 << 3) | 0; // field 5, wire 0 (invalid: string cannot be varint)
        byte[] invalidPayload = new byte[] { tag, 0x01 };

        var ex = Assert.Throws<AdbProtocolException>(() => AdbServerStatus.Parse(invalidPayload));
        Assert.Equal(AdbErrorCode.MalformedLength, ex.ErrorCode);
    }

    [Theory]
    [InlineData("0029", 41)]
    [InlineData("0001", 1)]
    [InlineData("FFFF", 65535)]
    [InlineData("ffff", 65535)]
    public void ParseVersion_ValidHexResult_DecodesIntegerProtocolVersion(string hex, int expectedVersion) {
        // Test P: host:version valid 4-character ASCII hex results
        byte[] payload = Encoding.ASCII.GetBytes(hex);
        int version = AdbCapabilities.ParseVersion(payload);
        Assert.Equal(expectedVersion, version);
    }

    [Theory]
    [InlineData("29")]
    [InlineData("000029")]
    [InlineData("00000029")]
    [InlineData("0029\n")]
    [InlineData("0029\r\n")]
    [InlineData("XXXX")]
    [InlineData("")]
    [InlineData("0000")]
    [InlineData("002G")]
    [InlineData("ZZZZ")]
    public void ParseVersion_MalformedHexResult_ThrowsAdbProtocolException(string invalid) {
        // Test Q: host:version malformed result produces typed protocol exception without defaulting to 0
        byte[] payload = Encoding.ASCII.GetBytes(invalid);
        var ex = Assert.Throws<AdbProtocolException>(() => AdbCapabilities.ParseVersion(payload));
        Assert.Equal(AdbErrorCode.MalformedLength, ex.ErrorCode);
    }

    [Fact]
    public async Task GetServerStatusAsync_ThroughFakeAdbServer_ReturnsValidStatus() {
        // Test R: host:server-status valid result through FakeAdbServer
        await using var server = new FakeAdbServer();
        byte[] protoPayload = CreateServerStatusPayload(
            version: "37.0.1",
            mdnsBackend: 1,
            mdnsEnabled: true,
            logPath: "C:\\adb.log",
            exePath: "C:\\adb.exe");

        server.SetHandler(async (socket, cmd) => {
            Assert.Equal("host:server-status", cmd);
            byte[] header = Encoding.ASCII.GetBytes($"OKAY{protoPayload.Length:X4}");
            await socket.SendAsync(header, SocketFlags.None);
            await socket.SendAsync(protoPayload, SocketFlags.None);
        });

        var capabilities = new AdbCapabilities(server.EndPoint);
        var status = await capabilities.GetServerStatusAsync();

        Assert.Equal("37.0.1", status.Version);
        Assert.Equal(AdbMdnsBackend.Bonjour, status.MdnsBackend);
        Assert.True(status.MdnsEnabled);
        Assert.Equal("C:\\adb.log", status.LogPath);
        Assert.Equal("C:\\adb.exe", status.ExecutablePath);

        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task GetVersionAsync_ThroughFakeAdbServer_ReturnsValidProtocolVersion() {
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            Assert.Equal("host:version", cmd);
            await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
        });

        var capabilities = new AdbCapabilities(server.EndPoint);
        int version = await capabilities.GetVersionAsync();

        Assert.Equal(41, version);
        server.AssertNoKillCommandSent();
    }
}
