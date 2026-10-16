namespace AdbView.Adb.Protocol;

using System;
using System.Net;
using System.Net.Sockets;

public enum AdbErrorCode {
    InvalidStatus,
    MalformedLength,
    PayloadTooLarge,
    UnexpectedEof,
    CommandFailed,
    InvalidRequest
}

public abstract class AdbException : Exception {
    protected AdbException() { }
    protected AdbException(string message) : base(message) { }
    protected AdbException(string message, Exception? innerException) : base(message, innerException) { }
}

public sealed class AdbConnectionException : AdbException {
    public EndPoint? EndPoint { get; }
    public SocketError? SocketError { get; }
    public bool IsTimeout { get; }

    public AdbConnectionException() { }

    public AdbConnectionException(string message) : base(message) { }

    public AdbConnectionException(string message, Exception? innerException) : base(message, innerException) { }

    public AdbConnectionException(
        string message,
        EndPoint? endPoint,
        bool isTimeout,
        SocketError? socketError = null,
        Exception? innerException = null)
        : base(message, innerException) {
        EndPoint = endPoint;
        IsTimeout = isTimeout;
        SocketError = socketError;
    }
}

public sealed class AdbProtocolException : AdbException {
    public AdbErrorCode ErrorCode { get; }
    public string? ServerReason { get; }

    public AdbProtocolException() { }

    public AdbProtocolException(string message) : base(message) { }

    public AdbProtocolException(string message, Exception? innerException) : base(message, innerException) { }

    public AdbProtocolException(
        AdbErrorCode errorCode,
        string message,
        string? serverReason = null,
        Exception? innerException = null)
        : base(message, innerException) {
        ErrorCode = errorCode;
        ServerReason = serverReason;
    }
}
