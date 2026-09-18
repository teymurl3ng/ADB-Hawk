#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb.Protocol;

using System;
using System.Text;
using AdbView.Adb.Protocol;
using Xunit;

public sealed class AdbProtoReaderTests {
    [Theory]
    [InlineData(new byte[] { 0x00 }, 0UL)]
    [InlineData(new byte[] { 0x01 }, 1UL)]
    [InlineData(new byte[] { 0x7F }, 127UL)]
    public void ReadVarint_SingleByte_DecodesExpectedValues(byte[] bytes, ulong expected) {
        // Test A: single-byte varints
        var reader = new AdbProtoReader(bytes);
        ulong actual = reader.ReadVarint();
        Assert.Equal(expected, actual);
        Assert.False(reader.HasMore);
    }

    [Theory]
    [InlineData(new byte[] { 0xAC, 0x02 }, 300UL)]
    [InlineData(new byte[] { 0x96, 0x01 }, 150UL)]
    [InlineData(new byte[] { 0x80, 0x80, 0x01 }, 16384UL)]
    public void ReadVarint_MultiByte_DecodesExpectedValues(byte[] bytes, ulong expected) {
        // Test B: multi-byte varints
        var reader = new AdbProtoReader(bytes);
        ulong actual = reader.ReadVarint();
        Assert.Equal(expected, actual);
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void ReadVarint_MaxValid64BitVarint_DecodesUlongMaxValue() {
        // Test C: max valid 64-bit varint (9 bytes of 0xFF followed by 0x01)
        byte[] maxBytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
        var reader = new AdbProtoReader(maxBytes);
        ulong actual = reader.ReadVarint();
        Assert.Equal(ulong.MaxValue, actual);
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void ReadVarint_MalformedOver10Bytes_ThrowsMalformedFrame() {
        // Test D1: 10 bytes of 0xFF (10th byte has MSB=1)
        byte[] elevenBytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
        var ex = Assert.Throws<AdbProtocolException>(() => new AdbProtoReader(elevenBytes).ReadVarint());
        Assert.Equal(AdbErrorCode.MalformedLength, ex.ErrorCode);
    }

    [Fact]
    public void ReadVarint_MalformedTenthByteOverflow_ThrowsMalformedFrame() {
        // Test D2: 10th byte has bits 1..7 set (e.g. 0x02 overflows 64-bit int)
        byte[] overflowBytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02 };
        var ex = Assert.Throws<AdbProtocolException>(() => new AdbProtoReader(overflowBytes).ReadVarint());
        Assert.Equal(AdbErrorCode.MalformedLength, ex.ErrorCode);
    }

    [Theory]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0xAC, 0x82 })]
    [InlineData(new byte[0])]
    public void ReadVarint_TruncatedVarint_ThrowsUnexpectedEof(byte[] truncated) {
        // Test E: truncated varint
        var ex = Assert.Throws<AdbProtocolException>(() => new AdbProtoReader(truncated).ReadVarint());
        Assert.Equal(AdbErrorCode.UnexpectedEof, ex.ErrorCode);
    }

    [Fact]
    public void ReadFixed32_ValidAndTruncated_BehavesCorrectly() {
        // Test F: fixed32
        byte[] valid = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var reader = new AdbProtoReader(valid);
        uint actual = reader.ReadFixed32();
        Assert.Equal(0x04030201U, actual);
        Assert.False(reader.HasMore);

        byte[] truncated = new byte[] { 0x01, 0x02, 0x03 };
        var ex = Assert.Throws<AdbProtocolException>(() => new AdbProtoReader(truncated).ReadFixed32());
        Assert.Equal(AdbErrorCode.UnexpectedEof, ex.ErrorCode);
    }

    [Fact]
    public void ReadFixed64_ValidAndTruncated_BehavesCorrectly() {
        // Test G: fixed64
        byte[] valid = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var reader = new AdbProtoReader(valid);
        ulong actual = reader.ReadFixed64();
        Assert.Equal(0x0807060504030201UL, actual);
        Assert.False(reader.HasMore);

        byte[] truncated = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var ex = Assert.Throws<AdbProtocolException>(() => new AdbProtoReader(truncated).ReadFixed64());
        Assert.Equal(AdbErrorCode.UnexpectedEof, ex.ErrorCode);
    }

    [Fact]
    public void ReadLengthDelimited_Valid_ExtractsBytesAndString() {
        // Test H: length-delimited field
        byte[] payload = "testing"u8.ToArray();
        byte[] encoded = new byte[1 + payload.Length];
        encoded[0] = (byte)payload.Length; // 7 bytes
        payload.CopyTo(encoded.AsSpan(1));

        var reader = new AdbProtoReader(encoded);
        string text = reader.ReadString();
        Assert.Equal("testing", text);
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void ReadLengthDelimited_Truncated_ThrowsUnexpectedEof() {
        // Test I: truncated length-delimited field
        byte[] truncated = new byte[] { 0x0A, 0x01, 0x02, 0x03 }; // length 10 advertised, only 3 bytes present
        var ex = Assert.Throws<AdbProtocolException>(() => new AdbProtoReader(truncated).ReadLengthDelimited());
        Assert.Equal(AdbErrorCode.UnexpectedEof, ex.ErrorCode);
    }

    [Fact]
    public void SkipField_UnknownSupportedWireTypes_SkippedSafely() {
        // Test J: unknown supported wire-type fields skipped safely
        // Construct a buffer with:
        // field 99 (wire 0 varint): tag = (99 << 3) | 0 = 792 = 0x98, 0x06; value = 300 (0xAC, 0x02)
        // field 98 (wire 1 fixed64): tag = (98 << 3) | 1 = 785 = 0x91, 0x06; 8 bytes
        // field 97 (wire 2 length-delimited): tag = (97 << 3) | 2 = 778 = 0x8A, 0x06; len = 4; "skip"
        // field 96 (wire 5 fixed32): tag = (96 << 3) | 5 = 773 = 0x85, 0x06; 4 bytes
        // field 1 (wire 0 varint, known target): tag = (1 << 3) | 0 = 8; value = 42
        byte[] buffer = new byte[] {
            0x98, 0x06, 0xAC, 0x02,                                     // field 99, wire 0
            0x91, 0x06, 1, 2, 3, 4, 5, 6, 7, 8,                         // field 98, wire 1
            0x8A, 0x06, 4, (byte)'s', (byte)'k', (byte)'i', (byte)'p',   // field 97, wire 2
            0x85, 0x06, 10, 20, 30, 40,                                 // field 96, wire 5
            0x08, 42                                                    // field 1, wire 0
        };

        var reader = new AdbProtoReader(buffer);

        int fieldsSeen = 0;
        int targetValue = 0;

        while (reader.TryReadTag(out int fieldNum, out int wireType)) {
            if (fieldNum == 1) {
                targetValue = reader.ReadInt32();
            }
            else {
                reader.SkipField(wireType);
            }
            fieldsSeen++;
        }

        Assert.Equal(5, fieldsSeen);
        Assert.Equal(42, targetValue);
        Assert.False(reader.HasMore);
    }

    [Theory]
    [InlineData(3)] // Start group
    [InlineData(4)] // End group
    [InlineData(6)] // Reserved
    [InlineData(7)] // Reserved
    public void TryReadTag_UnsupportedWireType_ThrowsMalformedFrame(int unsupportedWireType) {
        // Test K: unsupported wire types rejected
        byte tag = (byte)((1 << 3) | unsupportedWireType);
        var ex = Assert.Throws<AdbProtocolException>(() => new AdbProtoReader(new[] { tag }).TryReadTag(out _, out _));
        Assert.Equal(AdbErrorCode.MalformedLength, ex.ErrorCode);
    }
}
