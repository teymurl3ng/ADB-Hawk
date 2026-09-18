#pragma warning disable CA1707
#pragma warning disable CA2007
#pragma warning disable xUnit1051
namespace AdbView.Tests.Adb.Protocol;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Protocol;
using AdbView.Tests.Fakes;
using Xunit;

public sealed class AdbHostConnectionTests {
    [Fact]
    public async Task ExecuteOneShotAsync_Success_ReturnsExactPayloadBytes() {
        // Requirement A: exact host:version request bytes verified on server & payload received
        await using var server = new FakeAdbServer();
        server.RespondOkayOneShot("0029");

        await using var connection = new AdbHostConnection(server.EndPoint);
        var payload = await connection.ExecuteOneShotAsync("host:version");

        Assert.Equal("0029", Encoding.ASCII.GetString(payload.Span));
        Assert.Single(server.ReceivedCommands);
        Assert.Equal("host:version", server.ReceivedCommands[0]);
        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task ExecuteOneShotAsync_FragmentedOkay_ReconstructedDeterministically() {
        // Requirement G: fragmented OKAY reconstructed via TaskCompletionSource
        await using var server = new FakeAdbServer();
        var firstChunkSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceedWithRest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            // Send first 2 bytes "OK"
            await socket.SendAsync("OK"u8.ToArray(), SocketFlags.None);
            firstChunkSent.SetResult();

            await proceedWithRest.Task;
            // Send remaining "AY00040029"
            await socket.SendAsync("AY00040029"u8.ToArray(), SocketFlags.None);
        });

        await using var connection = new AdbHostConnection(server.EndPoint);
        var readTask = connection.ExecuteOneShotAsync("host:version");

        await firstChunkSent.Task;
        // Verify client operation has NOT completed yet
        Assert.False(readTask.IsCompleted);

        proceedWithRest.SetResult();
        var payload = await readTask;

        Assert.Equal("0029", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public async Task ExecuteOneShotAsync_FragmentedLength_ReconstructedDeterministically() {
        // Requirement H: fragmented length reconstructed
        await using var server = new FakeAdbServer();
        var firstChunkSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceedWithRest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            // Send OKAY + first 2 bytes of length "00"
            await socket.SendAsync("OKAY00"u8.ToArray(), SocketFlags.None);
            firstChunkSent.SetResult();

            await proceedWithRest.Task;
            // Send remaining length "04" + payload "0029"
            await socket.SendAsync("040029"u8.ToArray(), SocketFlags.None);
        });

        await using var connection = new AdbHostConnection(server.EndPoint);
        var readTask = connection.ExecuteOneShotAsync("host:version");

        await firstChunkSent.Task;
        Assert.False(readTask.IsCompleted);

        proceedWithRest.SetResult();
        var payload = await readTask;

        Assert.Equal("0029", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public async Task ExecuteOneShotAsync_FragmentedPayload_ReconstructedDeterministically() {
        // Requirement I: fragmented payload reconstructed
        await using var server = new FakeAdbServer();
        var firstChunkSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceedWithRest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            // Send OKAY + length "0004" + first 2 bytes of payload "00"
            await socket.SendAsync("OKAY000400"u8.ToArray(), SocketFlags.None);
            firstChunkSent.SetResult();

            await proceedWithRest.Task;
            // Send remaining payload "29"
            await socket.SendAsync("29"u8.ToArray(), SocketFlags.None);
        });

        await using var connection = new AdbHostConnection(server.EndPoint);
        var readTask = connection.ExecuteOneShotAsync("host:version");

        await firstChunkSent.Task;
        Assert.False(readTask.IsCompleted);

        proceedWithRest.SetResult();
        var payload = await readTask;

        Assert.Equal("0029", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public async Task ExecuteOneShotAsync_EarlyEofStatus_ThrowsUnexpectedEof() {
        // Requirement J: early EOF status
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            // Send only 2 bytes of status header then close
            await socket.SendAsync("OK"u8.ToArray(), SocketFlags.None);
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
        });

        await using var connection = new AdbHostConnection(server.EndPoint);
        var ex = await Assert.ThrowsAsync<AdbProtocolException>(() => connection.ExecuteOneShotAsync("host:version"));
        Assert.Equal(AdbErrorCode.UnexpectedEof, ex.ErrorCode);
    }

    [Fact]
    public async Task ExecuteOneShotAsync_EarlyEofPayload_ThrowsUnexpectedEof() {
        // Requirement K: early EOF payload
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            // Send status + length 4 + only 2 bytes of payload then close
            await socket.SendAsync("OKAY000400"u8.ToArray(), SocketFlags.None);
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
        });

        await using var connection = new AdbHostConnection(server.EndPoint);
        var ex = await Assert.ThrowsAsync<AdbProtocolException>(() => connection.ExecuteOneShotAsync("host:version"));
        Assert.Equal(AdbErrorCode.UnexpectedEof, ex.ErrorCode);
    }

    [Fact]
    public async Task ExecuteOneShotAsync_PayloadExceedingCap_ThrowsPayloadTooLargeBeforeRead() {
        // Requirement L: frame exceeding cap rejected before allocation/read
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            // Send length 200 bytes, but connection max frame is capped at 100 bytes
            await socket.SendAsync("OKAY00C8"u8.ToArray(), SocketFlags.None);
        });

        await using var connection = new AdbHostConnection(
            server.EndPoint,
            maxFrameLength: 100);

        var ex = await Assert.ThrowsAsync<AdbProtocolException>(() => connection.ExecuteOneShotAsync("host:version"));
        Assert.Equal(AdbErrorCode.PayloadTooLarge, ex.ErrorCode);
    }

    [Fact]
    public async Task ExecuteOneShotAsync_FailStatus_PreservesDiagnosticReason() {
        // Requirement M: FAIL diagnostic preserved
        await using var server = new FakeAdbServer();
        server.RespondFail("device offline");

        await using var connection = new AdbHostConnection(server.EndPoint);
        var ex = await Assert.ThrowsAsync<AdbProtocolException>(() => connection.ExecuteOneShotAsync("host:version"));

        Assert.Equal(AdbErrorCode.CommandFailed, ex.ErrorCode);
        Assert.Equal("device offline", ex.ServerReason);
        Assert.Contains("device offline", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteOneShotAsync_CallerCancellation_PropagatesOperationCanceledException() {
        // Requirement N: caller cancellation during blocked read
        await using var server = new FakeAdbServer();
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            requestReceived.SetResult();
            // Block indefinitely without sending bytes
            await Task.Delay(TimeSpan.FromSeconds(30));
        });

        using var cts = new CancellationTokenSource();
        await using var connection = new AdbHostConnection(server.EndPoint);
        var readTask = connection.ExecuteOneShotAsync("host:version", cts.Token);

        await requestReceived.Task;
        await cts.CancelAsync();

        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(readTask, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
    }

    [Fact]
    public async Task ExecuteOneShotAsync_AcceptedSilentConnection_ThrowsInternalTimeout() {
        // Requirement O: internal request timeout on accepted silent connection
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            // Accepts request, never writes back
            await Task.Delay(TimeSpan.FromSeconds(30));
        });

        // Use short 150ms timeout to verify fast, deterministic timeout failure
        await using var connection = new AdbHostConnection(
            server.EndPoint,
            requestTimeout: TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAsync<AdbConnectionException>(() => connection.ExecuteOneShotAsync("host:version"));
        Assert.True(ex.IsTimeout);
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteOneShotAsync_ConnectionRefusal_ThrowsDeterministicConnectionException() {
        // Requirement P: deterministic connection refusal handling
        // Find a closed high port
        int closedPort;
        using (var tempListener = new TcpListener(IPAddress.Loopback, 0)) {
            tempListener.Start();
            closedPort = ((IPEndPoint)tempListener.LocalEndpoint).Port;
            tempListener.Stop();
        }

        await using var connection = new AdbHostConnection(
            new IPEndPoint(IPAddress.Loopback, closedPort),
            connectTimeout: TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<AdbConnectionException>(() => connection.ExecuteOneShotAsync("host:version"));
        Assert.False(ex.IsTimeout);
        Assert.NotNull(ex.InnerException);
        Assert.Equal(SocketError.ConnectionRefused, ex.SocketError);
    }

    [Fact]
    public async Task ExecuteOneShotAsync_SequentialIndependentRequests_Succeed() {
        // Requirement Q: sequential independent requests
        await using var server = new FakeAdbServer();
        server.RespondOkayOneShot("0029");

        await using var connection = new AdbHostConnection(server.EndPoint);

        var first = await connection.ExecuteOneShotAsync("host:version");
        Assert.Equal("0029", Encoding.ASCII.GetString(first.Span));

        var second = await connection.ExecuteOneShotAsync("host:version");
        Assert.Equal("0029", Encoding.ASCII.GetString(second.Span));

        Assert.Equal(2, server.ReceivedCommands.Count);
        server.AssertNoKillCommandSent();
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_MultipleFrames_YieldedIndependentlyAndInOrder() {
        // Stream Requirement A: multiple frames yielded independently and in order
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("0005first"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("0006second"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("0000"u8.ToArray(), SocketFlags.None); // empty frame
            await socket.SendAsync("0005third"u8.ToArray(), SocketFlags.None);
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
        });

        await using var connection = new AdbHostConnection(server.EndPoint);
        var frames = new List<string>();

        await foreach (var frame in connection.ExecuteFramedStreamAsync("host:track-devices")) {
            frames.Add(Encoding.ASCII.GetString(frame.Span));
        }

        Assert.Equal(4, frames.Count);
        Assert.Equal("first", frames[0]);
        Assert.Equal("second", frames[1]);
        Assert.Equal("", frames[2]);
        Assert.Equal("third", frames[3]);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_FragmentedLengthPrefix_Reconstructed() {
        // Stream Requirement B: fragmented frame-length prefix reconstructed
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("0"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("0"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("0"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("4"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("item"u8.ToArray(), SocketFlags.None);
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
        });

        await using var connection = new AdbHostConnection(server.EndPoint);
        var frames = new List<string>();

        await foreach (var frame in connection.ExecuteFramedStreamAsync("host:track-devices")) {
            frames.Add(Encoding.ASCII.GetString(frame.Span));
        }

        Assert.Single(frames);
        Assert.Equal("item", frames[0]);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_FragmentedPayload_Reconstructed() {
        // Stream Requirement C: fragmented frame payload reconstructed
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("000A"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("hello"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("world"u8.ToArray(), SocketFlags.None);
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
        });

        await using var connection = new AdbHostConnection(server.EndPoint);
        var frames = new List<string>();

        await foreach (var frame in connection.ExecuteFramedStreamAsync("host:track-devices")) {
            frames.Add(Encoding.ASCII.GetString(frame.Span));
        }

        Assert.Single(frames);
        Assert.Equal("helloworld", frames[0]);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_MalformedLength_ThrowsMalformedLength() {
        // Stream Requirement D: malformed stream-frame length is rejected
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY"u8.ToArray(), SocketFlags.None);
            await socket.SendAsync("000Zdata"u8.ToArray(), SocketFlags.None);
        });

        await using var connection = new AdbHostConnection(server.EndPoint);

        var ex = await Assert.ThrowsAsync<AdbProtocolException>(async () => {
            await foreach (var _ in connection.ExecuteFramedStreamAsync("host:track-devices")) { }
        });
        Assert.Equal(AdbErrorCode.MalformedLength, ex.ErrorCode);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_EofBetweenCompleteFrames_EndsNormally() {
        // Stream Requirement E: EOF between complete frames ends normally
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY0004done"u8.ToArray(), SocketFlags.None);
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
        });

        await using var connection = new AdbHostConnection(server.EndPoint);
        var frames = new List<string>();

        await foreach (var frame in connection.ExecuteFramedStreamAsync("host:track-devices")) {
            frames.Add(Encoding.ASCII.GetString(frame.Span));
        }

        Assert.Single(frames);
        Assert.Equal("done", frames[0]);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_EofMidwayThroughLengthPrefix_ThrowsUnexpectedEof() {
        // Stream Requirement F1: EOF midway through length prefix
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY00"u8.ToArray(), SocketFlags.None);
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
        });

        await using var connection = new AdbHostConnection(server.EndPoint);

        var ex = await Assert.ThrowsAsync<AdbProtocolException>(async () => {
            await foreach (var _ in connection.ExecuteFramedStreamAsync("host:track-devices")) { }
        });
        Assert.Equal(AdbErrorCode.UnexpectedEof, ex.ErrorCode);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_EofMidwayThroughPayload_ThrowsUnexpectedEof() {
        // Stream Requirement F2: EOF midway through payload
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY0008four"u8.ToArray(), SocketFlags.None);
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
        });

        await using var connection = new AdbHostConnection(server.EndPoint);

        var ex = await Assert.ThrowsAsync<AdbProtocolException>(async () => {
            await foreach (var _ in connection.ExecuteFramedStreamAsync("host:track-devices")) { }
        });
        Assert.Equal(AdbErrorCode.UnexpectedEof, ex.ErrorCode);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_CallerCancellation_PropagatesOperationCanceledException() {
        // Stream Requirement G: caller cancellation terminates enumeration cleanly
        await using var server = new FakeAdbServer();
        var clientBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY0005first"u8.ToArray(), SocketFlags.None);
            clientBlocked.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(30));
        });

        using var cts = new CancellationTokenSource();
        await using var connection = new AdbHostConnection(server.EndPoint);
        var frames = new List<string>();

        async Task ConsumeAsync() {
            await foreach (var frame in connection.ExecuteFramedStreamAsync("host:track-devices", cts.Token)) {
                frames.Add(Encoding.ASCII.GetString(frame.Span));
            }
        }

        var streamTask = ConsumeAsync();

        // Deterministic wait: client received first frame and is blocked waiting for second
        await clientBlocked.Task;

        // Cancel caller token
        await cts.CancelAsync();

        // 1-second watchdog
        var completed = await Task.WhenAny(streamTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(streamTask, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streamTask);
        Assert.Single(frames);
        Assert.Equal("first", frames[0]);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_Disposal_ClosesUnderlyingConnection() {
        // Stream Requirement H1: disposal closes underlying connection
        await using var server = new FakeAdbServer();
        var clientDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY0005first0006second"u8.ToArray(), SocketFlags.None);
            byte[] buf = new byte[1];
            try {
                int r = await socket.ReceiveAsync(buf, SocketFlags.None);
                if (r == 0) {
                    clientDisconnected.SetResult();
                }
            }
            catch (SocketException) {
                clientDisconnected.SetResult();
            }
        });

        await using var connection = new AdbHostConnection(server.EndPoint);

        await foreach (var frame in connection.ExecuteFramedStreamAsync("host:track-devices")) {
            Assert.Equal("first", Encoding.ASCII.GetString(frame.Span));
            break; // Caller break triggers DisposeAsync on enumerator
        }

        // Server confirms client closed socket
        var completed = await Task.WhenAny(clientDisconnected.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(clientDisconnected.Task, completed);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_Cancellation_ClosesUnderlyingConnection() {
        // Stream Requirement H2: cancellation closes underlying connection
        await using var server = new FakeAdbServer();
        var clientDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY0005first"u8.ToArray(), SocketFlags.None);
            byte[] buf = new byte[1];
            try {
                int r = await socket.ReceiveAsync(buf, SocketFlags.None);
                if (r == 0) {
                    clientDisconnected.SetResult();
                }
            }
            catch (SocketException) {
                clientDisconnected.SetResult();
            }
        });

        using var cts = new CancellationTokenSource();
        await using var connection = new AdbHostConnection(server.EndPoint);

        async Task ConsumeAsync() {
            await foreach (var frame in connection.ExecuteFramedStreamAsync("host:track-devices", cts.Token)) {
                await cts.CancelAsync();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(ConsumeAsync);

        // Server confirms client closed socket
        var completed = await Task.WhenAny(clientDisconnected.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(clientDisconnected.Task, completed);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_FailResponse_ThrowsAdbProtocolException() {
        await using var server = new FakeAdbServer();
        server.RespondFail("unknown host service");

        await using var connection = new AdbHostConnection(server.EndPoint);

        var ex = await Assert.ThrowsAsync<AdbProtocolException>(async () => {
            await foreach (var _ in connection.ExecuteFramedStreamAsync("host:track-unknown")) { }
        });
        Assert.Equal(AdbErrorCode.CommandFailed, ex.ErrorCode);
        Assert.Equal("unknown host service", ex.ServerReason);
    }

    [Fact]
    public async Task ExecuteFramedStreamAsync_PayloadExceedingCap_ThrowsPayloadTooLargeBeforeRead() {
        await using var server = new FakeAdbServer();
        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY00C8"u8.ToArray(), SocketFlags.None);
        });

        await using var connection = new AdbHostConnection(
            server.EndPoint,
            maxFrameLength: 100);

        var ex = await Assert.ThrowsAsync<AdbProtocolException>(async () => {
            await foreach (var _ in connection.ExecuteFramedStreamAsync("host:track-devices")) { }
        });
        Assert.Equal(AdbErrorCode.PayloadTooLarge, ex.ErrorCode);
    }

    [Fact]
    public async Task DisposeAsync_CausesServerSideConnectionClosure() {
        // Requirement S: disposal causes server-side connection closure
        await using var server = new FakeAdbServer();
        var clientConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientDisconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        server.SetHandler(async (socket, cmd) => {
            await socket.SendAsync("OKAY00040029"u8.ToArray(), SocketFlags.None);
            clientConnected.SetResult();

            // Wait for client to close socket
            byte[] buf = new byte[1];
            int r = await socket.ReceiveAsync(buf, SocketFlags.None);
            if (r == 0) {
                clientDisconnected.SetResult();
            }
        });

        var connection = new AdbHostConnection(server.EndPoint);
        var res = await connection.ExecuteOneShotAsync("host:version");
        Assert.Equal("0029", Encoding.ASCII.GetString(res.Span));

        await clientConnected.Task;

        // Dispose client
        await connection.DisposeAsync();

        // Server must observe disconnect within 2 seconds
        var completed = await Task.WhenAny(clientDisconnected.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(clientDisconnected.Task, completed);
    }
}
