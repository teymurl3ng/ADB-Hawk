#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class PairingCoordinatorTests {
    private sealed class Collector<T> {
        private readonly object _gate = new();
        private readonly List<T> _items = new();

        public void Add(T item) {
            lock (_gate) {
                _items.Add(item);
            }
        }

        public int Count {
            get {
                lock (_gate) {
                    return _items.Count;
                }
            }
        }

        public List<T> Snapshot() {
            lock (_gate) {
                return new List<T>(_items);
            }
        }
    }

    private static PairingEndpoint Ep(string host = "192.168.1.10", int port = 37100) {
        Assert.True(PairingEndpoint.TryCreate(host, port, out var ep));
        return ep!;
    }

    private static RawMdnsService PairingService(
        string instance = "phone",
        string? ipv4 = "192.168.1.10",
        IEnumerable<string>? ipv6 = null,
        int port = 37100) =>
        new() {
            InstanceName = instance,
            ServiceType = MdnsServiceTypes.Pairing,
            Kind = MdnsServiceKind.Pairing,
            Ipv4 = ipv4,
            Ipv6 = ipv6 is null
                ? System.Collections.Immutable.ImmutableArray<string>.Empty
                : System.Collections.Immutable.ImmutableArray.CreateRange(ipv6),
            Port = port,
            Format = RawMdnsFormat.ProtoBinary,
        };

    [Theory]
    [InlineData("000001")]
    [InlineData("123456")]
    [InlineData("000000")]
    [InlineData("999999")]
    public void A_B_ValidCodes(string code) => Assert.True(PairingCode.IsValid(code));

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData(" 123456")]
    [InlineData("123456 ")]
    [InlineData("12 456")]
    [InlineData("")]
    [InlineData(null)]
    public void C_D_E_F_InvalidAsciiCodes(string? code) => Assert.False(PairingCode.IsValid(code));

    [Fact]
    public void G_UnicodeDigitsRejected() {
        // Fullwidth digits １２３４５６
        string fullwidth = "\uFF11\uFF12\uFF13\uFF14\uFF15\uFF16";
        Assert.False(PairingCode.IsValid(fullwidth));
    }

    [Fact]
    public async Task H_InvalidCode_ZeroAdbCalls() {
        var pair = new FakeHostPairClient(new HostPairResult(true, PairingFailureKind.None, null));
        var coord = new PairingCoordinator(pairClient: pair);
        Assert.True(coord.TryStart("offer-1", Ep()));
        await coord.SubmitAsync("offer-1", "12345");
        Assert.Equal(0, pair.CallCount);
    }

    [Fact]
    public void I_Ipv4_EndpointFormatted() {
        Assert.True(PairingEndpoint.TryCreate("10.0.0.5", 37123, out var ep));
        Assert.Equal("10.0.0.5:37123", ep!.WireEndpoint);
        Assert.Equal("host:pair:123456:10.0.0.5:37123", HostPairClient.BuildServiceName("123456", ep));
    }

    [Fact]
    public void J_Ipv6_EndpointFormatted() {
        string v6 = "2400:adc5:489:4400:d40e:56ff:fe0d:e2b4";
        Assert.True(PairingEndpoint.TryCreate(v6, 37123, out var ep));
        Assert.Equal($"[{v6}]:37123", ep!.WireEndpoint);
        string svc = HostPairClient.BuildServiceName("000001", ep);
        Assert.Equal($"host:pair:000001:[{v6}]:37123", svc);
        // First-colon split after pair: yields password then remainder with brackets intact.
        string afterPair = svc["host:pair:".Length..];
        int colon = afterPair.IndexOf(':', StringComparison.Ordinal);
        Assert.Equal("000001", afterPair[..colon]);
        Assert.Equal($"[{v6}]:37123", afterPair[(colon + 1)..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void K_InvalidPortRejected(int port) {
        Assert.False(PairingEndpoint.TryCreate("1.2.3.4", port, out _));
    }

    [Fact]
    public void L_NonPairingServiceCannotSubmit() {
        var connect = new RawMdnsService {
            InstanceName = "x",
            ServiceType = MdnsServiceTypes.Connect,
            Kind = MdnsServiceKind.Connect,
            Ipv4 = "1.2.3.4",
            Port = 5555,
            Format = RawMdnsFormat.ProtoBinary,
        };
        Assert.False(PairingEndpoint.TryFromPairingService(connect, out _));

        var legacy = new RawMdnsService {
            InstanceName = "y",
            ServiceType = MdnsServiceTypes.Legacy,
            Kind = MdnsServiceKind.Legacy,
            Ipv4 = "1.2.3.4",
            Port = 5555,
            Format = RawMdnsFormat.ProtoBinary,
        };
        Assert.False(PairingEndpoint.TryFromPairingService(legacy, out _));
    }

    [Fact]
    public void M_PairingPortUsedNotConnectPort() {
        var pairing = PairingService(port: 37111);
        var connectPort = 5555;
        Assert.True(PairingEndpoint.TryFromPairingService(pairing, out var ep));
        Assert.Equal(37111, ep!.Port);
        Assert.NotEqual(connectPort, ep.Port);
    }

    [Fact]
    public async Task N_HostPairOk_PairedWaitingForDevice() {
        var events = new Collector<PairingProgressed>();
        var pair = new FakeHostPairClient(new HostPairResult(true, PairingFailureKind.None, null));
        var coord = new PairingCoordinator(pairClient: pair);
        coord.Progressed += events.Add;
        Assert.True(coord.TryStart("o1", Ep()));
        await coord.SubmitAsync("o1", "123456");
        Assert.Equal(PairingPhase.PairedWaitingForDevice, coord.CurrentPhase);
        Assert.Contains(events.Snapshot(), e => e.Phase == PairingPhase.PairedWaitingForDevice);
        Assert.Equal(1, pair.CallCount);
    }

    [Fact]
    public async Task O_HostPairFail_Failed() {
        var pair = new FakeHostPairClient(
            new HostPairResult(false, PairingFailureKind.Rejected, "Failed: Wrong password or connection was dropped."));
        var coord = new PairingCoordinator(pairClient: pair);
        Assert.True(coord.TryStart("o1", Ep()));
        await coord.SubmitAsync("o1", "654321");
        Assert.Equal(PairingPhase.Failed, coord.CurrentPhase);
    }

    [Fact]
    public async Task P_MalformedResponse_Failed() {
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            Assert.True(FakeAdbPair.TryParsePairCommand(cmd, out _));
            await FakeAdbPair.SendServiceAcceptedAsync(socket).ConfigureAwait(false);
            // Hex length claiming 4 bytes but send garbage that is not Successful*
            await socket.SendAsync("0004NOPE"u8.ToArray(), SocketFlags.None).ConfigureAwait(false);
        });

        var client = new HostPairClient(server.EndPoint, requestTimeout: TimeSpan.FromSeconds(2));
        var result = await client.PairAsync(Ep(), "123456", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(PairingFailureKind.MalformedResponse, result.FailureKind);
    }

    [Fact]
    public async Task Q_Timeout_Failed() {
        var pair = new FakeHostPairClient(async (_, _, ct) => {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new HostPairResult(true, PairingFailureKind.None, null);
        });
        var coord = new PairingCoordinator(pairClient: pair, pairTimeout: TimeSpan.FromMilliseconds(30));
        Assert.True(coord.TryStart("o1", Ep()));
        await coord.SubmitAsync("o1", "123456");
        Assert.Equal(PairingPhase.Failed, coord.CurrentPhase);
    }

    [Fact]
    public async Task R_Cancellation_Cleared() {
        var events = new Collector<PairingProgressed>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = new FakeHostPairClient(async (_, _, ct) => {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new HostPairResult(true, PairingFailureKind.None, null);
        });
        var coord = new PairingCoordinator(pairClient: pair, pairTimeout: TimeSpan.FromSeconds(30));
        coord.Progressed += events.Add;
        Assert.True(coord.TryStart("o1", Ep()));
        var submit = coord.SubmitAsync("o1", "123456");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coord.Cancel("o1");
        await submit;
        Assert.Equal(PairingPhase.Cleared, coord.CurrentPhase);
        Assert.Contains(events.Snapshot(), e => e.Phase == PairingPhase.Cleared && e.FailureKind == PairingFailureKind.Cancelled);
    }

    [Fact]
    public async Task S_CancelPlusLateSuccess_DoesNotResurrect() {
        var release = new TaskCompletionSource<HostPairResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = new FakeHostPairClient(async (_, _, ct) => {
            entered.TrySetResult();
            using var reg = ct.Register(() => { /* observe cancel */ });
            return await release.Task.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        });
        var coord = new PairingCoordinator(pairClient: pair, pairTimeout: TimeSpan.FromSeconds(60));
        Assert.True(coord.TryStart("o1", Ep()));
        var submit = coord.SubmitAsync("o1", "123456");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coord.Cancel("o1");
        release.TrySetResult(new HostPairResult(true, PairingFailureKind.None, null));
        await submit;
        Assert.Equal(PairingPhase.Cleared, coord.CurrentPhase);
        Assert.Null(coord.ActiveOfferId);
    }

    [Fact]
    public async Task T_DuplicateConcurrentSubmit_SendsOnce() {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pair = new FakeHostPairClient(async (_, _, ct) => {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return new HostPairResult(true, PairingFailureKind.None, null);
        });
        var coord = new PairingCoordinator(pairClient: pair);
        Assert.True(coord.TryStart("o1", Ep()));
        var a = coord.SubmitAsync("o1", "123456");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var b = coord.SubmitAsync("o1", "123456");
        await b;
        Assert.Equal(1, pair.CallCount);
        release.TrySetResult();
        await a;
    }

    [Fact]
    public async Task U_PairSuccess_CreatesNoDeviceView() {
        var pair = new FakeHostPairClient(new HostPairResult(true, PairingFailureKind.None, null));
        var coord = new PairingCoordinator(pairClient: pair);
        Assert.True(coord.TryStart("o1", Ep()));
        await coord.SubmitAsync("o1", "123456");
        Assert.Equal(PairingPhase.PairedWaitingForDevice, coord.CurrentPhase);
        // Domain isolation: coordinator type has no DeviceView API; phase is waiting, not connected.
        Assert.NotEqual("Connected", coord.CurrentPhase.ToString());
    }

    [Fact]
    public async Task V_PairSuccess_NoAdbConnect() {
        await using var server = new FakeAdbServer();
        var commands = new List<string>();
        server.SetHandler(async (socket, cmd) => {
            commands.Add(cmd);
            Assert.StartsWith("host:pair:", cmd, StringComparison.Ordinal);
            Assert.DoesNotContain("host:connect", cmd, StringComparison.Ordinal);
            await FakeAdbPair.SendServiceAcceptedAsync(socket).ConfigureAwait(false);
            await FakeAdbPair.SendSuccessResultAsync(socket, "Successfully paired to 192.168.1.10 [guid=abc]").ConfigureAwait(false);
        });

        var client = new HostPairClient(server.EndPoint, requestTimeout: TimeSpan.FromSeconds(3));
        var result = await client.PairAsync(Ep(), "123456", CancellationToken.None);
        Assert.True(result.Success);
        Assert.All(commands, c => Assert.DoesNotContain("connect", c, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.ReceivedCommands, c => c.StartsWith("host:connect", StringComparison.Ordinal));
    }

    [Fact]
    public async Task W_CodeNeverInLogsOrProgress() {
        const string secret = "654321";
        var events = new Collector<PairingProgressed>();
        var diag = new CapturingDiagnostics();
        var pair = new FakeHostPairClient((ep, code, _) => {
            diag.Write(HostPairClient.DiagnosticServiceName(ep));
            diag.Write(HostPairClient.SanitizeReason("Failed: Wrong password"));
            Assert.Equal(secret, code); // in-memory only
            return Task.FromResult(new HostPairResult(false, PairingFailureKind.Rejected, "Failed: Wrong password"));
        });
        var coord = new PairingCoordinator(pairClient: pair);
        coord.Progressed += e => {
            events.Add(e);
            diag.Write($"{e.OfferId}|{e.Phase}|{e.FailureReason}|{e.FailureKind}");
        };

        Assert.True(coord.TryStart("o1", Ep()));
        await coord.SubmitAsync("o1", secret);

        string blob = diag.Join();
        Assert.DoesNotContain(secret, blob, StringComparison.Ordinal);
        Assert.All(events.Snapshot(), e => {
            Assert.DoesNotContain(secret, e.OfferId, StringComparison.Ordinal);
            if (e.FailureReason is not null) {
                Assert.DoesNotContain(secret, e.FailureReason, StringComparison.Ordinal);
            }
        });
        Assert.DoesNotContain(secret, HostPairClient.DiagnosticServiceName(Ep()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task X_RateLimit_MaxFivePerOfferPerMinute() {
        var clock = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var pair = new FakeHostPairClient(new HostPairResult(false, PairingFailureKind.Rejected, "no"));
        var coord = new PairingCoordinator(pairClient: pair, getUtcNow: () => clock);
        Assert.True(coord.TryStart("o1", Ep()));

        for (int i = 0; i < 5; i++) {
            await coord.SubmitAsync("o1", "123456");
            Assert.True(coord.TryRetry("o1", Ep()));
        }

        Assert.Equal(5, pair.CallCount);
        await coord.SubmitAsync("o1", "123456");
        Assert.Equal(5, pair.CallCount);
        Assert.Equal(PairingPhase.Failed, coord.CurrentPhase);
    }

    [Fact]
    public async Task Y_RateLimit_IsolatedPerOffer() {
        var clock = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var pair = new FakeHostPairClient(new HostPairResult(false, PairingFailureKind.Rejected, "no"));
        var coord = new PairingCoordinator(pairClient: pair, getUtcNow: () => clock);

        Assert.True(coord.TryStart("a", Ep()));
        for (int i = 0; i < 5; i++) {
            await coord.SubmitAsync("a", "123456");
            coord.Cancel("a");
            Assert.True(coord.TryStart("a", Ep()));
        }

        await coord.SubmitAsync("a", "123456");
        Assert.Equal(5, pair.CallCount); // 6th blocked for offer a

        coord.Cancel("a");
        Assert.True(coord.TryStart("b", Ep("10.0.0.2", 37101)));
        await coord.SubmitAsync("b", "123456");
        Assert.Equal(6, pair.CallCount); // offer b still allowed
    }

    [Fact]
    public void Z_CoordinatorSource_NoStateOrUiDependency() {
        string text = System.IO.File.ReadAllText(
            System.IO.Path.Combine(FindRepoRoot(), "src", "AdbView", "Adb", "PairingCoordinator.cs"));
        foreach (var token in new[] {
            "AdbView.State", "StateEngine", "AppSnapshot", "PairingOfferView",
            "PairingOperationView", "DeviceView", "BridgeCoordinator",
            "System.Windows.Forms", "Microsoft.Web.WebView2",
        }) {
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task HostPairClient_RejectsWrongPasswordViaFakeServer() {
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            Assert.True(FakeAdbPair.TryParsePairCommand(cmd, out var req));
            Assert.Equal(6, req!.CodeLength);
            Assert.Equal("192.168.1.10:37100", req.WireEndpoint);
            await FakeAdbPair.SendServiceAcceptedAsync(socket).ConfigureAwait(false);
            await FakeAdbPair.SendFailResultAsync(socket, "Failed: Wrong password or connection was dropped.").ConfigureAwait(false);
        });

        var client = new HostPairClient(server.EndPoint, requestTimeout: TimeSpan.FromSeconds(3));
        var result = await client.PairAsync(Ep(), "654321", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(PairingFailureKind.Rejected, result.FailureKind);
        Assert.DoesNotContain("654321", result.DiagnosticReason ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedHostPair_UsesCliFallback() {
        var host = new FakeHostPairClient((_, _, _) =>
            throw new AdbProtocolException(
                AdbErrorCode.CommandFailed,
                "failed",
                serverReason: "unknown host service 'host:pair:x'"));
        var cli = new FakePairCliFallback(new HostPairResult(true, PairingFailureKind.None, null));
        var coord = new PairingCoordinator(
            pairClient: host,
            cliFallback: cli,
            executableLocator: new FakeAdbExecutableLocator());
        Assert.True(coord.TryStart("o1", Ep()));
        await coord.SubmitAsync("o1", "123456");
        Assert.Equal(1, cli.CallCount);
        Assert.Equal(PairingPhase.PairedWaitingForDevice, coord.CurrentPhase);
        Assert.All(cli.ArgvJoined, a => Assert.DoesNotContain("123456", a, StringComparison.Ordinal));
    }

    [Fact]
    public void LeadingZerosPreservedInServiceName() {
        var ep = Ep();
        string svc = HostPairClient.BuildServiceName("000001", ep);
        Assert.Contains(":000001:", svc, StringComparison.Ordinal);
        Assert.DoesNotContain(":1:", svc, StringComparison.Ordinal);
    }

    private static string FindRepoRoot() {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "AGENTS.md"))) {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
