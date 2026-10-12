namespace AdbView.Tests.Adb;

using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using AdbView.Adb;
using AdbView.Host;
using Xunit;

public sealed class MdnsFallbackEgressTests {
    [Fact]
    public async System.Threading.Tasks.Task BurstSendsEveryDomainOnEachFreshInterface() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        var first = IPAddress.Parse("192.168.1.2");
        var second = IPAddress.Parse("10.0.0.2");
        harness.Addresses = [first, second, first];
        await harness.StartAsync().ConfigureAwait(true);
        var socket = harness.Factory.Active;
        int domains = MdnsFallbackBrowser.ServiceDomains.Length;
        Assert.Equal(2 * domains, socket.Sends.Count);
        foreach (IPAddress address in new[] { first, second }) {
            Assert.Equal(domains, socket.SendInterfaces.Count(candidate => candidate.Equals(address)));
            var packets = socket.Sends.Where((_, index) => socket.SendInterfaces[index].Equals(address)).ToArray();
            for (int i = 0; i < domains; i++) {
                Assert.Equal(DnsMessageCodec.BuildQuery(0, MdnsFallbackBrowser.ServiceDomains[i]).AsSpan(2).ToArray(), packets[i].AsSpan(2).ToArray());
            }
        }

        harness.Addresses = [first];
        harness.Browser.TriggerQuery();
        await harness.StepAsync(1).ConfigureAwait(true);
        Assert.Equal(3 * domains, socket.Sends.Count);
        Assert.All(socket.SendInterfaces.Skip(2 * domains), address => Assert.Equal(first, address));
    }

    [Fact]
    public async System.Threading.Tasks.Task EmptyInterfaceListRestoresDefaultRouteAfterExplicitSelection() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        harness.Addresses = [IPAddress.Parse("192.168.1.2")];
        await harness.StartAsync().ConfigureAwait(true);
        harness.Addresses = Array.Empty<IPAddress>();
        harness.Browser.TriggerQuery();
        await harness.StepAsync(1).ConfigureAwait(true);
        Assert.Equal(IPAddress.Any, harness.Factory.Active.SelectedInterfaces.Last());
        Assert.Equal(MdnsFallbackBrowser.ServiceDomains.Length, harness.Factory.Active.SendInterfaces.Count(address => address.Equals(IPAddress.Any)));
    }

    [Fact]
    public async System.Threading.Tasks.Task FailingInterfaceDoesNotBlockAnotherInterface() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        await harness.StartAsync().ConfigureAwait(true);
        var failed = IPAddress.Parse("10.0.0.2");
        var working = IPAddress.Parse("192.168.1.2");
        harness.Factory.Active.FailedInterface = failed;
        harness.Addresses = [failed, working];
        harness.Browser.TriggerQuery();
        await harness.StepAsync(1).ConfigureAwait(true);
        Assert.Equal(MdnsFallbackBrowser.ServiceDomains.Length, harness.Factory.Active.SendInterfaces.Count(address => address.Equals(working)));
        Assert.DoesNotContain(failed, harness.Factory.Active.SendInterfaces);
    }

    [Theory]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Ethernet, true, true)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Wireless80211, true, true)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Tunnel, true, true)]
    [InlineData(OperationalStatus.Down, NetworkInterfaceType.Ethernet, true, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Loopback, true, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Ethernet, false, false)]
    public void BrowserAndProbeCandidateFilterRequiresUpMulticastNonLoopback(
        OperationalStatus status, NetworkInterfaceType type, bool multicast, bool expected) {
        Assert.Equal(expected, WindowsNetworkDiagnosticsSource.IsMulticastCandidate(status, type, multicast));
    }
}
