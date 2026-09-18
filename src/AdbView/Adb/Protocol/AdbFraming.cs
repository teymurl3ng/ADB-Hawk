namespace AdbView.Adb.Protocol;

using System;
using System.Globalization;
using System.Text;

internal enum AdbStatus {
    Okay,
    Fail
}

internal static class AdbFraming {
    public const int MaxFrameLength = 1_048_576; // 1 MiB (ENGINEERING.md §6.1)
    public const int MaxRequestLength = 0xFFFF;   // 65,535 bytes (four-hex framing)
    public const int HeaderLength = 4;

    private static ReadOnlySpan<byte> OkayBytes => "OKAY"u8;
    private static ReadOnlySpan<byte> FailBytes => "FAIL"u8;

    public static byte[] EncodeRequest(string service) {
        ArgumentNullException.ThrowIfNull(service);

        int byteCount = Encoding.ASCII.GetByteCount(service);
        if (byteCount > MaxRequestLength) {
            throw new AdbProtocolException(
                AdbErrorCode.InvalidRequest,
                $"Service request length {byteCount} bytes exceeds maximum allowed {MaxRequestLength} (0x{MaxRequestLength:X4}) bytes.");
        }

        byte[] buffer = new byte[HeaderLength + byteCount];
        string hex = byteCount.ToString("X4", CultureInfo.InvariantCulture);
        Encoding.ASCII.GetBytes(hex, buffer.AsSpan(0, HeaderLength));
        Encoding.ASCII.GetBytes(service, buffer.AsSpan(HeaderLength, byteCount));
        return buffer;
    }

    public static AdbStatus ParseStatus(ReadOnlySpan<byte> header) {
        if (header.Length != HeaderLength) {
            throw new AdbProtocolException(
                AdbErrorCode.InvalidStatus,
                $"Expected status header of {HeaderLength} bytes, received {header.Length} bytes.");
        }

        if (header.SequenceEqual(OkayBytes)) {
            return AdbStatus.Okay;
        }

        if (header.SequenceEqual(FailBytes)) {
            return AdbStatus.Fail;
        }

        string rawText = Encoding.ASCII.GetString(header);
        string hex = Convert.ToHexString(header);
        throw new AdbProtocolException(
            AdbErrorCode.InvalidStatus,
            $"Invalid ADB status header: '{rawText}' (0x{hex}).");
    }

    public static int ParseHexLength(ReadOnlySpan<byte> span, int maxAllowed = MaxFrameLength) {
        if (span.Length != HeaderLength) {
            throw new AdbProtocolException(
                AdbErrorCode.MalformedLength,
                $"Expected {HeaderLength} hex length bytes, received {span.Length} bytes.");
        }

        int length = 0;
        for (int i = 0; i < HeaderLength; i++) {
            byte b = span[i];
            int digit = b switch {
                >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                _ => -1
            };

            if (digit < 0) {
                string rawText = Encoding.ASCII.GetString(span);
                throw new AdbProtocolException(
                    AdbErrorCode.MalformedLength,
                    $"Malformed hex length prefix '{rawText}' contains invalid character '{(char)b}'.");
            }

            length = (length << 4) | digit;
        }

        ValidateFrameLength(length, maxAllowed);
        return length;
    }

    public static void ValidateFrameLength(int length, int maxAllowed = MaxFrameLength) {
        if (length < 0) {
            throw new AdbProtocolException(
                AdbErrorCode.MalformedLength,
                $"Negative frame length {length} is invalid.");
        }

        if (length > maxAllowed) {
            throw new AdbProtocolException(
                AdbErrorCode.PayloadTooLarge,
                $"Frame length {length} bytes exceeds maximum allowed {maxAllowed} bytes.");
        }
    }
}
