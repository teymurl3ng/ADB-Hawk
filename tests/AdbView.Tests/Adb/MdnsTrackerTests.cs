#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class MdnsTrackerTests {
    [Fact]
    public async Task CliEmptyDocumentIsEmittedWhilePipeRemainsOpen() {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var output = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var input = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, output.ClientSafePipeHandle);
        var cli = new MdnsTrackServicesCli(TimeSpan.FromMilliseconds(10));
        await using var documents = cli.ReadDocumentsAsync(input, stop.Token).GetAsyncEnumerator(stop.Token);
        try {
            await output.WriteAsync(Encoding.UTF8.GetBytes("Services:\n\n"), stop.Token);
            await output.FlushAsync(stop.Token);
            Assert.True(await documents.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stop.Token));
            Assert.Empty(MdnsServicesProtoTextParser.Parse(documents.Current.Span));
        }
        finally {
            await stop.CancelAsync();
        }
    }

    [Fact]
    public async Task BoundedDiagnosticRetentionStillDrainsAllBytes() {
        var bytes = new byte[256 * 1024];
        Array.Fill(bytes, (byte)'x');
        using var input = new System.IO.MemoryStream(bytes);
        var retained = await MdnsTrackServicesCli.DrainBoundedAsync(input, 64 * 1024, TestContext.Current.CancellationToken);
        Assert.Equal(64 * 1024, retained.Length);
        Assert.Equal(input.Length, input.Position);
    }
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

        public bool IsEmpty => Count == 0;

        public T this[int index] {
            get {
                lock (_gate) {
                    return _items[index];
                }
            }
        }

        public List<T> Snapshot() {
            lock (_gate) {
                return new List<T>(_items);
            }
        }
    }

    [Fact]
    public void A_ZeroServices_EmptySnapshot() {
        var services = MdnsServicesProtoParser.Parse(FakeAdbMdns.EncodeMdnsServices());
        Assert.Empty(services);
    }

    [Fact]
    public void B_OnePairingService_Parses() {
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(
            pair: [FakeAdbMdns.EncodePairing("phone-1", 37123, ipv4: "192.168.1.10", givenName: "Pixel")]);
        var services = MdnsServicesProtoParser.Parse(payload);
        Assert.Single(services);
        Assert.Equal(MdnsServiceKind.Pairing, services[0].Kind);
        Assert.Equal(MdnsServiceTypes.Pairing, services[0].ServiceType);
        Assert.Equal("phone-1", services[0].InstanceName);
        Assert.Equal(37123, services[0].Port);
        Assert.Equal("192.168.1.10", services[0].Ipv4);
        Assert.Equal("Pixel", services[0].GivenName);
    }

    [Fact]
    public void C_OneConnectService_Parses() {
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(
            tls: [FakeAdbMdns.EncodeConnect("phone-2", 5555, ipv4: "10.0.0.2", knownDevice: true, serial: "SERIAL1")]);
        var services = MdnsServicesProtoParser.Parse(payload);
        Assert.Single(services);
        Assert.Equal(MdnsServiceKind.Connect, services[0].Kind);
        Assert.Equal("SERIAL1", services[0].Serial);
        Assert.True(services[0].KnownDevice);
    }

    [Fact]
    public void D_LegacyService_Classified() {
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("adb-LEGACY", 5555, ipv4: "192.168.0.5")]);
        var services = MdnsServicesProtoParser.Parse(payload);
        Assert.Equal(MdnsServiceKind.Legacy, services[0].Kind);
        Assert.Equal(MdnsServiceTypes.Legacy, services[0].ServiceType);
    }

    [Fact]
    public void E_UnknownService_Safe() {
        byte[] inner = FakeAdbMdns.EncodeMdnsService("x", "_adb-future._tcp", 1, ipv4: "1.2.3.4");
        // Place under tcp bucket but type string is unknown — classify by type string.
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(tcp: [FakeAdbMdns.EncodeWrappedService(inner)]);
        var services = MdnsServicesProtoParser.Parse(payload);
        Assert.Single(services);
        Assert.Equal(MdnsServiceKind.Unknown, services[0].Kind);
    }

    [Fact]
    public void F_DuplicateAnnouncement_Deduped() {
        var one = FakeAdbMdns.EncodePairing("same", 1111, ipv4: "1.1.1.1");
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(pair: [one, one]);
        var services = MdnsServicesProtoParser.Parse(payload);
        Assert.Single(services);
    }

    [Fact]
    public void G_MultipleServices_Preserved() {
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("a", 5555, ipv4: "1.1.1.1")],
            tls: [FakeAdbMdns.EncodeConnect("b", 5555, ipv4: "1.1.1.2")],
            pair: [FakeAdbMdns.EncodePairing("c", 37000, ipv4: "1.1.1.3")]);
        var services = MdnsServicesProtoParser.Parse(payload);
        Assert.Equal(3, services.Length);
        Assert.Contains(services, s => s.Kind == MdnsServiceKind.Legacy);
        Assert.Contains(services, s => s.Kind == MdnsServiceKind.Connect);
        Assert.Contains(services, s => s.Kind == MdnsServiceKind.Pairing);
    }

    [Fact]
    public void H_Ipv4_Parsed() {
        var services = MdnsServicesProtoParser.Parse(FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("i", 5555, ipv4: "192.168.18.248")]));
        Assert.Equal("192.168.18.248", services[0].Ipv4);
    }

    [Fact]
    public void I_Ipv6_ParsedWithoutLossyConcatenation() {
        string v6 = "2400:adc5:489:4400:d40e:56ff:fe0d:e2b4";
        var services = MdnsServicesProtoParser.Parse(FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("i", 5555, ipv6: [v6])]));
        Assert.Single(services[0].Ipv6);
        Assert.Equal(v6, services[0].Ipv6[0]);
        Assert.Equal(5555, services[0].Port);
        Assert.DoesNotContain(v6 + ":", services[0].Ipv6[0], StringComparison.Ordinal);

        Assert.True(MdnsServicesListParser.TrySplitHostPort($"[{v6}]:5555", out string? host, out int port));
        Assert.Equal(v6, host);
        Assert.Equal(5555, port);
        Assert.False(MdnsServicesListParser.TrySplitHostPort($"{v6}:5555", out _, out _));
    }

    [Fact]
    public void J_GivenName_Parsed() {
        var services = MdnsServicesProtoParser.Parse(FakeAdbMdns.EncodeMdnsServices(
            pair: [FakeAdbMdns.EncodePairing("p", 1, givenName: "Kitchen Phone")]));
        Assert.Equal("Kitchen Phone", services[0].GivenName);
    }

    [Fact]
    public void K_SerialAndProductModel_Parsed() {
        byte[] svc = FakeAdbMdns.EncodeWrappedService(FakeAdbMdns.EncodeMdnsService(
            "inst",
            MdnsServiceTypes.Connect,
            5555,
            productModel: "SM-A065F",
            serial: "R8VXA02SNKH"));
        var services = MdnsServicesProtoParser.Parse(FakeAdbMdns.EncodeMdnsServices(tls: [svc]));
        Assert.Equal("SM-A065F", services[0].ProductModel);
        Assert.Equal("R8VXA02SNKH", services[0].Serial);
    }

    [Fact]
    public void L_OptionalFieldsAbsent_Safe() {
        var services = MdnsServicesProtoParser.Parse(FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("bare", 5555)]));
        Assert.Null(services[0].Ipv4);
        Assert.Empty(services[0].Ipv6);
        Assert.Null(services[0].GivenName);
        Assert.Null(services[0].Serial);
        Assert.Null(services[0].ProductModel);
        Assert.Null(services[0].Hostname);
    }

    [Fact]
    public async Task M_Withdrawal_CompleteListAbsence() {
        await using var server = new FakeAdbServer();
        var lists = new Collector<MdnsServicesReceived>();
        using var holdCts = new CancellationTokenSource();

        byte[] first = FakeAdbMdns.EncodeMdnsServices(
            pair: [FakeAdbMdns.EncodePairing("offer", 37000, ipv4: "1.1.1.1")]);
        byte[] second = FakeAdbMdns.EncodeMdnsServices();

        server.SetHandler(async (socket, cmd) => {
            Assert.Equal(MdnsTracker.PreferredService, cmd);
            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbMdns.SendFrameAsync(socket, first).ConfigureAwait(false);
            await FakeAdbMdns.SendFrameAsync(socket, second).ConfigureAwait(false);
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = CreateTracker(server);
        tracker.ServicesReceived += lists.Add;
        var run = tracker.RunAsync(1, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 2, TimeSpan.FromSeconds(5));
        Assert.Single(lists[0].Services);
        Assert.Empty(lists[1].Services);
        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(run);
    }

    [Fact]
    public async Task N_MalformedPayload_Dropped_StreamContinues() {
        await using var server = new FakeAdbServer();
        var lists = new Collector<MdnsServicesReceived>();
        var malformed = new Collector<string>();
        using var holdCts = new CancellationTokenSource();
        byte[] bad = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        byte[] good = FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("ok", 5555, ipv4: "1.2.3.4")]);

        server.SetHandler(async (socket, cmd) => {
            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbMdns.SendFrameAsync(socket, bad).ConfigureAwait(false);
            await FakeAdbMdns.SendFrameAsync(socket, good).ConfigureAwait(false);
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = CreateTracker(server, onMalformed: malformed.Add);
        tracker.ServicesReceived += lists.Add;
        var run = tracker.RunAsync(1, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 1, TimeSpan.FromSeconds(5));
        Assert.Equal("ok", lists[0].Services[0].InstanceName);
        Assert.False(malformed.IsEmpty);
        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(run);
    }

    [Fact]
    public async Task O_UnrecoverableFraming_Reconnects() {
        await using var server = new FakeAdbServer();
        var failures = new Collector<MdnsTrackerFailed>();
        var delays = new List<TimeSpan>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var framed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            await socket.SendAsync("ZZZZ"u8.ToArray(), SocketFlags.None).ConfigureAwait(false);
            framed.TrySetResult();
        });

        using var cts = new CancellationTokenSource();
        var tracker = CreateTracker(server, delay: async (d, ct) => {
            delays.Add(d);
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        });
        tracker.Failed += failures.Add;
        var run = tracker.RunAsync(1, cts.Token);
        await framed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !failures.IsEmpty && delays.Count > 0, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        gate.TrySetResult();
        await AssertCancelledOrCompleted(run);
    }

    [Fact]
    public async Task P_Eof_Reconnects() {
        await using var server = new FakeAdbServer();
        var lists = new Collector<MdnsServicesReceived>();
        var failures = new Collector<MdnsTrackerFailed>();
        var delays = new List<TimeSpan>();
        int connections = 0;
        using var holdCts = new CancellationTokenSource();
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("x", 5555, ipv4: "1.1.1.1")]);

        server.SetHandler(async (socket, cmd) => {
            int n = Interlocked.Increment(ref connections);
            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbMdns.SendFrameAsync(socket, payload).ConfigureAwait(false);
            if (n == 1) {
                socket.Shutdown(SocketShutdown.Both);
                return;
            }

            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = CreateTracker(server, delay: (d, _) => {
            delays.Add(d);
            return Task.CompletedTask;
        });
        tracker.ServicesReceived += lists.Add;
        tracker.Failed += failures.Add;
        var run = tracker.RunAsync(1, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 2, TimeSpan.FromSeconds(5));
        Assert.False(failures.IsEmpty);
        Assert.Equal(TimeSpan.FromSeconds(1), delays[0]);
        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(run);
    }

    [Fact]
    public void Q_BackoffProgression_Exact() {
        var backoff = new MdnsTrackerBackoff(() => 0);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(15), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.NextDelay());
    }

    [Fact]
    public async Task R_BackoffResetsAfterRecovery() {
        await using var server = new FakeAdbServer();
        var delays = new List<TimeSpan>();
        var lists = new Collector<MdnsServicesReceived>();
        int connections = 0;
        using var holdCts = new CancellationTokenSource();
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("x", 5555, ipv4: "1.1.1.1")]);

        server.SetHandler(async (socket, cmd) => {
            int n = Interlocked.Increment(ref connections);
            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbMdns.SendFrameAsync(socket, payload).ConfigureAwait(false);
            if (n < 3) {
                socket.Shutdown(SocketShutdown.Both);
                return;
            }

            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = CreateTracker(server, delay: (d, _) => {
            delays.Add(d);
            return Task.CompletedTask;
        });
        tracker.ServicesReceived += lists.Add;
        var run = tracker.RunAsync(1, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 3, TimeSpan.FromSeconds(5));
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(1), d));
        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(run);
    }

    [Fact]
    public async Task S_UnsupportedPreferred_ActivatesCliFallback() {
        await using var server = new FakeAdbServer();
        var degraded = new Collector<string>();
        var lists = new Collector<MdnsServicesReceived>();
        var cli = new FakeMdnsTrackServicesCli(
            Encoding.UTF8.GetBytes(
                """
                Services:
                pair {
                  service {
                    instance: "from-cli"
                    service: "_adb-tls-pairing._tcp"
                    ipv4: "10.0.0.9"
                    port: 37111
                  }
                }
                """));

        string reason = $"unknown host service '{MdnsTracker.PreferredService}'";
        server.SetHandler(async (socket, cmd) => {
            Assert.Equal(MdnsTracker.PreferredService, cmd);
            await FakeAdbMdns.SendFailAsync(socket, reason).ConfigureAwait(false);
        });

        using var cts = new CancellationTokenSource();
        var tracker = CreateTracker(server, cli: cli, locator: new FakeAdbExecutableLocator(), onDegraded: degraded.Add);
        tracker.ServicesReceived += lists.Add;
        var run = tracker.RunAsync(3, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 1, TimeSpan.FromSeconds(5));
        Assert.Single(degraded.Snapshot());
        Assert.Equal("from-cli", lists[0].Services[0].InstanceName);
        Assert.Equal(RawMdnsFormat.ProtoText, lists[0].Services[0].Format);
        Assert.NotEmpty(cli.ExecutablePaths.ToArray());
        await cts.CancelAsync();
        await AssertCancelledOrCompleted(run);
    }

    [Fact]
    public async Task T_GenericFail_DoesNotPermanentlyDowngrade() {
        await using var server = new FakeAdbServer();
        var degraded = new Collector<string>();
        var lists = new Collector<MdnsServicesReceived>();
        var failures = new Collector<MdnsTrackerFailed>();
        int preferred = 0;
        using var holdCts = new CancellationTokenSource();
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("back", 5555, ipv4: "1.1.1.1")]);

        server.SetHandler(async (socket, cmd) => {
            Assert.Equal(MdnsTracker.PreferredService, cmd);
            int n = Interlocked.Increment(ref preferred);
            if (n == 1) {
                await FakeAdbMdns.SendFailAsync(socket, "device offline").ConfigureAwait(false);
                return;
            }

            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbMdns.SendFrameAsync(socket, payload).ConfigureAwait(false);
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = CreateTracker(server, onDegraded: degraded.Add);
        tracker.ServicesReceived += lists.Add;
        tracker.Failed += failures.Add;
        var run = tracker.RunAsync(1, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 1 && preferred >= 2, TimeSpan.FromSeconds(5));
        Assert.Empty(degraded.Snapshot());
        Assert.Equal(RawMdnsFormat.ProtoBinary, lists[0].Services[0].Format);
        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(run);
    }

    [Fact]
    public async Task U_NewGeneration_RetriesPreferred() {
        await using var server = new FakeAdbServer();
        var lists = new Collector<MdnsServicesReceived>();
        int preferredFails = 0;
        int preferredOk = 0;
        using var holdCts = new CancellationTokenSource();
        string reason = $"unknown host service '{MdnsTracker.PreferredService}'";
        byte[] payload = FakeAdbMdns.EncodeMdnsServices(
            tcp: [FakeAdbMdns.EncodeLegacy("g2", 5555, ipv4: "2.2.2.2")]);

        var cli = new FakeMdnsTrackServicesCli(
            Encoding.UTF8.GetBytes(
                """
                Services:
                tcp {
                  service {
                    instance: "cli-only"
                    service: "_adb._tcp"
                    port: 5555
                    ipv4: "9.9.9.9"
                  }
                }
                """));

        server.SetHandler(async (socket, cmd) => {
            if (cmd != MdnsTracker.PreferredService) {
                return;
            }

            // Generation 1: unsupported. Generation 2: OK.
            if (preferredOk == 0 && preferredFails == 0) {
                Interlocked.Increment(ref preferredFails);
                await FakeAdbMdns.SendFailAsync(socket, reason).ConfigureAwait(false);
                return;
            }

            Interlocked.Increment(ref preferredOk);
            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbMdns.SendFrameAsync(socket, payload).ConfigureAwait(false);
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts1 = new CancellationTokenSource();
        var tracker = CreateTracker(server, cli: cli, locator: new FakeAdbExecutableLocator());
        tracker.ServicesReceived += lists.Add;
        var run1 = tracker.RunAsync(1, cts1.Token);
        await WaitUntilAsync(() => lists.Count >= 1, TimeSpan.FromSeconds(5));
        Assert.Equal("cli-only", lists[0].Services[0].InstanceName);
        await cts1.CancelAsync();
        await AssertCancelledOrCompleted(run1);

        using var cts2 = new CancellationTokenSource();
        var run2 = tracker.RunAsync(2, cts2.Token);
        await WaitUntilAsync(
            () => lists.Snapshot().Any(l => l.Services.Length > 0 && l.Services[0].InstanceName == "g2"),
            TimeSpan.FromSeconds(5));
        var protoList = lists.Snapshot().Last(l => l.Services.Length > 0 && l.Services[0].InstanceName == "g2");
        Assert.Equal(RawMdnsFormat.ProtoBinary, protoList.Services[0].Format);
        await cts2.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(run2);
    }

    [Fact]
    public async Task V_Cancellation_StopsCleanly() {
        await using var server = new FakeAdbServer();
        using var holdCts = new CancellationTokenSource();
        server.SetHandler(async (socket, cmd) => {
            await FakeAdbMdns.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbMdns.SendFrameAsync(socket, FakeAdbMdns.EncodeMdnsServices()).ConfigureAwait(false);
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = CreateTracker(server);
        var run = tracker.RunAsync(1, cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(run);
    }

    [Fact]
    public void W_MdnsTrackerSource_HasNoStateOrUiDependency() {
        string text = System.IO.File.ReadAllText(
            System.IO.Path.Combine(FindRepoRoot(), "src", "AdbView.Core", "Adb", "MdnsTracker.cs"));
        foreach (var token in new[] {
            "AdbView.State", "StateEngine", "PairingOfferView",
            "PairingOperationView", "BridgeCoordinator", "System.Windows.Forms",
            "Microsoft.Web.WebView2", "flyout",
        }) {
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProtoText_ParsesLiveShapedDocument() {
        string text =
            """
            Services:
            tcp {
              service {
                instance: "adb-R8VXA02SNKH"
                service: "_adb._tcp"
                ipv4: "192.168.18.248"
                ipv6: "2400:adc5:489:4400:d40e:56ff:fe0d:e2b4"
                port: 5555
                product_model: "SM-A065F"
                hostname: "Android-5.local"
              }
            }
            """;
        var services = MdnsServicesProtoTextParser.Parse(text);
        Assert.Single(services);
        Assert.Equal(MdnsServiceKind.Legacy, services[0].Kind);
        Assert.Equal("192.168.18.248", services[0].Ipv4);
        Assert.Equal("2400:adc5:489:4400:d40e:56ff:fe0d:e2b4", services[0].Ipv6[0]);
        Assert.Equal("SM-A065F", services[0].ProductModel);
    }

    [Fact]
    public void ServicesList_ParsesOneShotListing() {
        var services = MdnsServicesListParser.Parse("adb-R8VXA02SNKH\t_adb._tcp\t192.168.18.248:5555\n");
        Assert.Single(services);
        Assert.Equal(MdnsServiceKind.Legacy, services[0].Kind);
        Assert.Equal("192.168.18.248", services[0].Ipv4);
        Assert.Equal(5555, services[0].Port);
        Assert.Equal(RawMdnsFormat.ServicesList, services[0].Format);
    }

    [Fact]
    public async Task QueryServicesOnce_UsesOneShotHostService() {
        await using var server = new FakeAdbServer();
        server.RespondOkayOneShot("inst\t_adb-tls-pairing._tcp\t10.0.0.5:37100\n");
        var tracker = CreateTracker(server);
        var services = await tracker.QueryServicesOnceAsync();
        Assert.Single(services);
        Assert.Equal(MdnsServiceKind.Pairing, services[0].Kind);
        Assert.Contains(server.ReceivedCommands, c => c == MdnsTracker.OneShotService);
    }

    [Fact]
    public void DiscoveryKey_UsesServiceTypeAndInstance() {
        var a = new RawMdnsService {
            InstanceName = "phone",
            ServiceType = MdnsServiceTypes.Pairing,
            Kind = MdnsServiceKind.Pairing,
            Port = 1,
            Format = RawMdnsFormat.ProtoBinary,
        };
        var b = a with { ServiceType = MdnsServiceTypes.Connect, Kind = MdnsServiceKind.Connect };
        Assert.NotEqual(a.DiscoveryKey, b.DiscoveryKey);
    }

    private static MdnsTracker CreateTracker(
        FakeAdbServer server,
        IMdnsTrackServicesCli? cli = null,
        IAdbExecutableLocator? locator = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? onDegraded = null,
        Action<string>? onMalformed = null) =>
        new(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            backoff: new MdnsTrackerBackoff(() => 0),
            getUtcNow: () => DateTimeOffset.UnixEpoch,
            delay: delay ?? ((_, _) => Task.CompletedTask),
            executableLocator: locator ?? new FakeAdbExecutableLocator(),
            cli: cli ?? new FakeMdnsTrackServicesCli(),
            onDegraded: onDegraded,
            onMalformedPayload: onMalformed);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout) {
        var start = DateTime.UtcNow;
        while (!predicate()) {
            if (DateTime.UtcNow - start > timeout) {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task AssertCancelledOrCompleted(Task runTask) {
        try {
            await runTask;
        }
        catch (OperationCanceledException) {
        }
    }

    private static string FindRepoRoot() {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ADB View.slnx"))
                || System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "AGENTS.md"))) {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}
