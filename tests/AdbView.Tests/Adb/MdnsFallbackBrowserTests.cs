namespace AdbView.Tests.Adb;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AdbView.Adb;
using AdbView.Host;
using Xunit;

public sealed class MdnsFallbackBrowserTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SharedHostAddressResolvesEveryServiceInEitherPacketOrder() {
        var browser = CreateBrowser();
        browser.Absorb(DnsTestMessages.Response(DnsTestMessages.A("phone.local", 120, "192.0.2.10")), T0);
        browser.Absorb(DnsTestMessages.Response(
            DnsTestMessages.Srv("adb-C._adb-tls-connect._tcp.local", 120, 40123, "phone.local"),
            DnsTestMessages.Srv("adb-P._adb-tls-pairing._tcp.local", 120, 40124, "phone.local")), T0);
        Assert.Equal(2, browser.Instances.Count);
        Assert.All(browser.Instances.Values, instance => Assert.Equal("192.0.2.10", instance.Ipv4));
        var rows = MdnsFallbackMapper.Map(browser.Instances.Values.ToImmutableArray());
        Assert.Equal(40124, Assert.Single(rows, row => row.Kind == AdbView.Adb.Model.MdnsServiceKind.Pairing).Port);
    }

    [Fact]
    public void LongPtrRefreshDoesNotExtendExpiredAddressOrPort() {
        var browser = CreateBrowser();
        browser.Absorb(DnsTestMessages.Response(
            DnsTestMessages.Srv("adb-C._adb-tls-connect._tcp.local", 120, 40123, "phone.local"),
            DnsTestMessages.A("phone.local", 120, "192.0.2.10")), T0);
        browser.Absorb(DnsTestMessages.Response(
            DnsTestMessages.Ptr("_adb-tls-connect._tcp.local", 4500, "adb-C._adb-tls-connect._tcp.local")), T0);
        Assert.True(browser.SweepExpired(T0.AddSeconds(121)));
        var instance = Assert.Single(browser.Instances.Values);
        Assert.Null(instance.Ipv4);
        Assert.Equal(0, instance.Port);
    }

    [Fact]
    public void AddressGoodbyeRemovesAddressWithoutInventingLongerTtl() {
        var browser = CreateBrowser();
        browser.Absorb(DnsTestMessages.Response(
            DnsTestMessages.Srv("adb-C._adb-tls-connect._tcp.local", 120, 40123, "phone.local"),
            DnsTestMessages.A("phone.local", 120, "192.0.2.10")), T0);
        browser.Absorb(DnsTestMessages.Response(DnsTestMessages.A("phone.local", 0, "192.0.2.10")), T0.AddSeconds(1));
        Assert.Null(Assert.Single(browser.Instances.Values).Ipv4);
    }

    [Fact]
    public void ShortLivedRecordsRefreshBeforeExpiryAndEmptyCacheKeepsQuietFloor() {
        var cache = new MdnsRecordCache();
        var records = new List<DnsMessageCodec.DnsRecord>();
        Assert.True(DnsMessageCodec.TryCollectRecords(DnsTestMessages.Response(
            DnsTestMessages.Ptr("_adb-tls-connect._tcp.local", 10, "adb-C._adb-tls-connect._tcp.local"),
            DnsTestMessages.Srv("adb-C._adb-tls-connect._tcp.local", 10, 40123, "phone.local"),
            DnsTestMessages.A("phone.local", 10, "192.0.2.10"),
            DnsTestMessages.Txt("adb-C._adb-tls-connect._tcp.local", 10)), records));
        cache.Absorb(records, T0);
        Assert.Equal(TimeSpan.FromSeconds(8), cache.RefreshDelay(T0));
        cache.MarkRefreshSent(T0.AddSeconds(8));
        Assert.Equal(TimeSpan.FromMilliseconds(500), cache.RefreshDelay(T0.AddSeconds(8)));
        cache.Clear();
        Assert.Equal(MdnsFallbackBrowser.RequeryFloor, cache.RefreshDelay(T0));
    }

    [Fact]
    public void MissingRecordsProduceSpecificResolutionQuestions() {
        var cache = new MdnsRecordCache();
        cache.Absorb(new[] { DnsMessageCodec.DnsRecord.Ptr("_adb-tls-pairing._tcp.local", 120,
            "adb-P._adb-tls-pairing._tcp.local") }, T0);
        var questions = cache.TakeDueQuestions(T0.AddSeconds(1));
        Assert.Contains(questions, question => question.Type == DnsMessageCodec.TypeSrv);
        Assert.Contains(questions, question => question.Type == DnsMessageCodec.TypeTxt);
        Assert.Empty(cache.TakeDueQuestions(T0.AddSeconds(1.5)));
    }

    [Fact]
    public void NewUniqueAddressSupersedesOldAddressAndIpv6SurvivesProjection() {
        var cache = new MdnsRecordCache();
        var records = new List<DnsMessageCodec.DnsRecord>();
        DnsMessageCodec.TryCollectRecords(DnsTestMessages.Response(
            DnsTestMessages.Srv("adb-C._adb-tls-connect._tcp.local", 120, 40123, "phone.local"),
            DnsTestMessages.A("phone.local", 120, "192.0.2.10")), records);
        cache.Absorb(records, T0);
        var next = new DnsMessageCodec.DnsRecord("phone.local", DnsMessageCodec.TypeA, 120,
            null, null, null, null, System.Net.IPAddress.Parse("192.0.2.11"), CacheFlush: true);
        var ipv6 = next with { Type = DnsMessageCodec.TypeAaaa, Address = System.Net.IPAddress.Parse("2001:db8::1") };
        cache.Absorb(new[] { next, ipv6 }, T0.AddSeconds(2));
        Assert.Equal("192.0.2.11", Assert.Single(cache.Instances.Values).Ipv4);
        cache.Sweep(T0.AddSeconds(3.1));
        var row = Assert.Single(MdnsFallbackMapper.Map(cache.Instances.Values.ToImmutableArray()));
        Assert.Equal("192.0.2.11", row.Ipv4);
        Assert.Equal("2001:db8::1", Assert.Single(row.Ipv6));
    }

    [Theory]
    [InlineData("adb-R8VXA02SNKH._adb-tls-connect._tcp.local", "adb-R8VXA02SNKH", "_adb-tls-connect._tcp")]
    [InlineData("adb-abc123._adb-tls-pairing._tcp.local.", "adb-abc123", "_adb-tls-pairing._tcp")]
    [InlineData("adb-R8VXA02SNKH._adb._tcp.local", "adb-R8VXA02SNKH", "_adb._tcp")]
    public void TrySplitInstanceSeparatesInstanceFromServiceType(string fqdn, string instance, string serviceType) {
        Assert.True(MdnsFallbackBrowser.TrySplitInstance(fqdn, out string? actualInstance, out string? actualType));
        Assert.Equal(instance, actualInstance);
        Assert.Equal(serviceType, actualType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nolabels")]
    [InlineData("instance.notmdns")]
    [InlineData(".leading-dot.local")]
    public void TrySplitInstanceRejectsNonServiceNames(string fqdn) {
        Assert.False(MdnsFallbackBrowser.TrySplitInstance(fqdn, out _, out _));
    }

    [Fact]
    public void AbsorbBuildsInstanceFromPtrSrvTxtA() {
        var browser = CreateBrowser();
        var packet = DnsTestMessages.Response(
            DnsTestMessages.Ptr("_adb-tls-connect._tcp.local", 4500, "adb-abc123._adb-tls-connect._tcp.local"),
            DnsTestMessages.Srv("adb-abc123._adb-tls-connect._tcp.local", 4500, 41953, "adb-abc123.local"),
            DnsTestMessages.A("adb-abc123.local", 4500, "192.168.18.248"),
            DnsTestMessages.Txt("adb-abc123._adb-tls-connect._tcp.local", 4500, ("v", "2"), ("name", "SM-A065F"), ("api", "36")));

        Assert.True(browser.Absorb(packet, T0));

        var entry = Assert.Single(browser.Instances.Values);
        Assert.Equal("adb-abc123", entry.InstanceName);
        Assert.Equal("_adb-tls-connect._tcp", entry.ServiceType);
        Assert.Equal(41953, entry.Port);
        Assert.Equal("192.168.18.248", entry.Ipv4);
        Assert.Equal("SM-A065F", entry.ProductModel);
        Assert.Equal("36", entry.BuildVersionSdkFull);
        Assert.True(entry.ExpiresAt > T0);
    }

    [Fact]
    public void AbsorbIgnoresUntrackedServiceTypes() {
        var browser = CreateBrowser();
        var packet = DnsTestMessages.Response(
            DnsTestMessages.Ptr("_googlecast._tcp.local", 4500, "Chromecast._googlecast._tcp.local"));

        Assert.False(browser.Absorb(packet, T0));
        Assert.Empty(browser.Instances);
    }

    [Fact]
    public void GoodbyeWithZeroTtlEvictsInstance() {
        var browser = CreateBrowser();
        browser.Absorb(
            DnsTestMessages.Response(DnsTestMessages.Record(
                DnsTestMessages.Ptr("_adb-tls-connect._tcp.local", 4500, "adb-abc123._adb-tls-connect._tcp.local"))),
            T0);

        var goodbye = DnsTestMessages.Response(DnsTestMessages.Record(
            DnsTestMessages.Ptr("_adb-tls-connect._tcp.local", 0, "adb-abc123._adb-tls-connect._tcp.local")));
        Assert.True(browser.Absorb(goodbye, T0.AddSeconds(1)));
        Assert.Empty(browser.Instances);

        // A lone goodbye for an unknown instance changes nothing.
        Assert.False(browser.Absorb(goodbye, T0.AddSeconds(2)));
    }

    [Fact]
    public void ReAnnouncementAfterGoodbyeRecreatesInstance() {
        var browser = CreateBrowser();
        var announce = DnsTestMessages.Response(DnsTestMessages.Record(
            DnsTestMessages.Ptr("_adb-tls-connect._tcp.local", 4500, "adb-abc123._adb-tls-connect._tcp.local")));
        browser.Absorb(announce, T0);
        browser.Absorb(
            DnsTestMessages.Response(DnsTestMessages.Record(
                DnsTestMessages.Ptr("_adb-tls-connect._tcp.local", 0, "adb-abc123._adb-tls-connect._tcp.local"))),
            T0.AddSeconds(1));

        Assert.True(browser.Absorb(announce, T0.AddSeconds(2)));
        Assert.Single(browser.Instances);
    }

    [Fact]
    public void ExpirySweepDropsStaleInstances() {
        var browser = CreateBrowser();
        browser.Absorb(
            DnsTestMessages.Response(DnsTestMessages.Record(
                DnsTestMessages.Ptr("_adb-tls-connect._tcp.local", ttl: 30, "adb-abc123._adb-tls-connect._tcp.local"))),
            T0);

        Assert.True(browser.SweepExpired(T0.AddSeconds(31)));
        Assert.False(browser.SweepExpired(T0.AddSeconds(32)));
        Assert.Empty(browser.Instances);
    }

    [Fact]
    public void DuplicateTxtKeysKeepFirstOccurrence() {
        var browser = CreateBrowser();
        browser.Absorb(
            DnsTestMessages.Response(DnsTestMessages.Record(
                DnsTestMessages.Ptr("_adb-tls-connect._tcp.local", 4500, "adb-abc123._adb-tls-connect._tcp.local"))),
            T0);

        browser.Absorb(DnsTestMessages.Response(DnsTestMessages.Record(
            DnsTestMessages.Txt("adb-abc123._adb-tls-connect._tcp.local", 4500, ("name", "First"), ("name", "Second")))),
            T0.AddSeconds(1));

        Assert.Equal("First", browser.Instances.Values.Single().ProductModel);
    }

    private static MdnsFallbackBrowser CreateBrowser() {
        return new MdnsFallbackBrowser(
            new MdnsFallbackBrowserTestsSocketFactory(),
            () => Array.Empty<System.Net.IPAddress>(),
            () => T0,
            _ => { });
    }

    private sealed class MdnsFallbackBrowserTestsSocketFactory : IUdpSocketFactory {
        public IUdpSocket Create() => throw new InvalidOperationException("Absorb tests never open sockets.");
    }
}

/// <summary>Minimal mDNS response packet builder for browser/codec tests.</summary>
internal static class DnsTestMessages {
    internal sealed record DnsRecord {
        public required string Name { get; init; }
        public required ushort Type { get; init; }
        public required uint Ttl { get; init; }
        public required byte[] Rdata { get; init; }

        public override string ToString() => Name;
    }

    /// <summary>Passthrough so call sites can group records: <c>Record(Ptr(...))</c>.</summary>
    public static DnsRecord[] Record(params DnsRecord[] records) => records;

    public static DnsRecord Ptr(string name, uint ttl, string target) =>
        new() { Name = name, Type = DnsMessageCodec.TypePtr, Ttl = ttl, Rdata = EncodeName(target) };

    public static DnsRecord Srv(string name, uint ttl, ushort port, string target) {
        var rdata = new List<byte> { 0, 0, 0, 0, (byte)(port >> 8), (byte)(port & 0xFF) };
        rdata.AddRange(EncodeName(target));
        return new DnsRecord { Name = name, Type = DnsMessageCodec.TypeSrv, Ttl = ttl, Rdata = rdata.ToArray() };
    }

    public static DnsRecord A(string name, uint ttl, string ipv4) =>
        new() {
            Name = name,
            Type = DnsMessageCodec.TypeA,
            Ttl = ttl,
            Rdata = System.Net.IPAddress.Parse(ipv4).GetAddressBytes(),
        };

    public static DnsRecord Txt(string name, uint ttl, params (string Key, string Value)[] pairs) {
        var rdata = new List<byte>();
        foreach (var (key, value) in pairs) {
            var entry = System.Text.Encoding.UTF8.GetBytes(key + "=" + value);
            rdata.Add((byte)entry.Length);
            rdata.AddRange(entry);
        }

        return new DnsRecord { Name = name, Type = DnsMessageCodec.TypeTxt, Ttl = ttl, Rdata = rdata.ToArray() };
    }

    public static byte[] Response(params DnsRecord[] records) {
        var body = new List<byte>();
        foreach (var record in records) {
            body.AddRange(EncodeName(record.Name));
            AddUInt16(body, record.Type);
            AddUInt16(body, DnsMessageCodec.ClassInet);
            AddUInt32(body, record.Ttl);
            AddUInt16(body, (ushort)record.Rdata.Length);
            body.AddRange(record.Rdata);
        }

        var message = new byte[12 + body.Count];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0, 2), 0x4242);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), 0x8000); // QR=1
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6, 2), (ushort)records.Length); // ANCOUNT
        body.CopyTo(message, 12);
        return message;
    }

    private static byte[] EncodeName(string fqdn) {
        var bytes = new List<byte>();
        foreach (var label in fqdn.TrimEnd('.').Split('.')) {
            var labelBytes = System.Text.Encoding.UTF8.GetBytes(label);
            if (labelBytes.Length > 63) {
                throw new ArgumentException("Label too long: " + label);
            }

            bytes.Add((byte)labelBytes.Length);
            bytes.AddRange(labelBytes);
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    internal static byte[] CompressedPairingResponse(string instance) {
        const string service = "_adb-tls-pairing._tcp.local";
        var message = new List<byte>(DnsMessageCodec.BuildQuery(0x4242, service));
        message[2] = 0x80;
        message[7] = 3;
        byte[] label = System.Text.Encoding.UTF8.GetBytes(instance);
        int instanceOffset = message.Count + 12;
        AddCompressedHeader(message, 12, DnsMessageCodec.TypePtr, (ushort)(label.Length + 3));
        message.Add((byte)label.Length);
        message.AddRange(label);
        AddUInt16(message, 0xc00c); // PTR target's service suffix in the question.

        AddCompressedHeader(message, instanceOffset, DnsMessageCodec.TypeSrv, 14);
        AddUInt16(message, 0);
        AddUInt16(message, 0);
        AddUInt16(message, 40123);
        int hostOffset = message.Count;
        message.Add(5);
        message.AddRange(System.Text.Encoding.ASCII.GetBytes("phone"));
        int localOffset = 12 + 1 + "_adb-tls-pairing".Length + 1 + "_tcp".Length;
        AddUInt16(message, (ushort)(0xc000 | localOffset)); // SRV target's .local suffix.

        AddCompressedHeader(message, hostOffset, DnsMessageCodec.TypeA, 4);
        message.AddRange(System.Net.IPAddress.Parse("192.168.1.5").GetAddressBytes());
        return message.ToArray();
    }

    private static void AddCompressedHeader(List<byte> into, int nameOffset, ushort type, ushort length) {
        AddUInt16(into, (ushort)(0xc000 | nameOffset));
        AddUInt16(into, type);
        AddUInt16(into, DnsMessageCodec.ClassInet);
        AddUInt32(into, 120);
        AddUInt16(into, length);
    }

    private static void AddUInt16(List<byte> into, ushort value) {
        into.Add((byte)(value >> 8));
        into.Add((byte)(value & 0xFF));
    }

    private static void AddUInt32(List<byte> into, uint value) {
        into.Add((byte)(value >> 24));
        into.Add((byte)((value >> 16) & 0xFF));
        into.Add((byte)((value >> 8) & 0xFF));
        into.Add((byte)(value & 0xFF));
    }
}

/// <summary>Decision 0011 amendment: fallback rows must carry the serial extracted from the
/// AOSP instance name — without it, DeviceIdentity rule 2c never resolves and one phone renders
/// as two cards.</summary>
public sealed class MdnsFallbackMapperTests {
    [Theory]
    [InlineData("adb-R5CW1234-abc123", "R5CW1234")]   // TLS-connect: adb-<serial>-<6 alphanum>
    [InlineData("adb-R5CW-12-34-abc123", "R5CW-12-34")] // dashes in serial tolerated (greedy middle)
    [InlineData("adb-R8VXA02SNKH", "R8VXA02SNKH")]     // legacy: adb-<serial> (no suffix)
    public void ExtractsSerialFromAospInstanceNames(string instance, string expected) {
        Assert.Equal(expected, MdnsFallbackMapper.ExtractSerialFromInstance(instance));
    }

    [Theory]
    [InlineData("")]
    [InlineData("adbview-1a2b3c4d")]       // our own QR pairing instances are not adb serials
    [InlineData("adb-")]
    [InlineData("adbview-1a2b3c4d-x")]
    [InlineData("adb-R5CW1234-abc123-x")]  // suffix not exactly 6
    [InlineData("Chromecast._googlecast")]
    public void RejectsNonSerialInstances(string instance) {
        Assert.Null(MdnsFallbackMapper.ExtractSerialFromInstance(instance));
    }

    [Fact]
    public void MappedRowsCarryDerivedSerialForIdentityResolution() {
        var instances = ImmutableArray.Create(new MdnsFallbackInstance {
            ServiceType = "_adb-tls-connect._tcp",
            InstanceName = "adb-R5CW1234-abc123",
            Port = 41953,
            Ipv4 = "192.168.18.248",
            ExpiresAt = DateTimeOffset.UtcNow,
        });

        var rows = MdnsFallbackMapper.Map(instances);

        var row = Assert.Single(rows);
        Assert.Equal("R5CW1234", row.Serial);
        Assert.Equal("192.168.18.248", row.Ipv4);
        Assert.Equal(41953, row.Port);
    }
}
