namespace AdbView.Tests.State;

using System.Collections.Immutable;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.State;
using Xunit;

public sealed class MdnsEffectiveSetTests {
    private static RawMdnsService Service(string type, string instance, MdnsServiceKind kind) => new() {
        InstanceName = instance,
        ServiceType = type,
        Kind = kind,
        Format = RawMdnsFormat.ServicesList,
        Ipv4 = "192.168.0.53",
        Port = 40123,
    };

    private const string ConnectType = "_adb-tls-connect._tcp";
    private const string PairingType = "_adb-tls-pairing._tcp";

    [Fact]
    public void AdbRowsWinPerInstanceKeyAndFallbackFillsOnlyMissingKeys() {
        var adb = ImmutableArray.Create(
            Service(ConnectType, "adb-A06-abc123", MdnsServiceKind.Connect));
        var fallback = ImmutableArray.Create(
            Service(ConnectType, "adb-A06-abc123", MdnsServiceKind.Connect),      // duplicate key
            Service(PairingType, "adbview-dbae29df", MdnsServiceKind.Pairing));   // half-deaf gap

        var merged = MdnsEffectiveSet.Merge(adb, fallback);

        Assert.Equal(2, merged.Length);
        Assert.Contains(merged, s => s.DiscoveryKey == PairingType + "\u001fadbview-dbae29df");
        Assert.Single(merged, s => s.DiscoveryKey == ConnectType + "\u001fadb-A06-abc123");
    }

    [Fact]
    public void MergeIsStableAcrossRepeatedCalls() {
        var adb = ImmutableArray.Create(Service(ConnectType, "adb-A06-abc123", MdnsServiceKind.Connect));
        var fallback = ImmutableArray.Create(Service(PairingType, "adbview-x", MdnsServiceKind.Pairing));

        var first = MdnsEffectiveSet.Merge(adb, fallback);
        var second = MdnsEffectiveSet.Merge(adb, fallback);

        Assert.Equal(first.Length, second.Length);
        Assert.Equal(first[0].DiscoveryKey, second[0].DiscoveryKey);
        Assert.Equal(first[1].DiscoveryKey, second[1].DiscoveryKey);
    }

    [Fact]
    public void SingleSourceBehavesExactlyAsBefore() {
        var adb = ImmutableArray.Create(Service(ConnectType, "adb-A06", MdnsServiceKind.Connect));

        Assert.Single(MdnsEffectiveSet.Merge(adb, ImmutableArray<RawMdnsService>.Empty));
        Assert.Equal(ImmutableArray<RawMdnsService>.Empty, MdnsEffectiveSet.Merge(default, ImmutableArray<RawMdnsService>.Empty));

        var fallback = ImmutableArray.Create(Service(PairingType, "adbview-y", MdnsServiceKind.Pairing));
        var merged = MdnsEffectiveSet.Merge(default, fallback);
        Assert.Single(merged);
    }
}
