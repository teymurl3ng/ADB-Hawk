namespace AdbView.Tests.Adb;

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using AdbView.Adb;
using AdbView.Adb.Protocol;
using AdbView.Host;
using AdbView.State;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class MdnsFallbackRejoinTests {
    [Fact]
    public async System.Threading.Tasks.Task AddressEventsCoalesceAndRejoinOnlyAfterOneSecondOfQuiet() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        var original = IPAddress.Parse("192.168.1.2");
        var current = IPAddress.Parse("192.168.2.2");
        harness.Addresses = [original];
        await harness.StartAsync().ConfigureAwait(true);
        var passive = harness.Factory.Sockets[0];
        Assert.Equal(original, Assert.Single(passive.Joins));
        harness.Addresses = [current];
        harness.Browser.TriggerNetworkRejoin();
        harness.Browser.TriggerNetworkRejoin();
        Assert.Equal(2, harness.Factory.Sockets.Count); // Callback never opens sockets.
        await harness.StepAsync(0).ConfigureAwait(true);
        harness.Browser.TriggerNetworkRejoin();
        await harness.StepAsync(0.8).ConfigureAwait(true);
        await harness.StepAsync(1.799).ConfigureAwait(true);
        Assert.Equal(2, harness.Factory.Sockets.Count);
        Assert.False(passive.Disposed);
        await harness.StepAsync(1.8).ConfigureAwait(true);
        Assert.True(passive.Disposed);
        Assert.False(harness.Factory.Active.Disposed);
        Assert.Equal(current, Assert.Single(harness.Factory.Sockets[2].Joins));
        Assert.Equal(2 * MdnsFallbackBrowser.ServiceDomains.Length, harness.Factory.Active.Sends.Count);
        await harness.StepAsync(50).ConfigureAwait(true);
        Assert.Equal(3, harness.Factory.Sockets.Count); // No event means no rejoin polling.
        Assert.All(harness.Factory.Active.ReceiveTimeouts, timeout => Assert.Equal(0, timeout));
    }

    [Fact]
    public async System.Threading.Tasks.Task FailedInitialPassiveBindCanRecoverOnNetworkEvent() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        harness.Factory.FailedBindNumber = 0;
        harness.Addresses = [IPAddress.Parse("192.168.1.2")];
        await harness.StartAsync().ConfigureAwait(true);
        Assert.False(harness.Browser.PassiveBound);
        harness.Browser.TriggerNetworkRejoin();
        await harness.StepAsync(0).ConfigureAwait(true);
        await harness.StepAsync(1).ConfigureAwait(true);
        Assert.True(harness.Browser.PassiveBound);
        Assert.Single(harness.Factory.Sockets[2].Joins);
        Assert.True(harness.Factory.Sockets[0].Disposed);
    }

    [Fact]
    public async System.Threading.Tasks.Task RejoinAndQrDueInSameSliceProduceOneBurst() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        await harness.StartAsync().ConfigureAwait(true);
        harness.Browser.StartQrQueryWindow();
        await harness.StepAsync(0).ConfigureAwait(true);
        harness.Browser.TriggerNetworkRejoin();
        await harness.StepAsync(1).ConfigureAwait(true);
        await harness.StepAsync(2).ConfigureAwait(true);
        Assert.Equal(3 * MdnsFallbackBrowser.ServiceDomains.Length, harness.Factory.Active.Sends.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task EmptyAdbBrowseAndFailedPassiveRebindStillDeliverActiveFallbackWithoutServerMutation() {
        var server = new FakeAdbServer();
        await using var serverCleanup = server.ConfigureAwait(true);
        server.RespondOkayOneShot("\n");
        var connection = new AdbHostConnection(endPoint: server.EndPoint);
        await using var connectionCleanup = connection.ConfigureAwait(true);
        var payload = await connection.ExecuteOneShotAsync("host:mdns:services", TestContext.Current.CancellationToken).ConfigureAwait(true);
        var adbRows = MdnsServicesListParser.Parse(System.Text.Encoding.UTF8.GetString(payload.Span));
        Assert.Empty(adbRows);

        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        await harness.StartAsync().ConfigureAwait(true);
        harness.Factory.FailedBindNumber = 2;
        harness.Browser.TriggerNetworkRejoin();
        await harness.StepAsync(0).ConfigureAwait(true);
        await harness.StepAsync(1).ConfigureAwait(true);
        Assert.False(harness.Browser.PassiveBound);
        harness.Factory.Active.Incoming.Enqueue(DnsTestMessages.Response(
            DnsTestMessages.Ptr("_adb-tls-pairing._tcp.local", 120, "adbview-test._adb-tls-pairing._tcp.local"),
            DnsTestMessages.Srv("adbview-test._adb-tls-pairing._tcp.local", 120, 40123, "phone.local"),
            DnsTestMessages.A("phone.local", 120, "192.168.1.5")));
        await harness.StepAsync(1.2).ConfigureAwait(true);
        var merged = MdnsEffectiveSet.Merge(adbRows, MdnsFallbackMapper.Map(harness.Browser.Instances.Values.ToImmutableArray()));
        var row = Assert.Single(merged);
        Assert.Equal("192.168.1.5", row.Ipv4);
        Assert.Equal(40123, row.Port);
        Assert.Equal("host:mdns:services", Assert.Single(server.ReceivedCommands));
        // A later event retries passive setup without disrupting the active socket.
        harness.Browser.TriggerNetworkRejoin();
        await harness.StepAsync(2).ConfigureAwait(true);
        await harness.StepAsync(3).ConfigureAwait(true);
        Assert.True(harness.Browser.PassiveBound);
        Assert.False(harness.Factory.Active.Disposed);
    }
}
