namespace AdbView.Tests.Adb;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AdbView.Adb;
using Xunit;

public sealed class DnsMessageCodecTests {
    private const string ServiceDomain = "_adb-tls-connect._tcp.local";

    [Fact]
    public void BuildQueryProducesSingleQuestionPtrQuery() {
        var query = DnsMessageCodec.BuildQuery(0x1234, ServiceDomain);

        // Header: id, flags=0 (QM), QDCOUNT=1, ANCOUNT/NSCOUNT/ARCOUNT=0
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(6, 2)));

        // Question name = "_adb-tls-connect"."_tcp"."local" then root.
        Assert.Equal((byte)16, query[12]);
        Assert.Equal("_adb-tls-connect", System.Text.Encoding.UTF8.GetString(query, 13, 16));
        Assert.Equal((byte)4, query[29]);
        Assert.Equal("_tcp", System.Text.Encoding.UTF8.GetString(query, 30, 4));
        Assert.Equal((byte)5, query[34]);
        Assert.Equal("local", System.Text.Encoding.UTF8.GetString(query, 35, 5));
        Assert.Equal(0, query[40]);

        // QTYPE=PTR(12), QCLASS=IN(1)
        int tail = query.Length - 4;
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(tail, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(tail + 2, 2)));
    }

    [Fact]
    public void BuildQueryRejectsEmptyDomain() {
        Assert.Throws<ArgumentException>(() => DnsMessageCodec.BuildQuery(1, ""));
    }

    [Fact]
    public void TryCollectPtrTargetsReadsCompressedLegacyResponse() {
        var message = BuildLegacyResponse(
            questionName: ServiceDomain,
            answerNamePointerToQuestion: true,
            ptrTarget: "adb-R8VXA02SNKH",
            ptrTargetCompressionBaseOffset: 12);

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(DnsMessageCodec.TryCollectPtrTargets(message, targets));
        var target = Assert.Single(targets);
        Assert.Equal("adb-R8VXA02SNKH._adb-tls-connect._tcp.local", target);
    }

    [Fact]
    public void TryCollectPtrTargetsWithoutPointerCompressionReadsFullName() {
        // rdata carries the full instance name without compression.
        var message = BuildLegacyResponse(
            questionName: ServiceDomain,
            answerNamePointerToQuestion: true,
            ptrTarget: "adb-abc123._adb-tls-connect._tcp.local",
            ptrTargetCompressionBaseOffset: null);

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(DnsMessageCodec.TryCollectPtrTargets(message, targets));
        Assert.Equal(
            "adb-abc123._adb-tls-connect._tcp.local",
            Assert.Single(targets));
    }

    [Fact]
    public void NonResponseMessageIsRejected() {
        var query = DnsMessageCodec.BuildQuery(0x1234, ServiceDomain);

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.False(DnsMessageCodec.TryCollectPtrTargets(query, targets));
        Assert.Empty(targets);
    }

    [Fact]
    public void TruncatedMessageIsRejected() {
        var message = BuildLegacyResponse(
            questionName: ServiceDomain,
            answerNamePointerToQuestion: true,
            ptrTarget: "adb-x",
            ptrTargetCompressionBaseOffset: 12);

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.False(DnsMessageCodec.TryCollectPtrTargets(message.AsSpan(0, message.Length - 5).ToArray(), targets));
    }

    [Fact]
    public void ResponseWithoutPtrRecordsIsWellFormedWithNoTargets() {
        var message = BuildLegacyResponse(
            questionName: ServiceDomain,
            answerNamePointerToQuestion: true,
            ptrTarget: "adb-x",
            ptrTargetCompressionBaseOffset: 12,
            answerTypeOverride: 33); // SRV — not a PTR

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(DnsMessageCodec.TryCollectPtrTargets(message, targets));
        Assert.Empty(targets);
    }

    /// <summary>
    /// Builds a legacy-unicast response: QR set, one question, one answer whose name is a
    /// compression pointer back to the question (exactly what responders emit for PTR answers).
    /// </summary>
    private static byte[] BuildLegacyResponse(
        string questionName,
        bool answerNamePointerToQuestion,
        string ptrTarget,
        int? ptrTargetCompressionBaseOffset,
        ushort answerTypeOverride = DnsMessageCodec.TypePtr) {
        var labels = questionName.Split('.');
        var body = new List<byte>();

        // Question: uncompressed name + QTYPE + QCLASS
        int questionNameStart = DnsMessageCodec.HeaderLength;
        foreach (var label in labels) {
            var labelBytes = System.Text.Encoding.UTF8.GetBytes(label);
            body.Add((byte)labelBytes.Length);
            body.AddRange(labelBytes);
        }

        body.Add(0);
        int questionEnd = questionNameStart + body.Count;
        AddUInt16(body, DnsMessageCodec.TypePtr);
        AddUInt16(body, DnsMessageCodec.ClassInet);

        // Answer: NAME (pointer to question name), TYPE, CLASS, TTL, RDLENGTH, RDATA
        if (answerNamePointerToQuestion) {
            body.Add(0xC0);
            body.Add((byte)(questionNameStart & 0xFF));
        }
        else {
            foreach (var label in labels) {
                var labelBytes = System.Text.Encoding.UTF8.GetBytes(label);
                body.Add((byte)labelBytes.Length);
                body.AddRange(labelBytes);
            }

            body.Add(0);
        }

        AddUInt16(body, answerTypeOverride);
        AddUInt16(body, DnsMessageCodec.ClassInet);
        AddUInt32(body, ttl: 10);

        // RDATA for PTR: a domain name (optionally ending in a compression pointer).
        var rdata = new List<byte>();
        if (ptrTargetCompressionBaseOffset is int baseOffset) {
            // Only the first label is uncompressed; the suffix is a pointer.
            var firstLabel = ptrTarget.Split('.')[0];
            var firstLabelBytes = System.Text.Encoding.UTF8.GetBytes(firstLabel);
            rdata.Add((byte)firstLabelBytes.Length);
            rdata.AddRange(firstLabelBytes);
            rdata.Add(0xC0);
            rdata.Add((byte)(baseOffset & 0xFF));
            // The compression base must point at the shared suffix start within the question:
            // for "_adb-tls-connect._tcp.local" the pointer target is the question name start
            // only when the target label list continues with the full question suffix.
        }
        else {
            foreach (var label in ptrTarget.Split('.')) {
                var labelBytes = System.Text.Encoding.UTF8.GetBytes(label);
                rdata.Add((byte)labelBytes.Length);
                rdata.AddRange(labelBytes);
            }

            rdata.Add(0);
        }

        AddUInt16(body, (ushort)rdata.Count);
        body.AddRange(rdata);

        var message = new byte[DnsMessageCodec.HeaderLength + body.Count];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0, 2), 0x4242);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), 0x8000); // QR=1
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4, 2), 1); // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6, 2), 1); // ANCOUNT
        body.CopyTo(message, DnsMessageCodec.HeaderLength);
        Assert.True(questionEnd > questionNameStart); // compression base sanity
        return message;
    }

    private static void AddUInt16(List<byte> into, ushort value) {
        into.Add((byte)(value >> 8));
        into.Add((byte)(value & 0xFF));
    }

    private static void AddUInt32(List<byte> into, uint ttl) {
        into.Add((byte)(ttl >> 24));
        into.Add((byte)((ttl >> 16) & 0xFF));
        into.Add((byte)((ttl >> 8) & 0xFF));
        into.Add((byte)(ttl & 0xFF));
    }
}
