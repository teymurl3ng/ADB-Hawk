namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using Xunit;

public sealed class MdnsFallbackWindowTests {
    [Fact]
    public async Task DelayedScanWindowQueriesEveryTwoSecondsWithFrozenClockBetweenSlices() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        await harness.StartAsync().ConfigureAwait(true);
        int burst = MdnsFallbackBrowser.ServiceDomains.Length;
        Assert.Equal(burst, harness.Factory.Active.Sends.Count);
        harness.Browser.StartQrQueryWindow();
        await harness.StepAsync(10).ConfigureAwait(true);
        Assert.Equal(2 * burst, harness.Factory.Active.Sends.Count);
        await harness.StepAsync(10).ConfigureAwait(true);
        await harness.StepAsync(11.999).ConfigureAwait(true);
        Assert.Equal(2 * burst, harness.Factory.Active.Sends.Count);
        await harness.StepAsync(12).ConfigureAwait(true);
        Assert.Equal(3 * burst, harness.Factory.Active.Sends.Count);
        await harness.StepAsync(40).ConfigureAwait(true);
        // A late slice sends once, never catches up missed intervals in a burst storm.
        Assert.Equal(4 * burst, harness.Factory.Active.Sends.Count);
    }

    [Fact]
    public async Task EndingWindowStopsFastQueriesAndKeepsBackgroundFloor() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        await harness.StartAsync().ConfigureAwait(true);
        harness.Browser.StartQrQueryWindow();
        await harness.StepAsync(1).ConfigureAwait(true);
        harness.Browser.StopQrQueryWindow();
        int sends = harness.Factory.Active.Sends.Count;
        await harness.StepAsync(3).ConfigureAwait(true);
        await harness.StepAsync(60.999).ConfigureAwait(true);
        Assert.Equal(sends, harness.Factory.Active.Sends.Count);
        await harness.StepAsync(61).ConfigureAwait(true);
        Assert.Equal(sends + MdnsFallbackBrowser.ServiceDomains.Length, harness.Factory.Active.Sends.Count);
    }

    [Fact]
    public async Task WindowExpiresAtTwoMinutesAndRepeatedStartsDoNotExtendIt() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        await harness.StartAsync().ConfigureAwait(true);
        harness.Browser.StartQrQueryWindow();
        await harness.StepAsync(118).ConfigureAwait(true);
        harness.Browser.StartQrQueryWindow();
        int sends = harness.Factory.Active.Sends.Count;
        await harness.StepAsync(120).ConfigureAwait(true);
        await harness.StepAsync(122).ConfigureAwait(true);
        Assert.Equal(sends, harness.Factory.Active.Sends.Count);
    }

    [Fact]
    public async Task EmptySetOutsideQrStillUsesSixtySecondFloor() {
        var harness = new MdnsBrowserHarness();
        await using var cleanup = harness.ConfigureAwait(true);
        await harness.StartAsync().ConfigureAwait(true);
        int sends = harness.Factory.Active.Sends.Count;
        await harness.StepAsync(2).ConfigureAwait(true);
        await harness.StepAsync(59.999).ConfigureAwait(true);
        Assert.Equal(sends, harness.Factory.Active.Sends.Count);
        await harness.StepAsync(60).ConfigureAwait(true);
        Assert.Equal(2 * sends, harness.Factory.Active.Sends.Count);
    }
}

/// <summary>Pauses at each existing loop delay so tests can freeze/advance time without sleeps.</summary>
internal sealed class MdnsBrowserHarness : IAsyncDisposable {
    private static readonly DateTimeOffset Epoch = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource _entered = NewSignal();
    private TaskCompletionSource _release = NewSignal();
    private Task? _run;

    public MdnsBrowserHarness() {
        Browser = new MdnsFallbackBrowser(Factory, () => Addresses, () => Now, _ => { }, DelayAsync);
    }

    public DateTimeOffset Now { get; private set; } = Epoch;
    public IReadOnlyList<IPAddress> Addresses { get; set; } = Array.Empty<IPAddress>();
    public MdnsRecordingSocketFactory Factory { get; } = new();
    public MdnsFallbackBrowser Browser { get; }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
        Assert.Equal(MdnsFallbackBrowser.LoopSlice, delay);
        _release = NewSignal();
        _entered.SetResult();
        return _release.Task.WaitAsync(cancellationToken);
    }

    public async Task StartAsync() {
        _run = Browser.RunAsync(_cts.Token);
        await _entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    public async Task StepAsync(double seconds) {
        Now = Epoch.AddSeconds(seconds);
        var release = _release;
        _entered = NewSignal();
        release.SetResult();
        await _entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync() {
        await _cts.CancelAsync().ConfigureAwait(true);
        if (_run is not null) {
            await _run.ConfigureAwait(true);
        }

        _cts.Dispose();
    }
}

internal sealed class MdnsRecordingSocketFactory : IUdpSocketFactory {
    public List<MdnsRecordingSocket> Sockets { get; } = new();
    public MdnsRecordingSocket Active => Sockets[1];
    public IUdpSocket Create() {
        var socket = new MdnsRecordingSocket();
        Sockets.Add(socket);
        return socket;
    }
}

internal sealed class MdnsRecordingSocket : IUdpSocket {
    public List<byte[]> Sends { get; } = new();
    public List<IPAddress> SendInterfaces { get; } = new();
    public List<IPAddress> SelectedInterfaces { get; } = new();
    public IPAddress? FailedInterface { get; set; }
    private IPAddress _selected = IPAddress.Any;
    public bool IsBound { get; private set; }
    public void Bind(IPEndPoint endPoint, bool reuse) => IsBound = true;
    public void JoinMulticastGroup(IPAddress group, IPAddress interfaceAddress) { }
    public void SetMulticastInterface(IPAddress interfaceAddress) {
        if (interfaceAddress.Equals(FailedInterface)) {
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable);
        }

        _selected = interfaceAddress;
        SelectedInterfaces.Add(interfaceAddress);
    }

    public void SendTo(ReadOnlySpan<byte> packet, IPEndPoint destination) {
        Sends.Add(packet.ToArray());
        SendInterfaces.Add(_selected);
    }
    public int Receive(Span<byte> buffer, int timeoutMs) => -1;
    public void Dispose() { }
}
