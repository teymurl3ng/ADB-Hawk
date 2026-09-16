using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AdbView.App;

internal sealed class SingleInstance : IDisposable {
    private const string MutexName = @"Local\AdbView.SingleInstance";
    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly bool _isOwner;
    private CancellationTokenSource? _pipeCts;
    private Task? _pipeServerTask;

    public bool IsOwner => _isOwner;

    // Action invoked when a second instance sends the "toggle" command.
    public Action? OnToggleRequested { get; set; }

    public SingleInstance(int sessionId) {
        _pipeName = $@"AdbView.Control.{sessionId}";
        _mutex = new Mutex(true, MutexName, out _isOwner);
        if (_isOwner) {
            Log.Information("Acquired single instance ownership. Pipe: {PipeName}", _pipeName);
        }
        else {
            Log.Information("Another instance already owns the mutex.");
        }
    }

    public void StartServer() {
        if (!_isOwner) return;

        _pipeCts = new CancellationTokenSource();
        _pipeServerTask = Task.Run(() => ServerLoop(_pipeCts.Token));
    }

    private async Task ServerLoop(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            try {
                using var pipeServer = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous);

                await pipeServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var buffer = new byte[128];
                var bytesRead = await pipeServer.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                var message = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimEnd('\0');

                if (message == "toggle") {
                    Log.Information("Received toggle command from second instance.");
                    OnToggleRequested?.Invoke();
                }
            }
            catch (OperationCanceledException) {
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex) {
                Log.Error(ex, "Error in single instance pipe server loop.");
            }
#pragma warning restore CA1031
        }
    }

    public bool TrySendToggleCommand(int timeoutMs) {
        try {
            using var pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            pipeClient.Connect(timeoutMs);

            var messageBytes = System.Text.Encoding.UTF8.GetBytes("toggle");
            pipeClient.Write(messageBytes, 0, messageBytes.Length);
            pipeClient.Flush();
            return true;
        }
        catch (TimeoutException) {
            Log.Warning("Timeout connecting to the owner's named pipe.");
            return false;
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Error(ex, "Failed to send toggle command to owner.");
            return false;
        }
#pragma warning restore CA1031
    }

    public void Dispose() {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _pipeServerTask?.Wait(500);

        if (_isOwner) {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
