namespace AdbView.WinUiMigration.Host;

using System.IO.Pipes;
using System.Security.Principal;
using Serilog;

internal static class CandidateInstanceIdentity {
    public static string ForUserSession(string userSid, int sessionId) => CandidateOptions.LiveIdentity + "." + userSid + "." + sessionId;
    public static string Current() {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return ForUserSession(identity.User?.Value ?? throw new InvalidOperationException("Candidate user identity unavailable."), process.SessionId);
    }
}

// Fixed local command; never carries arguments, device data or snapshot traffic.
internal sealed class CandidateControlChannel(string pipeName, TimeSpan? requestTimeout = null) : IDisposable {
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _server;
    private int _disposed;
    public void Start(Func<bool> enqueueToggle, Action<Exception> failed) {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_server is not null) { throw new InvalidOperationException("Candidate control server already started."); }
        _server = Task.Run(() => ServeAsync(enqueueToggle, failed, _lifetime.Token));
    }
    private async Task ServeAsync(Func<bool> enqueueToggle, Action<Exception> failed, CancellationToken cancellationToken) {
        try {
            while (!cancellationToken.IsCancellationRequested) {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(2));
                try {
                    var command = new byte[1];
                    await server.ReadExactlyAsync(command, request.Token).ConfigureAwait(false);
                    bool accepted = command[0] == 1 && enqueueToggle();
                    if (!accepted) { Log.Warning("Candidate control request rejected (command {Command})", command[0]); }
                    await server.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, request.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                    Log.Warning("Candidate control client exceeded its request deadline");
                }
                catch (IOException exception) { Log.Warning(exception, "Candidate control client disconnected before completion"); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Log.Debug("Candidate control server canceled during shutdown"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) {
            Log.Error(exception, "Candidate control server failed"); failed(exception);
        }
    }
    public static async Task<bool> SendToggleAsync(string pipeName, TimeSpan timeout) {
        using var deadline = new CancellationTokenSource(timeout);
        try {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await client.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            var response = new byte[1];
            await client.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
            if (response[0] == 1) { return true; }
            Log.Warning("Candidate owner rejected toggle dispatch"); return false;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) {
            Log.Error(exception, "Candidate toggle forwarding failed"); return false;
        }
    }
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _lifetime.Cancel();
        if (_server is not null && !_server.Wait(TimeSpan.FromSeconds(3))) {
            throw new InvalidOperationException("Candidate control server did not stop within its shutdown bound.");
        }
        _lifetime.Dispose();
    }
}
