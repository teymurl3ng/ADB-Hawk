#pragma warning disable CA1707
namespace AdbView.Tests.Adb.Protocol;

using System;
using System.Text;
using AdbView.Adb.Protocol;
using Xunit;

public sealed class AdbFramingTests {
    [Fact]
    public void EncodeRequest_ProducesExactHexAndPayloadBytes() {
        // Requirement A: exact host:version request bytes
        byte[] encoded = AdbFraming.EncodeRequest("host:version");
        string text = Encoding.ASCII.GetString(encoded);

        Assert.Equal("000Chost:version", text);
        Assert.Equal(16, encoded.Length);
        Assert.Equal((byte)'0', encoded[0]);
        Assert.Equal((byte)'0', encoded[1]);
        Assert.Equal((byte)'0', encoded[2]);
        Assert.Equal((byte)'C', encoded[3]);
    }

    [Fact]
    public void EncodeRequest_RejectsRequestExceedingMaxRequestLength() {
        string hugeService = new string('A', 0x10000); // 65,536 bytes > 0xFFFF
        var ex = Assert.Throws<AdbProtocolException>(() => AdbFraming.EncodeRequest(hugeService));
        Assert.Equal(AdbErrorCode.InvalidRequest, ex.ErrorCode);
    }

    [Theory]
    [InlineData("0000", 0)]
    [InlineData("000c", 12)]
    [InlineData("000C", 12)]
    [InlineData("FFFF", 65535)]
    [InlineData("0001", 1)]
    [InlineData("ffff", 65535)]
    [InlineData("0029", 41)]
    public void ParseHexLength_ParsesValidHexLengths(string hex, int expected) {
        // Requirement B: valid hex decoding
        byte[] bytes = Encoding.ASCII.GetBytes(hex);
        int actual = AdbFraming.ParseHexLength(bytes);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("000G")]
    [InlineData("ZZZZ")]
    [InlineData("-001")]
    [InlineData("    ")]
    [InlineData("00 1")]
    [InlineData("0x12")]
    [InlineData("+-01")]
    public void ParseHexLength_RejectsInvalidHex(string invalidHex) {
        // Requirement C: invalid hex rejected
        byte[] bytes = Encoding.ASCII.GetBytes(invalidHex);
        var ex = Assert.Throws<AdbProtocolException>(() => AdbFraming.ParseHexLength(bytes));
        Assert.Equal(AdbErrorCode.MalformedLength, ex.ErrorCode);
    }

    [Fact]
    public void ParseHexLength_RejectsLengthOutsideFourBytes() {
        byte[] shortBytes = Encoding.ASCII.GetBytes("00C");
        byte[] longBytes = Encoding.ASCII.GetBytes("0000C");

        Assert.Throws<AdbProtocolException>(() => AdbFraming.ParseHexLength(shortBytes));
        Assert.Throws<AdbProtocolException>(() => AdbFraming.ParseHexLength(longBytes));
    }

    [Fact]
    public void ParseStatus_RecognizesExactOkay() {
        // Requirement D: exact OKAY recognition
        byte[] okay = "OKAY"u8.ToArray();
        var status = AdbFraming.ParseStatus(okay);
        Assert.Equal(AdbStatus.Okay, status);
    }

    [Fact]
    public void ParseStatus_RecognizesExactFail() {
        // Requirement E: exact FAIL recognition
        byte[] fail = "FAIL"u8.ToArray();
        var status = AdbFraming.ParseStatus(fail);
        Assert.Equal(AdbStatus.Fail, status);
    }

    [Theory]
    [InlineData("okay")]
    [InlineData("HTTP")]
    [InlineData("0000")]
    [InlineData("fail")]
    [InlineData("OKAYx")]
    [InlineData("OKA")]
    [InlineData("ROK!")]
    public void ParseStatus_RejectsMalformedStatus(string malformed) {
        // Requirement F: malformed status rejected
        byte[] bytes = Encoding.ASCII.GetBytes(malformed);
        var ex = Assert.Throws<AdbProtocolException>(() => AdbFraming.ParseStatus(bytes));
        Assert.Equal(AdbErrorCode.InvalidStatus, ex.ErrorCode);
    }

    [Fact]
    public void ParseHexLength_AcceptsAtMostFFFF() {
        // Four hex characters can represent at most 0xFFFF (65,535 bytes)
        int upper = AdbFraming.ParseHexLength("FFFF"u8);
        int lower = AdbFraming.ParseHexLength("ffff"u8);
        Assert.Equal(65535, upper);
        Assert.Equal(65535, lower);
    }

    [Fact]
    public void ParseHexLength_ExceedingCustomMaxAllowed_ThrowsPayloadTooLarge() {
        // Wire hex prefix 00C8 is 200 bytes; when maxAllowed is 100, throws PayloadTooLarge
        byte[] bytes = "00C8"u8.ToArray();
        var ex = Assert.Throws<AdbProtocolException>(() => AdbFraming.ParseHexLength(bytes, maxAllowed: 100));
        Assert.Equal(AdbErrorCode.PayloadTooLarge, ex.ErrorCode);
    }

    [Fact]
    public void ValidateFrameLength_RejectsFrameExceedingMaxFrameLength() {
        // Generic safety cap: >1 MiB frame rejected before allocation/read
        const int oneMibPlusOne = AdbFraming.MaxFrameLength + 1; // 1,048,577 bytes
        var ex = Assert.Throws<AdbProtocolException>(() => AdbFraming.ValidateFrameLength(oneMibPlusOne));
        Assert.Equal(AdbErrorCode.PayloadTooLarge, ex.ErrorCode);
    }

    [Fact]
    public void ValidateFrameLength_ExceedingCustomMaxAllowed_ThrowsPayloadTooLarge() {
        var ex = Assert.Throws<AdbProtocolException>(() => AdbFraming.ValidateFrameLength(200, maxAllowed: 100));
        Assert.Equal(AdbErrorCode.PayloadTooLarge, ex.ErrorCode);
    }

    [Fact]
    public void ValidateFrameLength_RejectsNegativeLength() {
        var ex = Assert.Throws<AdbProtocolException>(() => AdbFraming.ValidateFrameLength(-1));
        Assert.Equal(AdbErrorCode.MalformedLength, ex.ErrorCode);
    }

    [Fact]
    public void ValidateFrameLength_AcceptsExactMaxFrameLength() {
        // 1 MiB boundary is valid
        AdbFraming.ValidateFrameLength(AdbFraming.MaxFrameLength);
        AdbFraming.ValidateFrameLength(0);
        AdbFraming.ValidateFrameLength(65535);
    }
}
