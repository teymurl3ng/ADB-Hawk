#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb;

using System;
using System.Collections.Generic;
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

public sealed class DeviceTrackerTests {
    private sealed class ControllableClock {
        public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Get() => Now;
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
    public async Task J_K_MalformedProtobuf_Dropped_ThenValidFrameDelivered() {
        await using var server = new FakeAdbServer();
        var clock = new ControllableClock();
        var malformedLogs = new Collector<string>();
        var lists = new Collector<DeviceListReceived>();
        var sessionReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holdCts = new CancellationTokenSource();

        byte[] goodDevice = FakeAdbTrackDevices.EncodeDevice("GOOD", state: 8, connectionType: 1);
        byte[] goodPayload = FakeAdbTrackDevices.EncodeDevices(goodDevice);
        byte[] badPayload = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];

        server.SetHandler(async (socket, cmd) => {
            Assert.Equal(DeviceTracker.ProtoBinaryService, cmd);
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbTrackDevices.SendFrameAsync(socket, badPayload).ConfigureAwait(false);
            sessionReady.TrySetResult();
            await FakeAdbTrackDevices.SendFrameAsync(socket, goodPayload).ConfigureAwait(false);
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            backoff: new DeviceTrackerBackoff(() => 0),
            getUtcNow: clock.Get,
            delay: (_, _) => Task.CompletedTask,
            onMalformedPayload: malformedLogs.Add);

        tracker.ListReceived += lists.Add;

        var runTask = tracker.RunAsync(serverGeneration: 1, cts.Token);
        await sessionReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => lists.Count >= 1, TimeSpan.FromSeconds(5));

        Assert.Single(lists.Snapshot());
        Assert.Equal("GOOD", lists[0].Devices[0].Serial);
        Assert.False(malformedLogs.IsEmpty);

        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(runTask);
    }

    [Fact]
    public async Task MalformedPayload_LogRateLimitedOncePerMinute() {
        await using var server = new FakeAdbServer();
        var clock = new ControllableClock();
        var malformedLogs = new Collector<string>();
        var secondFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holdCts = new CancellationTokenSource();
        byte[] badPayload = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];

        server.SetHandler(async (socket, cmd) => {
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbTrackDevices.SendFrameAsync(socket, badPayload).ConfigureAwait(false);
            secondFrame.TrySetResult();
            await FakeAdbTrackDevices.SendFrameAsync(socket, badPayload).ConfigureAwait(false);
            thirdFrame.TrySetResult();
            await FakeAdbTrackDevices.SendFrameAsync(socket, badPayload).ConfigureAwait(false);
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            getUtcNow: clock.Get,
            delay: (_, _) => Task.CompletedTask,
            onMalformedPayload: malformedLogs.Add);

        var runTask = tracker.RunAsync(1, cts.Token);
        await secondFrame.Task;
        await WaitUntilAsync(() => malformedLogs.Count >= 1, TimeSpan.FromSeconds(3));
        Assert.Single(malformedLogs.Snapshot());

        await thirdFrame.Task;
        await Task.Delay(50);
        Assert.Single(malformedLogs.Snapshot());

        clock.Now = clock.Now.AddMinutes(1).AddSeconds(1);
        await cts.CancelAsync();
        await holdCts.CancelAsync();
        try {
            await runTask;
        }
        catch (OperationCanceledException) {
        }

        using var holdCts2 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fourth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SetHandler(async (socket, cmd) => {
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbTrackDevices.SendFrameAsync(socket, badPayload).ConfigureAwait(false);
            fourth.TrySetResult();
            try {
                await Task.Delay(Timeout.Infinite, holdCts2.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });
        var run2 = tracker.RunAsync(1, cts2.Token);
        await fourth.Task;
        await WaitUntilAsync(() => malformedLogs.Count >= 2, TimeSpan.FromSeconds(3));
        Assert.Equal(2, malformedLogs.Count);
        await cts2.CancelAsync();
        await holdCts2.CancelAsync();
        await AssertCancelledOrCompleted(run2);
    }

    [Fact]
    public async Task L_MalformedFraming_TerminatesSession_EmitsFailure() {
        await using var server = new FakeAdbServer();
        var failures = new Collector<DeviceTrackerFailed>();
        var observedDelays = new List<TimeSpan>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var framed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            await socket.SendAsync("ZZZZ"u8.ToArray(), SocketFlags.None).ConfigureAwait(false);
            framed.TrySetResult();
        });

        using var cts = new CancellationTokenSource();
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            backoff: new DeviceTrackerBackoff(() => 0),
            getUtcNow: () => DateTimeOffset.UnixEpoch,
            delay: async (d, ct) => {
                observedDelays.Add(d);
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            });
        tracker.Failed += failures.Add;

        var runTask = tracker.RunAsync(1, cts.Token);
        await framed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !failures.IsEmpty && observedDelays.Count > 0, TimeSpan.FromSeconds(5));
        Assert.False(failures.IsEmpty);
        Assert.NotEmpty(observedDelays);

        await cts.CancelAsync();
        gate.TrySetResult();
        await AssertCancelledOrCompleted(runTask);
    }

    [Fact]
    public async Task M_N_O_P_Eof_Reconnect_CompleteReplace_Timestamps() {
        await using var server = new FakeAdbServer();
        var clock = new ControllableClock();
        var lists = new Collector<DeviceListReceived>();
        var failures = new Collector<DeviceTrackerFailed>();
        var observedDelays = new List<TimeSpan>();
        int connections = 0;
        using var holdCts = new CancellationTokenSource();

        byte[] first = FakeAdbTrackDevices.EncodeDevices(
            FakeAdbTrackDevices.EncodeDevice("ONE", state: 8, connectionType: 1));
        byte[] second = FakeAdbTrackDevices.EncodeDevices(
            FakeAdbTrackDevices.EncodeDevice("TWO", state: 8, connectionType: 2),
            FakeAdbTrackDevices.EncodeDevice("THREE", state: 3, connectionType: 1));

        server.SetHandler(async (socket, cmd) => {
            int n = Interlocked.Increment(ref connections);
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            if (n == 1) {
                await FakeAdbTrackDevices.SendFrameAsync(socket, first).ConfigureAwait(false);
                socket.Shutdown(SocketShutdown.Both);
                return;
            }

            clock.Now = clock.Now.AddSeconds(5);
            await FakeAdbTrackDevices.SendFrameAsync(socket, second).ConfigureAwait(false);
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            backoff: new DeviceTrackerBackoff(() => 0),
            getUtcNow: clock.Get,
            delay: (d, _) => {
                observedDelays.Add(d);
                return Task.CompletedTask;
            });
        tracker.ListReceived += lists.Add;
        tracker.Failed += failures.Add;

        var runTask = tracker.RunAsync(1, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal("ONE", lists[0].Devices[0].Serial);
        Assert.Equal(2, lists[1].Devices.Length);
        Assert.Equal("TWO", lists[1].Devices[0].Serial);
        Assert.Equal("THREE", lists[1].Devices[1].Serial);
        Assert.True(lists[1].At > lists[0].At);
        Assert.False(failures.IsEmpty);
        Assert.Equal(TimeSpan.FromMilliseconds(500), observedDelays[0]);

        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(runTask);
    }

    [Fact]
    public async Task R_BackoffResetsAfterSuccessfulRecovery() {
        await using var server = new FakeAdbServer();
        var delays = new List<TimeSpan>();
        int connections = 0;
        var lists = new Collector<DeviceListReceived>();
        using var holdCts = new CancellationTokenSource();

        byte[] payload = FakeAdbTrackDevices.EncodeDevices(
            FakeAdbTrackDevices.EncodeDevice("S", state: 8, connectionType: 1));

        server.SetHandler(async (socket, cmd) => {
            int n = Interlocked.Increment(ref connections);
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbTrackDevices.SendFrameAsync(socket, payload).ConfigureAwait(false);
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
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            backoff: new DeviceTrackerBackoff(() => 0),
            getUtcNow: () => DateTimeOffset.UnixEpoch,
            delay: (d, _) => {
                delays.Add(d);
                return Task.CompletedTask;
            });
        tracker.ListReceived += lists.Add;

        var runTask = tracker.RunAsync(1, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 3, TimeSpan.FromSeconds(5));

        Assert.All(delays, d => Assert.Equal(TimeSpan.FromMilliseconds(500), d));

        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(runTask);
    }

    [Fact]
    public async Task S_T_ProtoRejection_ActivatesTextFallback_NotRetriedInGeneration() {
        await using var server = new FakeAdbServer();
        var degraded = new Collector<string>();
        var lists = new Collector<DeviceListReceived>();
        var secondText = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int textSessions = 0;
        using var holdCts = new CancellationTokenSource();

        // AOSP sockets.cpp: unknown host service '<service>'
        string aospReason = $"unknown host service '{DeviceTracker.ProtoBinaryService}'";

        server.SetHandler(async (socket, cmd) => {
            if (cmd == DeviceTracker.ProtoBinaryService) {
                await FakeAdbTrackDevices.SendFailAsync(socket, aospReason).ConfigureAwait(false);
                return;
            }

            Assert.Equal(DeviceTracker.TextLongService, cmd);
            int t = Interlocked.Increment(ref textSessions);
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            byte[] text = Encoding.UTF8.GetBytes("ABC\tdevice product:p model:m device:d\n");
            await FakeAdbTrackDevices.SendFrameAsync(socket, text).ConfigureAwait(false);
            if (t >= 2) {
                secondText.TrySetResult();
                try {
                    await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                }
                return;
            }

            socket.Shutdown(SocketShutdown.Both);
        });

        using var cts = new CancellationTokenSource();
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            backoff: new DeviceTrackerBackoff(() => 0),
            getUtcNow: () => DateTimeOffset.UnixEpoch,
            delay: (_, _) => Task.CompletedTask,
            onDegraded: degraded.Add);
        tracker.ListReceived += lists.Add;

        var runTask = tracker.RunAsync(7, cts.Token);
        await secondText.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => lists.Count >= 2, TimeSpan.FromSeconds(5));

        int protoCount = 0;
        foreach (var cmd in server.ReceivedCommands) {
            if (cmd == DeviceTracker.ProtoBinaryService) {
                protoCount++;
            }
        }

        Assert.Equal(1, protoCount);
        Assert.Single(degraded.Snapshot());
        Assert.Equal(RawDeviceFormat.TrackDevicesL, lists[0].Devices[0].Format);
        Assert.Null(lists[0].Devices[0].ConnectionType);

        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(runTask);
    }

    [Fact]
    public async Task GenericProtoCommandFailed_DoesNotCacheUnsupported_RetriesProto() {
        await using var server = new FakeAdbServer();
        var degraded = new Collector<string>();
        var failures = new Collector<DeviceTrackerFailed>();
        var lists = new Collector<DeviceListReceived>();
        var delays = new Collector<TimeSpan>();
        int protoSessions = 0;
        using var holdCts = new CancellationTokenSource();

        byte[] payload = FakeAdbTrackDevices.EncodeDevices(
            FakeAdbTrackDevices.EncodeDevice("USB1", state: 8, connectionType: 1));

        server.SetHandler(async (socket, cmd) => {
            Assert.Equal(DeviceTracker.ProtoBinaryService, cmd);
            int n = Interlocked.Increment(ref protoSessions);
            if (n == 1) {
                // Generic FAIL — not the AOSP unknown-host-service diagnostic.
                await FakeAdbTrackDevices.SendFailAsync(socket, "device offline").ConfigureAwait(false);
                return;
            }

            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbTrackDevices.SendFrameAsync(socket, payload).ConfigureAwait(false);
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            backoff: new DeviceTrackerBackoff(() => 0),
            getUtcNow: () => DateTimeOffset.UnixEpoch,
            delay: (d, _) => {
                delays.Add(d);
                return Task.CompletedTask;
            },
            onDegraded: degraded.Add);
        tracker.ListReceived += lists.Add;
        tracker.Failed += failures.Add;

        var runTask = tracker.RunAsync(1, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 1 && protoSessions >= 2, TimeSpan.FromSeconds(5));

        Assert.Empty(degraded.Snapshot());
        Assert.DoesNotContain(server.ReceivedCommands, c => c == DeviceTracker.TextLongService);
        Assert.True(failures.Count >= 1);
        Assert.Contains(delays.Snapshot(), d => d == TimeSpan.FromMilliseconds(500));
        Assert.Equal(RawDeviceFormat.ProtoBinary, lists[0].Devices[0].Format);

        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(runTask);
    }

    [Fact]
    public async Task NextServerGeneration_ClearsCachedProtoUnsupported_RetriesProto() {
        await using var server = new FakeAdbServer();
        var lists = new Collector<DeviceListReceived>();
        int protoSessions = 0;
        int textSessions = 0;
        var textReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holdCts = new CancellationTokenSource();

        string aospReason = $"unknown host service '{DeviceTracker.ProtoBinaryService}'";
        byte[] protoPayload = FakeAdbTrackDevices.EncodeDevices(
            FakeAdbTrackDevices.EncodeDevice("USB1", state: 8, connectionType: 1));

        server.SetHandler(async (socket, cmd) => {
            if (cmd == DeviceTracker.ProtoBinaryService) {
                int p = Interlocked.Increment(ref protoSessions);
                if (p == 1) {
                    // Generation 1: service unknown → text fallback.
                    await FakeAdbTrackDevices.SendFailAsync(socket, aospReason).ConfigureAwait(false);
                    return;
                }

                // Generation 2+: proto available again.
                await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
                await FakeAdbTrackDevices.SendFrameAsync(socket, protoPayload).ConfigureAwait(false);
                try {
                    await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                }
                return;
            }

            Assert.Equal(DeviceTracker.TextLongService, cmd);
            Interlocked.Increment(ref textSessions);
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            byte[] text = Encoding.UTF8.GetBytes("ABC\tdevice\n");
            await FakeAdbTrackDevices.SendFrameAsync(socket, text).ConfigureAwait(false);
            textReady.TrySetResult();
            // Hold open so gen1 does not reconnect with more text frames.
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts1 = new CancellationTokenSource();
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            backoff: new DeviceTrackerBackoff(() => 0),
            getUtcNow: () => DateTimeOffset.UnixEpoch,
            delay: (_, _) => Task.CompletedTask);
        tracker.ListReceived += lists.Add;

        var run1 = tracker.RunAsync(1, cts1.Token);
        await textReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => lists.Count >= 1, TimeSpan.FromSeconds(5));
        Assert.Equal(RawDeviceFormat.TrackDevicesL, lists[0].Devices[0].Format);
        Assert.Equal(1, protoSessions);
        Assert.Equal(1, textSessions);

        await cts1.CancelAsync();
        await AssertCancelledOrCompleted(run1);

        int listsAfterGen1 = lists.Count;

        // Same tracker instance, new server generation — must try proto again.
        using var cts2 = new CancellationTokenSource();
        var run2 = tracker.RunAsync(2, cts2.Token);
        await WaitUntilAsync(
            () => protoSessions >= 2
                && lists.Snapshot().Skip(listsAfterGen1).Any(l =>
                    l.Devices.Length > 0 && l.Devices[0].Format == RawDeviceFormat.ProtoBinary),
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, textSessions);
        Assert.True(protoSessions >= 2);

        await cts2.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(run2);
    }

    [Fact]
    public async Task U_TransientProtoStreamFailure_DoesNotSwitchToText() {
        await using var server = new FakeAdbServer();
        var lists = new Collector<DeviceListReceived>();
        int protoSessions = 0;
        using var holdCts = new CancellationTokenSource();

        byte[] payload = FakeAdbTrackDevices.EncodeDevices(
            FakeAdbTrackDevices.EncodeDevice("USB1", state: 8, connectionType: 1));

        server.SetHandler(async (socket, cmd) => {
            Assert.Equal(DeviceTracker.ProtoBinaryService, cmd);
            int n = Interlocked.Increment(ref protoSessions);
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            await FakeAdbTrackDevices.SendFrameAsync(socket, payload).ConfigureAwait(false);
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
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            backoff: new DeviceTrackerBackoff(() => 0),
            getUtcNow: () => DateTimeOffset.UnixEpoch,
            delay: (_, _) => Task.CompletedTask);
        tracker.ListReceived += lists.Add;

        var runTask = tracker.RunAsync(1, cts.Token);
        await WaitUntilAsync(() => lists.Count >= 2 && protoSessions >= 2, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(server.ReceivedCommands, c => c == DeviceTracker.TextLongService);
        Assert.Equal(RawDeviceFormat.ProtoBinary, lists[0].Devices[0].Format);

        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(runTask);
    }

    [Fact]
    public async Task Y_Cancellation_TerminatesCleanly() {
        await using var server = new FakeAdbServer();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holdCts = new CancellationTokenSource();

        server.SetHandler(async (socket, cmd) => {
            await FakeAdbTrackDevices.SendOkayAsync(socket).ConfigureAwait(false);
            hold.TrySetResult();
            try {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
            }
        });

        using var cts = new CancellationTokenSource();
        var tracker = new DeviceTracker(
            connectionFactory: () => new AdbHostConnection(server.EndPoint),
            delay: (_, _) => Task.CompletedTask);

        var runTask = tracker.RunAsync(1, cts.Token);
        await hold.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await holdCts.CancelAsync();
        await AssertCancelledOrCompleted(runTask);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate()) {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token).ConfigureAwait(false);
        }
    }

    private static async Task AssertCancelledOrCompleted(Task runTask) {
        try {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
        }
    }
}
