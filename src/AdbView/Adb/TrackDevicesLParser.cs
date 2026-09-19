namespace AdbView.Adb;

using System;
using System.Collections.Immutable;
using System.Text;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;

/// <summary>
/// Parses <c>host:track-devices-l</c> / <c>host:devices-l</c> long-list text payloads.
/// Does not invent transport / connection_type (Phase 5B).
/// </summary>
internal static class TrackDevicesLParser {
    public static ImmutableArray<RawDevice> Parse(ReadOnlySpan<byte> payload) {
        if (payload.IsEmpty) {
            return ImmutableArray<RawDevice>.Empty;
        }

        string text = Encoding.UTF8.GetString(payload);
        return Parse(text);
    }

    public static ImmutableArray<RawDevice> Parse(string text) {
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrWhiteSpace(text)) {
            return ImmutableArray<RawDevice>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<RawDevice>();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        foreach (string rawLine in lines) {
            string line = rawLine.Trim();
            if (line.Length == 0) {
                continue;
            }

            builder.Add(ParseLine(line));
        }

        return builder.ToImmutable();
    }

    private static RawDevice ParseLine(string line) {
        // Format (AOSP devices-l / track-devices-l):
        // SERIAL<ws>STATE[ product:x model:y device:z transport_id:n ...]
        int firstWs = IndexOfWhitespace(line);
        if (firstWs < 0) {
            throw new AdbProtocolException(
                AdbErrorCode.MalformedLength,
                $"Malformed track-devices-l line (missing state): '{line}'.");
        }

        string serial = line[..firstWs];
        int restStart = SkipWhitespace(line, firstWs);
        if (restStart >= line.Length) {
            throw new AdbProtocolException(
                AdbErrorCode.MalformedLength,
                $"Malformed track-devices-l line (missing state after serial): '{line}'.");
        }

        int stateEnd = IndexOfWhitespace(line, restStart);
        string stateToken;
        string attrs;
        if (stateEnd < 0) {
            stateToken = line[restStart..];
            attrs = string.Empty;
        }
        else {
            stateToken = line[restStart..stateEnd];
            attrs = line[SkipWhitespace(line, stateEnd)..];
        }

        if (stateToken.Length == 0) {
            throw new AdbProtocolException(
                AdbErrorCode.MalformedLength,
                $"Malformed track-devices-l line (empty state): '{line}'.");
        }

        string? product = null;
        string? model = null;
        string? device = null;

        if (attrs.Length > 0) {
            foreach (string part in attrs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                int colon = part.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0 || colon == part.Length - 1) {
                    continue;
                }

                string key = part[..colon];
                string value = part[(colon + 1)..];
                if (key.Equals("product", StringComparison.Ordinal)) {
                    product = value;
                }
                else if (key.Equals("model", StringComparison.Ordinal)) {
                    model = value;
                }
                else if (key.Equals("device", StringComparison.Ordinal)) {
                    device = value;
                }
            }
        }

        return new RawDevice {
            Serial = serial,
            ProtoState = null,
            StateToken = stateToken,
            ConnectionType = null,
            Product = product,
            Model = model,
            Device = device,
            Format = RawDeviceFormat.TrackDevicesL,
        };
    }

    private static int IndexOfWhitespace(string s, int start = 0) {
        for (int i = start; i < s.Length; i++) {
            if (char.IsWhiteSpace(s[i])) {
                return i;
            }
        }
        return -1;
    }

    private static int SkipWhitespace(string s, int start) {
        int i = start;
        while (i < s.Length && char.IsWhiteSpace(s[i])) {
            i++;
        }
        return i;
    }
}
