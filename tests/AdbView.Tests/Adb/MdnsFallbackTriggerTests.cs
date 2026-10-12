#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.Host;
using Xunit;

/// <summary>
/// Decision 0016 / research 2026-10-03: a QR-session start must produce an immediate fallback
/// query burst rather than waiting out the 60 s empty-set requery floor (the phone advertises
/// only while its pairing dialog is open). Verified deterministically with a recording socket.
/// </summary>
public sealed class MdnsFallbackTriggerTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class RecordingSocket : IUdpSocket {
        public List<IPEndPoint> Sends { get; } = new();

        public bool IsBound { get; private set; }

        public void Bind(IPEndPoint endPoint, bool reuse) => IsBound = true;

        public void JoinMulticastGroup(IPAddress group, IPAddress interfaceAddress) {
        }

        public void SendTo(ReadOnlySpan<byte> packet, IPEndPoint destination) => Sends.Add(destination);

        public int Receive(Span<byte> buffer, int timeoutMs) => -1;

        public void Dispose() {
        }
    }

    private sealed class RecordingSocketFactory : IUdpSocketFactory {
        public List<RecordingSocket> Sockets { get; } = new();

        /// <summary>Index 1 is the active query socket (index 0 is the passive listener).</summary>
        public RecordingSocket Active => Sockets[1];

        public IUdpSocket Create() {
            var socket = new RecordingSocket();
            Sockets.Add(socket);
            return socket;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 4000) {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline) {
            if (condition()) {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("condition not met within " + timeoutMs + "ms");
    }

    [Fact]
    public async Task TriggerQuerySendsAnImmediateBurstWithFrozenClock() {
        var factory = new RecordingSocketFactory();
        // Frozen clock: the 60 s requery floor can never elapse, so any burst after the initial
        // one is attributable to TriggerQuery alone.
        var browser = new MdnsFallbackBrowser(
            factory,
            () => Array.Empty<IPAddress>(),
            () => T0,
            _ => { },
            // Must actually yield: Task.CompletedTask would let the loop spin synchronously and
            // never return to the caller. A frozen clock still guarantees no automatic requery.
            (_, ct) => Task.Delay(1, ct));

        using var cts = new CancellationTokenSource();
        var run = browser.RunAsync(cts.Token);

        int domains = MdnsFallbackBrowser.ServiceDomains.Length;
        await WaitForAsync(() => factory.Sockets.Count >= 2 && factory.Active.Sends.Count >= domains);
        int afterInitial = factory.Active.Sends.Count;

        await Task.Delay(60);
        Assert.Equal(afterInitial, factory.Active.Sends.Count);

        browser.TriggerQuery();

        await WaitForAsync(() => factory.Active.Sends.Count > afterInitial);
        Assert.True(factory.Active.Sends.Count >= afterInitial + domains);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public void TriggerQueryWithNoSessionIsInert() {
        var factory = new RecordingSocketFactory();
        using var life = new MdnsFallbackLifecycle(
            _ => { },
            () => Array.Empty<IPAddress>(),
            factory,
            () => T0);

        // No EnsureRunning: nothing to trigger, and it must not throw.
        life.TriggerQuery();
        life.StartQrQueryWindow();
        life.StopQrQueryWindow();
        Assert.Empty(factory.Sockets);
    }

    [Fact]
    public async Task LifecycleForwardsTriggerToTheRunningBrowser() {
        var factory = new RecordingSocketFactory();
        using var life = new MdnsFallbackLifecycle(
            _ => { },
            () => Array.Empty<IPAddress>(),
            factory,
            () => T0);

        life.EnsureRunning(generation: 1);

        int domains = MdnsFallbackBrowser.ServiceDomains.Length;
        await WaitForAsync(() => factory.Sockets.Count >= 2 && factory.Active.Sends.Count >= domains);
        int afterInitial = factory.Active.Sends.Count;

        life.TriggerQuery();

        await WaitForAsync(() => factory.Active.Sends.Count > afterInitial);

        life.Stop();
    }

    [Fact]
    public async Task GenerationReplacementDoesNotCarryQrFastWindow() {
        var factory = new RecordingSocketFactory();
        long ticks = T0.UtcTicks;
        using var life = new MdnsFallbackLifecycle(
            _ => { }, () => Array.Empty<IPAddress>(), factory,
            () => new DateTimeOffset(Interlocked.Read(ref ticks), TimeSpan.Zero));
        life.EnsureRunning(1);
        int domains = MdnsFallbackBrowser.ServiceDomains.Length;
        await WaitForAsync(() => factory.Sockets.Count >= 2 && factory.Active.Sends.Count == domains);
        life.StartQrQueryWindow();
        await WaitForAsync(() => factory.Active.Sends.Count == 2 * domains);
        life.EnsureRunning(2);
        await WaitForAsync(() => factory.Sockets.Count >= 4 && factory.Sockets[3].Sends.Count == domains);
        Interlocked.Exchange(ref ticks, T0.AddSeconds(2).UtcTicks);
        await Task.Delay(650, TestContext.Current.CancellationToken);
        Assert.Equal(domains, factory.Sockets[3].Sends.Count);
        Assert.Equal(2, life.RunningGeneration);
    }
}
