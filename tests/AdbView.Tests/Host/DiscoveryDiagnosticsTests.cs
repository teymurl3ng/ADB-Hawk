namespace AdbView.Tests.Host;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using AdbView.Host;
using Xunit;

public sealed class DiscoveryDiagnosticsTests {
    private sealed class FakeSource : INetworkDiagnosticsSource {
        public IReadOnlyList<int> Owners { get; init; } = Array.Empty<int>();
        public IReadOnlyList<NetworkAdapterProbeInfo> Adapters { get; init; } =
            Array.Empty<NetworkAdapterProbeInfo>();
        public IReadOnlyList<IPAddress> MulticastAddresses { get; init; } = Array.Empty<IPAddress>();

        public IReadOnlyList<int> GetDiscoveryPortOwnerPids() => Owners;

        public IReadOnlyList<NetworkAdapterProbeInfo> GetOperationalAdapters() => Adapters;

        public IReadOnlyList<IPAddress> GetMulticastCandidateAddresses() => MulticastAddresses;
    }

    private static NetworkAdapterProbeInfo Adapter(string name, string description) =>
        new(name, description, NetworkInterfaceType.Ethernet);

    [Fact]
    public void CollectExcludesOwnProcessAndAdbServerFromPortHolders() {
        var owners = new[] { 100, 200, 300, 400 };
        var source = new FakeSource { Owners = owners };
        // 100 = own process; 200 = adb.exe (legitimate binder); 300/400 = foreign apps.
        var evidence = DiscoveryDiagnostics.Collect(
            source,
            ownProcessId: 100,
            resolveProcessName: pid => pid switch {
                100 => "AdbView",
                200 => "adb",
                300 => "msedge",
                400 => "msedge", // duplicate name — deduped
                _ => null,
            });

        var expectedHolders = new[] { "msedge" };
        Assert.Equal(expectedHolders, evidence.PortHolderNames);
    }

    [Fact]
    public void CollectSkipsExitedPidsByResolverNull() {
        var owners = new[] { 1, 2 };
        var source = new FakeSource { Owners = owners };

        var evidence = DiscoveryDiagnostics.Collect(
            source,
            ownProcessId: -1,
            resolveProcessName: pid => pid == 1 ? null : "ChatGPT");

        var expectedHolders = new[] { "ChatGPT" };
        Assert.Equal(expectedHolders, evidence.PortHolderNames);
    }

    [Fact]
    public void CollectFlagsVirtualAdaptersByNameOrDescription() {
        var adapters = new[] {
            Adapter("Tailscale", "Tailscale Tunnel"),
            Adapter("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter"),
            Adapter("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz"),
            Adapter("Loopback Pseudo-Interface 1", "Loopback"),
        };
        var source = new FakeSource { Adapters = adapters };

        var evidence = DiscoveryDiagnostics.Collect(
            source,
            ownProcessId: -1,
            resolveProcessName: _ => null);

        var expectedVirtual = new[] { "Tailscale", "vEthernet (Default Switch)" };
        Assert.Equal(expectedVirtual, evidence.VirtualAdapterNames);
    }

    [Fact]
    public void CollectProducesEmptyEvidenceOnCleanHost() {
        var source = new FakeSource { Owners = new[] { 200 } };

        var evidence = DiscoveryDiagnostics.Collect(
            source,
            ownProcessId: -1,
            resolveProcessName: pid => pid == 200 ? "adb" : null);

        Assert.Empty(evidence.PortHolderNames);
        Assert.Empty(evidence.VirtualAdapterNames);
    }

    [Fact]
    public void VirtualAdapterHintsIncludeDocumentedSuspects() {
        // research/2026-09-19-host-mdns-empty-while-tcp-works.md §D: Tailscale + Hyper-V are the
        // documented suspects; the heuristic must match both.
        Assert.True(DiscoveryDiagnostics.MatchesVirtualAdapter(Adapter("Tailscale", "Tailscale Tunnel")));
        Assert.True(DiscoveryDiagnostics.MatchesVirtualAdapter(
            Adapter("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")));
        Assert.False(DiscoveryDiagnostics.MatchesVirtualAdapter(Adapter("Wi-Fi", "Intel(R) Wi-Fi 6")));
        Assert.False(DiscoveryDiagnostics.MatchesVirtualAdapter(Adapter("Ethernet", "Realtek PCIe GbE")));
    }
}
