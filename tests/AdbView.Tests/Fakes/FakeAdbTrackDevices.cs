namespace AdbView.Tests.Fakes;

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class FakeAdbTrackDevices {
    public static byte[] EncodeFrame(ReadOnlySpan<byte> payload) {
        string hex = payload.Length.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        byte[] header = Encoding.ASCII.GetBytes(hex);
        byte[] frame = new byte[4 + payload.Length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    public static byte[] EncodeOkayThenFrames(params byte[][] payloads) {
        var parts = new List<byte>();
        parts.AddRange(Encoding.ASCII.GetBytes("OKAY"));
        foreach (var payload in payloads) {
            parts.AddRange(EncodeFrame(payload));
        }
        return parts.ToArray();
    }

    public static async Task SendOkayAsync(Socket socket, CancellationToken ct = default) {
        await socket.SendAsync(Encoding.ASCII.GetBytes("OKAY"), SocketFlags.None, ct).ConfigureAwait(false);
    }

    public static async Task SendFrameAsync(Socket socket, ReadOnlyMemory<byte> payload, CancellationToken ct = default) {
        await socket.SendAsync(EncodeFrame(payload.Span), SocketFlags.None, ct).ConfigureAwait(false);
    }

    public static async Task SendFailAsync(Socket socket, string reason, CancellationToken ct = default) {
        byte[] reasonBytes = Encoding.ASCII.GetBytes(reason);
        byte[] header = Encoding.ASCII.GetBytes($"FAIL{reasonBytes.Length:X4}");
        await socket.SendAsync(header, SocketFlags.None, ct).ConfigureAwait(false);
        await socket.SendAsync(reasonBytes, SocketFlags.None, ct).ConfigureAwait(false);
    }

    public static byte[] EncodeDevice(
        string serial,
        int state,
        string? product = null,
        string? model = null,
        string? device = null,
        int? connectionType = null,
        IEnumerable<(int Field, byte[] Value)>? extraFields = null) {
        var ms = new List<byte>();
        WriteString(ms, fieldNumber: 1, serial);
        WriteVarintField(ms, fieldNumber: 2, state);
        if (product != null) {
            WriteString(ms, fieldNumber: 4, product);
        }
        if (model != null) {
            WriteString(ms, fieldNumber: 5, model);
        }
        if (device != null) {
            WriteString(ms, fieldNumber: 6, device);
        }
        if (connectionType is int ct) {
            WriteVarintField(ms, fieldNumber: 7, ct);
        }
        if (extraFields != null) {
            foreach (var (field, value) in extraFields) {
                // length-delimited extras
                WriteTag(ms, field, wireType: 2);
                WriteVarint(ms, (ulong)value.Length);
                ms.AddRange(value);
            }
        }
        return ms.ToArray();
    }

    public static byte[] EncodeDevices(params byte[][] devices) {
        var ms = new List<byte>();
        foreach (var device in devices) {
            WriteTag(ms, fieldNumber: 1, wireType: 2);
            WriteVarint(ms, (ulong)device.Length);
            ms.AddRange(device);
        }
        return ms.ToArray();
    }

    private static void WriteString(List<byte> ms, int fieldNumber, string value) {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(ms, fieldNumber, wireType: 2);
        WriteVarint(ms, (ulong)bytes.Length);
        ms.AddRange(bytes);
    }

    private static void WriteVarintField(List<byte> ms, int fieldNumber, int value) {
        WriteTag(ms, fieldNumber, wireType: 0);
        WriteVarint(ms, unchecked((ulong)(uint)value));
    }

    private static void WriteTag(List<byte> ms, int fieldNumber, int wireType) {
        WriteVarint(ms, ((ulong)fieldNumber << 3) | (uint)wireType);
    }

    private static void WriteVarint(List<byte> ms, ulong value) {
        while (value >= 0x80) {
            ms.Add((byte)(value | 0x80));
            value >>= 7;
        }
        ms.Add((byte)value);
    }
}
