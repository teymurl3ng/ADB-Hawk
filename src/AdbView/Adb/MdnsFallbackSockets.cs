namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Serilog;

/// <summary>
/// Narrow seam over a UDP socket for the fallback browser (testable). Implementations must be
/// safe to dispose from any thread; Receive may return -1 on timeout.
/// </summary>
internal interface IUdpSocket : IDisposable {
    /// <summary>Binds to <paramref name="endPoint"/>; when <paramref name="reuse"/> the socket sets SO_REUSEADDR.</summary>
    void Bind(IPEndPoint endPoint, bool reuse);

    bool IsBound { get; }

    /// <summary>Joins the multicast group on the given (already-bound) socket.</summary>
    void JoinMulticastGroup(IPAddress group, IPAddress interfaceAddress);

    void SetMulticastInterface(IPAddress interfaceAddress);

    void SendTo(ReadOnlySpan<byte> packet, IPEndPoint destination);

    /// <summary>Receives one datagram or returns -1 after <paramref name="timeoutMs"/>.</summary>
    int Receive(Span<byte> buffer, int timeoutMs);
}

internal interface IUdpSocketFactory {
    IUdpSocket Create();
}

internal sealed class WindowsUdpSocketFactory : IUdpSocketFactory {
    public IUdpSocket Create() => new WindowsUdpSocket();
}

internal sealed class WindowsUdpSocket : IUdpSocket {
    private Socket? _socket;

    public bool IsBound => _socket is { IsBound: true };

    public void Bind(IPEndPoint endPoint, bool reuse) {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try {
            if (reuse) {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            socket.Bind(endPoint);
            _socket = socket;
        }
        finally {
            if (_socket != socket) {
                socket.Dispose();
            }
        }
    }

    public void JoinMulticastGroup(IPAddress group, IPAddress interfaceAddress) {
        var option = new MulticastOption(group, interfaceAddress);
        _socket?.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
    }

    public void SetMulticastInterface(IPAddress interfaceAddress) {
        var socket = _socket ?? throw new InvalidOperationException("UDP socket is not bound.");
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, interfaceAddress.GetAddressBytes());
    }

    public void SendTo(ReadOnlySpan<byte> packet, IPEndPoint destination) {
        _socket?.SendTo(packet.ToArray(), SocketFlags.None, destination);
    }

    public int Receive(Span<byte> buffer, int timeoutMs) {
        if (_socket is null || !_socket.Poll(timeoutMs * 1000, SelectMode.SelectRead)) {
            return -1;
        }

        try {
            return _socket.Receive(buffer, SocketFlags.None);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.Interrupted) {
            // ICMP port-unreachable storms or interrupted waits — nothing to deliver.
            return -1;
        }
    }

    public void Dispose() {
        try {
            _socket?.Dispose();
        }
#pragma warning disable CA1031 // R-08: dispose failure during teardown is logged; nothing to recover.
        catch (Exception ex) {
            Log.Debug(ex, "Fallback browser socket dispose.");
        }
#pragma warning restore CA1031

        _socket = null;
    }
}
