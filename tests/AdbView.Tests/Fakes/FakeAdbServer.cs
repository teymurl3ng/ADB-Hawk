namespace AdbView.Tests.Fakes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

internal sealed class FakeAdbServer : IAsyncDisposable, IDisposable {
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoopTask;
    private readonly List<string> _receivedCommands = new();
    private readonly List<Socket> _activeSockets = new();
    private readonly object _lock = new();
    private Func<Socket, string, Task>? _handler;
    private bool _isDisposed;

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;
    public int Port => EndPoint.Port;

    public IReadOnlyList<string> ReceivedCommands {
        get {
            lock (_lock) {
                return _receivedCommands.ToList();
            }
        }
    }

    public int ActiveConnections {
        get {
            lock (_lock) {
                return _activeSockets.Count(s => s.Connected);
            }
        }
    }

    public FakeAdbServer() {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoopTask = Task.Run(AcceptLoopAsync);
    }

    public void SetHandler(Func<Socket, string, Task> handler) {
        lock (_lock) {
            _handler = handler;
        }
    }

    public void RespondOkayOneShot(string payload) =>
        RespondOkayOneShot(Encoding.ASCII.GetBytes(payload));

    public void RespondOkayOneShot(byte[] payload) {
        SetHandler(async (socket, cmd) => {
            byte[] header = Encoding.ASCII.GetBytes($"OKAY{payload.Length:X4}");
            await socket.SendAsync(header, SocketFlags.None).ConfigureAwait(false);
            if (payload.Length > 0) {
                await socket.SendAsync(payload, SocketFlags.None).ConfigureAwait(false);
            }
        });
    }

    public void RespondFail(string reason) {
        SetHandler(async (socket, cmd) => {
            byte[] reasonBytes = Encoding.ASCII.GetBytes(reason);
            byte[] header = Encoding.ASCII.GetBytes($"FAIL{reasonBytes.Length:X4}");
            await socket.SendAsync(header, SocketFlags.None).ConfigureAwait(false);
            await socket.SendAsync(reasonBytes, SocketFlags.None).ConfigureAwait(false);
        });
    }

    private async Task AcceptLoopAsync() {
        while (!_cts.IsCancellationRequested) {
            Socket socket;
            try {
                socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                break;
            }
            catch (SocketException) {
                break;
            }
            catch (ObjectDisposedException) {
                break;
            }

            lock (_lock) {
                _activeSockets.Add(socket);
            }

            _ = Task.Run(() => HandleClientAsync(socket), _cts.Token);
        }
    }

    private async Task HandleClientAsync(Socket socket) {
        try {
            using var stream = new NetworkStream(socket, ownsSocket: false);

            while (!_cts.IsCancellationRequested && socket.Connected) {
                // Read 4-hex length
                byte[] lenBuffer = new byte[4];
                int totalLenRead = 0;
                while (totalLenRead < 4) {
                    int r = await stream.ReadAsync(lenBuffer.AsMemory(totalLenRead, 4 - totalLenRead), _cts.Token).ConfigureAwait(false);
                    if (r == 0) {
                        return; // Clean client disconnect
                    }
                    totalLenRead += r;
                }

                string hexStr = Encoding.ASCII.GetString(lenBuffer);
                if (!int.TryParse(hexStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cmdLen)) {
                    return;
                }

                byte[] cmdBuffer = new byte[cmdLen];
                int totalCmdRead = 0;
                while (totalCmdRead < cmdLen) {
                    int r = await stream.ReadAsync(cmdBuffer.AsMemory(totalCmdRead, cmdLen - totalCmdRead), _cts.Token).ConfigureAwait(false);
                    if (r == 0) {
                        return;
                    }
                    totalCmdRead += r;
                }

                string cmd = Encoding.ASCII.GetString(cmdBuffer);

                // Invariant: never receive host:kill
                Assert.False(cmd.StartsWith("host:kill", StringComparison.OrdinalIgnoreCase), "AdbView must NEVER send host:kill (AGENTS.md R-14).");

                lock (_lock) {
                    _receivedCommands.Add(cmd);
                }

                Func<Socket, string, Task>? handler;
                lock (_lock) {
                    handler = _handler;
                }

                if (handler != null) {
                    await handler(socket, cmd).ConfigureAwait(false);
                }
                else {
                    // Default fallback: return OKAY + 0000
                    await socket.SendAsync("OKAY0000"u8.ToArray(), SocketFlags.None, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException) {
            // Expected socket/client lifecycle events
        }
        finally {
            lock (_lock) {
                _activeSockets.Remove(socket);
            }
            try {
                socket.Dispose();
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void AssertNoKillCommandSent() {
        lock (_lock) {
            foreach (var cmd in _receivedCommands) {
                Assert.False(cmd.Contains("kill", StringComparison.OrdinalIgnoreCase), $"AdbView sent forbidden kill command: '{cmd}'");
            }
        }
    }

    /// <summary>
    /// Two-step metadata shell: <c>host:transport:SERIAL</c> then <c>shell:…</c> → OKAY + raw body + close.
    /// Tracks the transport serial per socket so wrong-device selection is detectable.
    /// </summary>
    public void RespondDeviceShell(Func<string, string, FakeDeviceShellPlan> planFactory) {
        var transportBySocket = new Dictionary<Socket, string>();
        SetHandler(async (socket, cmd) => {
            if (cmd.StartsWith("host:transport:", StringComparison.Ordinal)) {
                string serial = cmd["host:transport:".Length..];
                lock (_lock) {
                    transportBySocket[socket] = serial;
                }

                // Peek plan with a placeholder shell to allow transport-only FAIL.
                var transportPlan = planFactory(serial, "");
                if (!transportPlan.TransportOk) {
                    await SendFailAsync(socket, transportPlan.FailReason ?? "device not found").ConfigureAwait(false);
                    return;
                }

                await socket.SendAsync("OKAY"u8.ToArray(), SocketFlags.None).ConfigureAwait(false);
                return;
            }

            if (!cmd.StartsWith("shell:", StringComparison.Ordinal)) {
                await SendFailAsync(socket, "unsupported").ConfigureAwait(false);
                return;
            }

            string? serialForShell;
            lock (_lock) {
                transportBySocket.TryGetValue(socket, out serialForShell);
            }

            if (string.IsNullOrEmpty(serialForShell)) {
                await SendFailAsync(socket, "no transport").ConfigureAwait(false);
                return;
            }

            var plan = planFactory(serialForShell, cmd);
            if (!plan.TransportOk) {
                await SendFailAsync(socket, plan.FailReason ?? "device not found").ConfigureAwait(false);
                return;
            }

            if (!plan.ShellOk) {
                await SendFailAsync(socket, plan.FailReason ?? "shell failed").ConfigureAwait(false);
                return;
            }

            await socket.SendAsync("OKAY"u8.ToArray(), SocketFlags.None).ConfigureAwait(false);

            if (plan.HangAfterShellOkay) {
                await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
                return;
            }

            byte[] body = plan.Body ?? Array.Empty<byte>();
            if (body.Length > 0) {
                await socket.SendAsync(body, SocketFlags.None).ConfigureAwait(false);
            }

            // Classic shell: EOF ends the stream (close write side of this connection).
            socket.Shutdown(SocketShutdown.Send);
        });
    }

    private static async Task SendFailAsync(Socket socket, string reason) {
        byte[] reasonBytes = Encoding.ASCII.GetBytes(reason);
        byte[] header = Encoding.ASCII.GetBytes($"FAIL{reasonBytes.Length:X4}");
        await socket.SendAsync(header, SocketFlags.None).ConfigureAwait(false);
        await socket.SendAsync(reasonBytes, SocketFlags.None).ConfigureAwait(false);
    }

    public void Dispose() {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        try {
            _listener.Stop();
            _listener.Dispose();
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }

        lock (_lock) {
            foreach (var socket in _activeSockets) {
                try { socket.Dispose(); } catch (SocketException) { } catch (ObjectDisposedException) { }
            }
            _activeSockets.Clear();
        }

        _cts.Dispose();
    }

    public async ValueTask DisposeAsync() {
        Dispose();
        try {
            await _acceptLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }
}

/// <summary>Plan for one FakeAdb metadata shell interaction.</summary>
internal sealed class FakeDeviceShellPlan {
    public bool TransportOk { get; init; } = true;
    public bool ShellOk { get; init; } = true;
    public byte[]? Body { get; init; }
    public string? FailReason { get; init; }
    public bool HangAfterShellOkay { get; init; }

    public static FakeDeviceShellPlan Ok(byte[] body) => new() { Body = body };
    public static FakeDeviceShellPlan OkText(string text) => Ok(Encoding.UTF8.GetBytes(text));
    public static FakeDeviceShellPlan TransportFail(string reason) =>
        new() { TransportOk = false, FailReason = reason };
    public static FakeDeviceShellPlan ShellFail(string reason) =>
        new() { ShellOk = false, FailReason = reason };
    public static FakeDeviceShellPlan Hang() => new() { HangAfterShellOkay = true };
}
