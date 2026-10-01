namespace AdbView.Adb;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Text;

/// <summary>
/// Minimal DNS wire codec for mDNS (RFC 6762/6763): builds single-question PTR queries,
/// collects PTR answer targets (legacy-unicast probe), and parses full record sets —
/// PTR/SRV/TXT/A/AAAA with TTLs and RFC 1035 name compression — for the fallback browser.
/// Instance names are never rendered anywhere (R-15).
/// </summary>
internal static class DnsMessageCodec {
    internal const int HeaderLength = 12;
    internal const ushort TypePtr = 12;
    internal const ushort ClassInet = 1;
    internal const ushort TypeA = 1;
    internal const ushort TypeTxt = 16;
    internal const ushort TypeAaaa = 28;
    internal const ushort TypeSrv = 33;
    private const int MaxPointerJumps = 32;

    /// <summary>One parsed resource record. Payload fields are null when not applicable.</summary>
    internal sealed record DnsRecord(
        string Name,
        ushort Type,
        uint Ttl,
        string? PtrTarget,
        string? SrvTarget,
        ushort? SrvPort,
        IReadOnlyList<KeyValuePair<string, string>>? TxtPairs,
        IPAddress? Address) {
        public static DnsRecord Ptr(string name, uint ttl, string target) =>
            new(name, TypePtr, ttl, target, null, null, null, null);
    }

    /// <summary>Builds a one-shot QM PTR query (no QU bit) for <paramref name="serviceDomain"/>.</summary>
    public static byte[] BuildQuery(ushort messageId, string serviceDomain) {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceDomain);

        var labels = serviceDomain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length == 0) {
            throw new ArgumentException("Service domain must contain at least one label.", nameof(serviceDomain));
        }

        int length = HeaderLength + 1 + 4; // header + root byte + QTYPE + QCLASS
        foreach (var label in labels) {
            int byteCount = Encoding.UTF8.GetByteCount(label);
            if (byteCount is 0 or > 63) {
                throw new ArgumentException("DNS label length must be 1..63 bytes.", nameof(serviceDomain));
            }

            length += 1 + byteCount;
        }

        var buffer = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), messageId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 0); // flags: standard query, QM
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), 1); // QDCOUNT = 1

        int offset = HeaderLength;
        foreach (var label in labels) {
            int byteCount = Encoding.UTF8.GetByteCount(label);
            buffer[offset] = (byte)byteCount;
            offset += 1;
            offset += Encoding.UTF8.GetBytes(label, buffer.AsSpan(offset));
        }

        buffer[offset] = 0; // root label
        offset += 1;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), TypePtr);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), ClassInet);
        return buffer;
    }

    /// <summary>
    /// Collects PTR record rdata names from a DNS response into <paramref name="targets"/>.
    /// Returns false when the message is malformed or not a response; well-formed messages
    /// always return true even with zero PTR records.
    /// </summary>
    public static bool TryCollectPtrTargets(ReadOnlySpan<byte> message, HashSet<string> targets) {
        ArgumentNullException.ThrowIfNull(targets);
        if (message.Length < HeaderLength) {
            return false;
        }

        // Flags bit 15: QR. Legacy responses always have it set.
        if ((BinaryPrimitives.ReadUInt16BigEndian(message.Slice(2, 2)) & 0x8000) == 0) {
            return false;
        }

        int questionCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(4, 2));
        int answerCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(6, 2));
        int authorityCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(8, 2));
        int additionalCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(10, 2));

        int offset = HeaderLength;
        for (int i = 0; i < questionCount; i++) {
            if (!TrySkipName(message, ref offset) || offset + 4 > message.Length) {
                return false;
            }

            offset += 4; // QTYPE + QCLASS
        }

        for (int i = 0; i < answerCount + authorityCount + additionalCount; i++) {
            if (!TrySkipName(message, ref offset) || offset + 10 > message.Length) {
                return false;
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
            offset += 2; // TYPE
            offset += 2; // CLASS
            offset += 4; // TTL
            ushort dataLength = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
            offset += 2;
            if (offset + dataLength > message.Length) {
                return false;
            }

            if (type == TypePtr
                && TryDecodeName(message, offset, out string? target, out int _)
                && target!.Length > 0) {
                targets.Add(target);
            }

            offset += dataLength;
        }

        return true;
    }

    private static bool TrySkipName(ReadOnlySpan<byte> message, ref int offset) {
        return TryDecodeName(message, offset, out _, out offset);
    }

    /// <summary>
    /// Parses every record (answer + authority + additional) with TTL and typed rdata into
    /// <paramref name="into"/>. Returns false when the message is malformed or not a response;
    /// well-formed messages always return true even with zero records.
    /// </summary>
    public static bool TryCollectRecords(ReadOnlySpan<byte> message, List<DnsRecord> into) {
        ArgumentNullException.ThrowIfNull(into);
        if (message.Length < HeaderLength) {
            return false;
        }

        if ((BinaryPrimitives.ReadUInt16BigEndian(message.Slice(2, 2)) & 0x8000) == 0) {
            return false; // QR bit: responses only.
        }

        int questionCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(4, 2));
        int answerCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(6, 2));
        int authorityCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(8, 2));
        int additionalCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(10, 2));

        int offset = HeaderLength;
        for (int i = 0; i < questionCount; i++) {
            if (!TrySkipName(message, ref offset) || offset + 4 > message.Length) {
                return false;
            }

            offset += 4;
        }

        int total = answerCount + authorityCount + additionalCount;
        for (int i = 0; i < total; i++) {
            if (!TryDecodeName(message, offset, out string? name, out offset) || name is null
                || offset + 10 > message.Length) {
                return false;
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
            offset += 2;
            offset += 2; // CLASS
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(message.Slice(offset, 4));
            offset += 4;
            ushort dataLength = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
            offset += 2;
            if (offset + dataLength > message.Length) {
                return false;
            }

            var record = ParseRecordData(name, type, ttl, message.Slice(offset, dataLength));
            if (record is not null) {
                into.Add(record);
            }

            offset += dataLength;
        }

        return true;
    }

    private static DnsRecord? ParseRecordData(string name, ushort type, uint ttl, ReadOnlySpan<byte> rdata) {
        switch (type) {
            case TypePtr:
                if (!TryDecodeName(rdata, 0, out string? target, out _) || target is null) {
                    return null;
                }

                return new DnsRecord(name, type, ttl, target, null, null, null, null);

            case TypeSrv: {
                    // priority(2) weight(2) port(2) target-name
                    if (rdata.Length < 7
                        || !TryDecodeName(rdata, 6, out string? srvTarget, out _) || srvTarget is null) {
                        return null;
                    }

                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(rdata.Slice(4, 2));
                    return new DnsRecord(name, type, ttl, null, srvTarget, port, null, null);
                }

            case TypeTxt: {
                    var pairs = new List<KeyValuePair<string, string>>();
                    var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int cursor = 0;
                    while (cursor < rdata.Length) {
                        int length = rdata[cursor];
                        cursor += 1;
                        if (cursor + length > rdata.Length) {
                            break;
                        }

                        string entry = Encoding.UTF8.GetString(rdata.Slice(cursor, length));
                        cursor += length;
                        int eq = entry.IndexOf('=', StringComparison.Ordinal);
                        if (eq > 0) {
                            string key = entry[..eq];
                            // RFC 6763 §6.4: duplicates — first occurrence wins.
                            if (seenKeys.Add(key)) {
                                pairs.Add(new KeyValuePair<string, string>(key, entry[(eq + 1)..]));
                            }
                        }
                    }

                    return new DnsRecord(name, type, ttl, null, null, null, pairs, null);
                }

            case TypeA:
                if (rdata.Length != 4) {
                    return null;
                }

                return new DnsRecord(name, type, ttl, null, null, null, null,
                    new IPAddress(rdata.ToArray()));

            case TypeAaaa:
                if (rdata.Length != 16) {
                    return null;
                }

                return new DnsRecord(name, type, ttl, null, null, null, null,
                    new IPAddress(rdata.ToArray()));

            default:
                return null; // Uninteresting type (NSEC, OPT, …) — parsed past, not surfaced.
        }
    }

    private static bool TryDecodeName(ReadOnlySpan<byte> message, int offset, out string? name, out int nextOffset) {
        var builder = new StringBuilder(96);
        int cursor = offset;
        nextOffset = offset;
        int jumps = 0;
        bool followedPointer = false;

        while (true) {
            if (cursor < 0 || cursor >= message.Length) {
                name = null;
                nextOffset = offset;
                return false;
            }

            int lengthByte = message[cursor];
            if (lengthByte == 0) {
                cursor += 1;
                if (!followedPointer) {
                    nextOffset = cursor;
                }
                // else: nextOffset was already fixed before the first pointer jump.

                break;
            }

            switch (lengthByte & 0xC0) {
                case 0xC0: // compression pointer
                    if (cursor + 1 >= message.Length) {
                        name = null;
                        nextOffset = offset;
                        return false;
                    }

                    int pointer = ((lengthByte & 0x3F) << 8) | message[cursor + 1];
                    if (++jumps > MaxPointerJumps) {
                        name = null;
                        nextOffset = offset;
                        return false;
                    }

                    if (!followedPointer) {
                        nextOffset = cursor + 2;
                        followedPointer = true;
                    }

                    cursor = pointer;
                    break;
                default: // ordinary label (0x40/0x80 extended labels unsupported — absent from mDNS PTR data)
                    int labelLength = lengthByte & 0x3F;
                    if (labelLength == 0 || cursor + 1 + labelLength > message.Length) {
                        name = null;
                        nextOffset = offset;
                        return false;
                    }

                    if (builder.Length > 0) {
                        builder.Append('.');
                    }

                    builder.Append(Encoding.UTF8.GetString(message.Slice(cursor + 1, labelLength)));
                    cursor += 1 + labelLength;
                    break;
            }
        }

        name = builder.ToString();
        return true;
    }
}
