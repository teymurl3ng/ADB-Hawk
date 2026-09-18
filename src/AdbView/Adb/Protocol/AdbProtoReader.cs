namespace AdbView.Adb.Protocol;

using System;
using System.Buffers.Binary;
using System.Text;

internal ref struct AdbProtoReader {
    private ReadOnlySpan<byte> _span;

    public bool HasMore => !_span.IsEmpty;
    public int RemainingLength => _span.Length;

    public AdbProtoReader(ReadOnlySpan<byte> span) {
        _span = span;
    }

    public bool TryReadTag(out int fieldNumber, out int wireType) {
        if (_span.IsEmpty) {
            fieldNumber = 0;
            wireType = 0;
            return false;
        }

        ulong tag = ReadVarint();
        if (tag == 0) {
            throw new AdbProtocolException(AdbErrorCode.MalformedLength, "Protobuf tag 0 is invalid (field number must be > 0).");
        }

        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);

        if (fieldNumber <= 0) {
            throw new AdbProtocolException(AdbErrorCode.MalformedLength, $"Protobuf field number {fieldNumber} is invalid.");
        }

        if (wireType is not (0 or 1 or 2 or 5)) {
            throw new AdbProtocolException(AdbErrorCode.MalformedLength, $"Unsupported protobuf wire type {wireType} for field {fieldNumber}.");
        }

        return true;
    }

    public ulong ReadVarint() {
        ulong value = 0;
        int shift = 0;
        for (int i = 0; i < 10; i++) {
            if (_span.IsEmpty) {
                throw new AdbProtocolException(AdbErrorCode.UnexpectedEof, "Truncated protobuf varint.");
            }

            byte b = _span[0];
            _span = _span.Slice(1);

            if (i == 9) {
                // 10th byte: only bit 0 is valid for 64-bit unsigned integers.
                // If MSB is set, or bits 1..7 are set, it exceeds 64 bits.
                if ((b & 0x80) != 0 || (b & 0xFE) != 0) {
                    throw new AdbProtocolException(AdbErrorCode.MalformedLength, "Protobuf varint exceeds 64 bits.");
                }
                value |= ((ulong)b) << shift;
                return value;
            }

            value |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0) {
                return value;
            }
            shift += 7;
        }

        throw new AdbProtocolException(AdbErrorCode.MalformedLength, "Protobuf varint exceeds 10 bytes.");
    }

    public long ReadInt64() => (long)ReadVarint();
    public int ReadInt32() => (int)ReadVarint();
    public bool ReadBool() => ReadVarint() != 0;

    public uint ReadFixed32() {
        if (_span.Length < 4) {
            throw new AdbProtocolException(AdbErrorCode.UnexpectedEof, "Truncated protobuf fixed32 field.");
        }
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_span);
        _span = _span.Slice(4);
        return value;
    }

    public ulong ReadFixed64() {
        if (_span.Length < 8) {
            throw new AdbProtocolException(AdbErrorCode.UnexpectedEof, "Truncated protobuf fixed64 field.");
        }
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_span);
        _span = _span.Slice(8);
        return value;
    }

    public ReadOnlySpan<byte> ReadLengthDelimited() {
        ulong len = ReadVarint();
        if (len > (ulong)_span.Length) {
            throw new AdbProtocolException(
                AdbErrorCode.UnexpectedEof,
                $"Truncated protobuf length-delimited payload: expected {len} bytes, remaining {_span.Length} bytes.");
        }
        int byteCount = (int)len;
        ReadOnlySpan<byte> slice = _span.Slice(0, byteCount);
        _span = _span.Slice(byteCount);
        return slice;
    }

    public string ReadString() {
        ReadOnlySpan<byte> bytes = ReadLengthDelimited();
        return Encoding.UTF8.GetString(bytes);
    }

    public void SkipField(int wireType) {
        switch (wireType) {
            case 0:
                _ = ReadVarint();
                break;
            case 1:
                if (_span.Length < 8) {
                    throw new AdbProtocolException(AdbErrorCode.UnexpectedEof, "Truncated protobuf fixed64 field during skip.");
                }
                _span = _span.Slice(8);
                break;
            case 2:
                _ = ReadLengthDelimited();
                break;
            case 5:
                if (_span.Length < 4) {
                    throw new AdbProtocolException(AdbErrorCode.UnexpectedEof, "Truncated protobuf fixed32 field during skip.");
                }
                _span = _span.Slice(4);
                break;
            default:
                throw new AdbProtocolException(AdbErrorCode.MalformedLength, $"Cannot skip unsupported protobuf wire type {wireType}.");
        }
    }
}
