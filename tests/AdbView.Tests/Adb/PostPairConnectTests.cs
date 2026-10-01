using System.Collections.Immutable;
using System.Net;
using AdbView.Adb.Model;
using AdbView.Host;
using AdbView.State;
using AdbView.Tests.Fakes;

namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Protocol;
using Xunit;

public sealed class HostConnectClientTests {
    private sealed class FakeConnection : IAdbHostConnection {
        private readonly EndPoint _endPoint = new IPEndPoint(System.Net.IPAddress.Loopback, 5037);
        public Func<string, ReadOnlyMemory<byte>>? Handler { get; set; }
        public List<string> Services { get; } = new();

        public EndPoint EndPoint => _endPoint;

        public Task<ReadOnlyMemory<byte>> ExecuteOneShotAsync(string service, CancellationToken ct = default) {
            Services.Add(service);
            if (Handler is null) {
                throw new AdbProtocolException(AdbErrorCode.UnexpectedEof, "no handler");
            }

            return Task.FromResult(Handler(service));
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ExecuteFramedStreamAsync(
            string service,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
            await Task.CompletedTask.ConfigureAwait(false);
            yield return Handler?.Invoke(service) ?? ReadOnlyMemory<byte>.Empty;
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static AdbProtocolException UnknownService() =>
        new(AdbErrorCode.CommandFailed, "failed", serverReason: "unknown host service 'host:connect:...'");

    [Fact]
    public async Task SuccessBodyClassifiesConnectedAndBuildsIpv4Service() {
        using var connection = new FakeConnection {
            Handler = _ => Encoding.UTF8.GetBytes("connected to 192.168.18.248:41953"),
        };
        var client = new HostConnectClient(connection);

        var result = await client.ConnectAsync("192.168.18.248", 41953, TestContext.Current.CancellationToken);

        Assert.Equal(HostConnectOutcome.Connected, result.Outcome);
        Assert.Equal("host:connect:192.168.18.248:41953", Assert.Single(connection.Services));
    }

    [Fact]
    public async Task AlreadyConnectedBodyClassifiesAsConnected() {
        using var connection = new FakeConnection {
            Handler = _ => Encoding.UTF8.GetBytes("already connected to 192.168.18.248:41953"),
        };

        var result = await new HostConnectClient(connection).ConnectAsync("192.168.18.248", 41953, TestContext.Current.CancellationToken);

        Assert.Equal(HostConnectOutcome.AlreadyConnected, result.Outcome);
    }

    [Fact]
    public async Task FailureBodyClassifiesFailedWithReason() {
        using var connection = new FakeConnection {
            Handler = _ => Encoding.UTF8.GetBytes("failed to connect to '192.168.18.248:41953'"),
        };

        var result = await new HostConnectClient(connection).ConnectAsync("192.168.18.248", 41953, TestContext.Current.CancellationToken);

        Assert.Equal(HostConnectOutcome.Failed, result.Outcome);
        Assert.Contains("failed to connect", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownHostServiceClassifiesUnsupported() {
        using var connection = new FakeConnection { Handler = _ => throw UnknownService() };

        var result = await new HostConnectClient(connection).ConnectAsync("192.168.18.248", 41953, TestContext.Current.CancellationToken);

        Assert.Equal(HostConnectOutcome.Unsupported, result.Outcome);
    }

    [Fact]
    public async Task Ipv6HostGetsBracketedServiceName() {
        using var connection = new FakeConnection {
            Handler = _ => Encoding.UTF8.GetBytes("connected to [fe80::1]:41953"),
        };

        var result = await new HostConnectClient(connection).ConnectAsync("fe80::1", 41953, TestContext.Current.CancellationToken);

        Assert.Equal(HostConnectOutcome.Connected, result.Outcome);
        Assert.Equal("host:connect:[fe80::1]:41953", Assert.Single(connection.Services));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task InvalidPortFailsWithoutContactingServer(int port) {
        using var connection = new FakeConnection();

        var result = await new HostConnectClient(connection).ConnectAsync("192.168.18.248", port, TestContext.Current.CancellationToken);

        Assert.Equal(HostConnectOutcome.Failed, result.Outcome);
        Assert.Empty(connection.Services);
    }
}

public sealed class PostPairConnectCoordinatorTests {
    // The snapshot projection type lives in AdbView.State; disambiguate explicitly.
    private const AdbView.State.PairingPhase WaitingPhase = AdbView.State.PairingPhase.PairedWaitingForDevice;
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static AppSnapshot WaitingSnapshot() =>
        StateEngine.CreateDefaultSnapshot() with {
            GeneratedAt = T0,
            Pairing = new PairingOperationView {
                OfferId = "offer-1",
                DisplayName = "Galaxy S24",
                Phase = WaitingPhase,
                Title = "Paired with Galaxy S24",
                Detail = "Waiting for ADB connection…",
                CanSubmitCode = false,
                CanCancel = false,
                CanRetry = false,
                CodeLength = 6,
            },
        };

    private static RawMdnsService ConnectRow(string ipv4, int port) => new() {
        InstanceName = "adb-abc123",
        ServiceType = "_adb-tls-connect._tcp",
        Kind = MdnsServiceKind.Connect,
        Ipv4 = ipv4,
        Port = port,
        Format = RawMdnsFormat.Fallback,
    };

    private static DeviceEngineState StateWith(string host, params RawMdnsService[] services) =>
        DeviceEngineState.Empty with {
            ActivePairingHost = host,
            LatestMdnsServices = ImmutableArray.Create(services),
        };

    private sealed class Harness {
        public FakeHostConnectClient Client { get; }
        public PostPairConnectCoordinator Coordinator { get; }

        public Harness(HostConnectResult? result = null) {
            Client = result is null
                ? new FakeHostConnectClient(new HostConnectResult(HostConnectOutcome.Connected, null))
                : new FakeHostConnectClient(result);
            Coordinator = new PostPairConnectCoordinator(Client);
        }
    }

    [Fact]
    public void ConnectsOnceWhenMatchingConnectRowAppears() {
        var harness = new Harness();
        var snapshot = WaitingSnapshot();
        var state = StateWith("192.168.18.248", ConnectRow("192.168.18.248", 41953));

        harness.Coordinator.OnSnapshotPublished(snapshot, state);

        Assert.Equal(1, harness.Client.CallCount);
        Assert.Equal("192.168.18.248", harness.Client.SeenHosts[0]);
        Assert.Equal(41953, harness.Client.SeenPorts[0]);
    }

    [Fact]
    public void ContinuousRowDoesNotRetryWithinEpisode() {
        var harness = new Harness();
        var snapshot = WaitingSnapshot();
        var state = StateWith("192.168.18.248", ConnectRow("192.168.18.248", 41953));

        harness.Coordinator.OnSnapshotPublished(snapshot, state);
        harness.Coordinator.OnSnapshotPublished(snapshot, state);
        harness.Coordinator.OnSnapshotPublished(snapshot, state);

        Assert.Equal(1, harness.Client.CallCount);
    }

    [Fact]
    public void VanishedAndRepresentedRowReArmsOneAttempt() {
        var harness = new Harness();
        var snapshot = WaitingSnapshot();
        var present = StateWith("192.168.18.248", ConnectRow("192.168.18.248", 41953));
        var absent = StateWith("192.168.18.248");

        harness.Coordinator.OnSnapshotPublished(snapshot, present);
        harness.Coordinator.OnSnapshotPublished(snapshot, absent);
        harness.Coordinator.OnSnapshotPublished(snapshot, present);

        Assert.Equal(2, harness.Client.CallCount);
    }

    [Fact]
    public void HostMismatchNeverConnects() {
        var harness = new Harness();
        var state = StateWith("192.168.18.248", ConnectRow("192.168.18.999", 41953));

        harness.Coordinator.OnSnapshotPublished(WaitingSnapshot(), state);

        Assert.Equal(0, harness.Client.CallCount);
    }

    [Fact]
    public void NonWaitingPhaseNeverConnectsAndDisarms() {
        var harness = new Harness();
        var cleared = WaitingSnapshot() with { Pairing = null };

        harness.Coordinator.OnSnapshotPublished(cleared, StateWith("192.168.18.248", ConnectRow("192.168.18.248", 41953)));
        Assert.Equal(0, harness.Client.CallCount);

        // After disarm, a new episode can attempt the same endpoint again.
        harness.Coordinator.OnSnapshotPublished(
            WaitingSnapshot(),
            StateWith("192.168.18.248", ConnectRow("192.168.18.248", 41953)));
        Assert.Equal(1, harness.Client.CallCount);
    }

    [Fact]
    public void FallbackRowsFillWhenAdbSourceEmpty() {
        var harness = new Harness();
        var snapshot = WaitingSnapshot();
        var state = DeviceEngineState.Empty with {
            ActivePairingHost = "192.168.18.248",
            LatestMdnsServices = ImmutableArray<RawMdnsService>.Empty,
            LatestFallbackServices = ImmutableArray.Create(ConnectRow("192.168.18.248", 41953)),
        };

        harness.Coordinator.OnSnapshotPublished(snapshot, state);

        Assert.Equal(1, harness.Client.CallCount);
    }

    [Fact]
    public void FailureResultIsSwallowedAndLoggedNotThrown() {
        var harness = new Harness(new HostConnectResult(HostConnectOutcome.Failed, "failed to connect to 'x'"));

        harness.Coordinator.OnSnapshotPublished(
            WaitingSnapshot(),
            StateWith("192.168.18.248", ConnectRow("192.168.18.248", 41953)));

        Assert.Equal(1, harness.Client.CallCount);
    }
}
