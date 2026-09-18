namespace AdbView.Adb.Protocol;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal interface IAdbHostConnection : IAsyncDisposable, IDisposable {
    Task<ReadOnlyMemory<byte>> ExecuteOneShotAsync(string service, CancellationToken ct = default);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ExecuteFramedStreamAsync(string service, CancellationToken ct = default);
}

internal sealed class AdbHostConnection : IAdbHostConnection {
    public static readonly EndPoint DefaultEndPoint = new IPEndPoint(IPAddress.Loopback, 5037);
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly EndPoint _endPoint;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxFrameLength;

    private Socket? _socket;
    private NetworkStream? _networkStream;
    private bool _isDisposed;

    public EndPoint EndPoint => _endPoint;

    public AdbHostConnection(
        EndPoint? endPoint = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null,
        int maxFrameLength = AdbFraming.MaxFrameLength) {
        _endPoint = endPoint ?? DefaultEndPoint;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _maxFrameLength = maxFrameLength;
    }

    public async Task<ReadOnlyMemory<byte>> ExecuteOneShotAsync(string service, CancellationToken ct = default) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(service);

        using var timeoutCts = new CancellationTokenSource(_requestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            var stream = await EnsureConnectedAsync(linkedCts.Token).ConfigureAwait(false);

            byte[] requestBytes = AdbFraming.EncodeRequest(service);
            await stream.WriteAsync(requestBytes, linkedCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(linkedCts.Token).ConfigureAwait(false);

            byte[] statusBuffer = new byte[AdbFraming.HeaderLength];
            await ReadExactAsync(stream, statusBuffer, linkedCts.Token).ConfigureAwait(false);
            var status = AdbFraming.ParseStatus(statusBuffer);

            if (status == AdbStatus.Fail) {
                byte[] errorLenBuffer = new byte[AdbFraming.HeaderLength];
                await ReadExactAsync(stream, errorLenBuffer, linkedCts.Token).ConfigureAwait(false);
                int errorLength = AdbFraming.ParseHexLength(errorLenBuffer, _maxFrameLength);

                byte[] errorBuffer = new byte[errorLength];
                await ReadExactAsync(stream, errorBuffer, linkedCts.Token).ConfigureAwait(false);
                string reason = Encoding.ASCII.GetString(errorBuffer);
                throw new AdbProtocolException(
                    AdbErrorCode.CommandFailed,
                    $"ADB command '{service}' failed: {reason}",
                    serverReason: reason);
            }

            // Status is OKAY
            byte[] payloadLenBuffer = new byte[AdbFraming.HeaderLength];
            await ReadExactAsync(stream, payloadLenBuffer, linkedCts.Token).ConfigureAwait(false);
            int payloadLength = AdbFraming.ParseHexLength(payloadLenBuffer, _maxFrameLength);

            if (payloadLength == 0) {
                return ReadOnlyMemory<byte>.Empty;
            }

            byte[] payloadBuffer = new byte[payloadLength];
            await ReadExactAsync(stream, payloadBuffer, linkedCts.Token).ConfigureAwait(false);
            return payloadBuffer;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            throw new AdbConnectionException(
                $"ADB request '{service}' to {_endPoint} timed out after {_requestTimeout.TotalSeconds:0.##}s.",
                _endPoint,
                isTimeout: true);
        }
        catch (AdbException) {
            throw;
        }
        catch (SocketException ex) {
            throw new AdbConnectionException(
                $"Socket error during ADB request '{service}' to {_endPoint}: {ex.Message}",
                _endPoint,
                isTimeout: false,
                socketError: ex.SocketErrorCode,
                innerException: ex);
        }
        catch (IOException ex) when (ex.InnerException is SocketException sex) {
            throw new AdbConnectionException(
                $"I/O socket error during ADB request '{service}' to {_endPoint}: {sex.Message}",
                _endPoint,
                isTimeout: false,
                socketError: sex.SocketErrorCode,
                innerException: ex);
        }
        finally {
            await DisposeSocketAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ExecuteFramedStreamAsync(
        string service,
        [EnumeratorCancellation] CancellationToken ct = default) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(service);

        NetworkStream stream;
        using (var handshakeTimeoutCts = new CancellationTokenSource(_requestTimeout))
        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, handshakeTimeoutCts.Token)) {
            try {
                stream = await EnsureConnectedAsync(linkedCts.Token).ConfigureAwait(false);

                byte[] requestBytes = AdbFraming.EncodeRequest(service);
                await stream.WriteAsync(requestBytes, linkedCts.Token).ConfigureAwait(false);
                await stream.FlushAsync(linkedCts.Token).ConfigureAwait(false);

                byte[] statusBuffer = new byte[AdbFraming.HeaderLength];
                await ReadExactAsync(stream, statusBuffer, linkedCts.Token).ConfigureAwait(false);
                var status = AdbFraming.ParseStatus(statusBuffer);

                if (status == AdbStatus.Fail) {
                    byte[] errorLenBuffer = new byte[AdbFraming.HeaderLength];
                    await ReadExactAsync(stream, errorLenBuffer, linkedCts.Token).ConfigureAwait(false);
                    int errorLength = AdbFraming.ParseHexLength(errorLenBuffer, _maxFrameLength);

                    byte[] errorBuffer = new byte[errorLength];
                    await ReadExactAsync(stream, errorBuffer, linkedCts.Token).ConfigureAwait(false);
                    string reason = Encoding.ASCII.GetString(errorBuffer);
                    await DisposeSocketAsync().ConfigureAwait(false);
                    throw new AdbProtocolException(
                        AdbErrorCode.CommandFailed,
                        $"ADB stream request '{service}' failed: {reason}",
                        serverReason: reason);
                }
            }
            catch (OperationCanceledException) when (handshakeTimeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) {
                await DisposeSocketAsync().ConfigureAwait(false);
                throw new AdbConnectionException(
                    $"ADB handshake for stream '{service}' to {_endPoint} timed out after {_requestTimeout.TotalSeconds:0.##}s.",
                    _endPoint,
                    isTimeout: true);
            }
            catch (AdbException) {
                await DisposeSocketAsync().ConfigureAwait(false);
                throw;
            }
            catch (SocketException ex) {
                await DisposeSocketAsync().ConfigureAwait(false);
                throw new AdbConnectionException(
                    $"Socket error during stream handshake '{service}' to {_endPoint}: {ex.Message}",
                    _endPoint,
                    isTimeout: false,
                    socketError: ex.SocketErrorCode,
                    innerException: ex);
            }
            catch (IOException ex) when (ex.InnerException is SocketException sex) {
                await DisposeSocketAsync().ConfigureAwait(false);
                throw new AdbConnectionException(
                    $"I/O socket error during stream handshake '{service}' to {_endPoint}: {sex.Message}",
                    _endPoint,
                    isTimeout: false,
                    socketError: sex.SocketErrorCode,
                    innerException: ex);
            }
        }

        // Streaming phase: no idle timeout (ENGINEERING.md §6.1).
        // The try/finally ensures the underlying socket connection is closed when enumeration completes,
        // is cancelled, or the enumerator is disposed by the caller.
        try {
            byte[] chunkLenBuffer = new byte[AdbFraming.HeaderLength];
            while (true) {
                ct.ThrowIfCancellationRequested();

                bool hasChunk;
                try {
                    hasChunk = await TryReadExactOrEofAsync(stream, chunkLenBuffer, ct).ConfigureAwait(false);
                }
                catch (SocketException ex) {
                    throw new AdbConnectionException(
                        $"Socket error while reading stream chunks from {_endPoint}: {ex.Message}",
                        _endPoint,
                        isTimeout: false,
                        socketError: ex.SocketErrorCode,
                        innerException: ex);
                }
                catch (IOException ex) when (ex.InnerException is SocketException sex) {
                    throw new AdbConnectionException(
                        $"I/O socket error while reading stream chunks from {_endPoint}: {sex.Message}",
                        _endPoint,
                        isTimeout: false,
                        socketError: sex.SocketErrorCode,
                        innerException: ex);
                }

                if (!hasChunk) {
                    yield break;
                }

                int chunkLength = AdbFraming.ParseHexLength(chunkLenBuffer, _maxFrameLength);

                if (chunkLength == 0) {
                    yield return ReadOnlyMemory<byte>.Empty;
                    continue;
                }

                byte[] chunkBuffer = new byte[chunkLength];
                try {
                    await ReadExactAsync(stream, chunkBuffer, ct).ConfigureAwait(false);
                }
                catch (SocketException ex) {
                    throw new AdbConnectionException(
                        $"Socket error while reading stream chunk payload from {_endPoint}: {ex.Message}",
                        _endPoint,
                        isTimeout: false,
                        socketError: ex.SocketErrorCode,
                        innerException: ex);
                }
                catch (IOException ex) when (ex.InnerException is SocketException sex) {
                    throw new AdbConnectionException(
                        $"I/O socket error while reading stream chunk payload from {_endPoint}: {sex.Message}",
                        _endPoint,
                        isTimeout: false,
                        socketError: sex.SocketErrorCode,
                        innerException: ex);
                }

                yield return chunkBuffer;
            }
        }
        finally {
            await DisposeSocketAsync().ConfigureAwait(false);
        }
    }

    private async Task<NetworkStream> EnsureConnectedAsync(CancellationToken ct) {
        ThrowIfDisposed();

        if (_networkStream != null && _socket != null && _socket.Connected) {
            return _networkStream;
        }

        await DisposeSocketAsync().ConfigureAwait(false);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) {
            NoDelay = true
        };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        using var timeoutCts = new CancellationTokenSource(_connectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            await socket.ConnectAsync(_endPoint, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            socket.Dispose();
            throw new AdbConnectionException(
                $"Connection to ADB server at {_endPoint} timed out after {_connectTimeout.TotalSeconds:0.##}s.",
                _endPoint,
                isTimeout: true);
        }
        catch (SocketException ex) {
            socket.Dispose();
            throw new AdbConnectionException(
                $"Failed to connect to ADB server at {_endPoint}: {ex.Message}",
                _endPoint,
                isTimeout: false,
                socketError: ex.SocketErrorCode,
                innerException: ex);
        }
        catch (Exception) {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        _networkStream = new NetworkStream(socket, ownsSocket: true);
        return _networkStream;
    }

    private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct) {
        int total = 0;
        while (total < buffer.Length) {
            int read = await stream.ReadAsync(buffer.Slice(total), ct).ConfigureAwait(false);
            if (read == 0) {
                throw new AdbProtocolException(
                    AdbErrorCode.UnexpectedEof,
                    $"Unexpected EOF after reading {total} of {buffer.Length} expected bytes.");
            }
            total += read;
        }
    }

    private static async ValueTask<bool> TryReadExactOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken ct) {
        int total = 0;
        while (total < buffer.Length) {
            int read = await stream.ReadAsync(buffer.Slice(total), ct).ConfigureAwait(false);
            if (read == 0) {
                if (total == 0) {
                    return false; // Normal EOF at frame boundary
                }
                throw new AdbProtocolException(
                    AdbErrorCode.UnexpectedEof,
                    $"Unexpected EOF after reading {total} of {buffer.Length} expected bytes.");
            }
            total += read;
        }
        return true;
    }

    private async ValueTask DisposeSocketAsync() {
        if (_networkStream != null) {
            await _networkStream.DisposeAsync().ConfigureAwait(false);
            _networkStream = null;
        }
        if (_socket != null) {
            _socket.Dispose();
            _socket = null;
        }
    }

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    public void Dispose() {
        if (_isDisposed) return;
        _isDisposed = true;

        _networkStream?.Dispose();
        _networkStream = null;
        _socket?.Dispose();
        _socket = null;
    }

    public async ValueTask DisposeAsync() {
        if (_isDisposed) return;
        _isDisposed = true;

        await DisposeSocketAsync().ConfigureAwait(false);
    }
}
