namespace AdbView.Adb;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.App;

internal sealed class AdbServerSupervisor {
    private readonly Func<IAdbHostConnection> _connectionFactory;
    private readonly IAdbExecutableLocator _locator;
    private readonly IAdbProcessRunner _processRunner;
    private readonly Backoff _backoff;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _upProbeInterval;

    private AdbServerPhase _currentPhase = AdbServerPhase.Probing;
    private AdbServerStatus? _currentStatus;
    private int _currentProtocolVersion;
    private int _generation;
    private string? _currentExecutablePath;
    private DateTimeOffset? _outageStart;
    private AdbServerStateChanged? _lastEmittedState;
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private int _revalidateSession;
    private int _refreshStatus;

    public AdbServerSupervisor(
        Func<IAdbHostConnection>? connectionFactory = null,
        IAdbExecutableLocator? locator = null,
        IAdbProcessRunner? processRunner = null,
        Backoff? backoff = null,
        Func<DateTimeOffset>? getUtcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? upProbeInterval = null) {
        _connectionFactory = connectionFactory ?? (() => new AdbHostConnection());
        _locator = locator ?? new AdbExecutableLocator();
        _processRunner = processRunner ?? new AdbProcessRunner();
        _backoff = backoff ?? new Backoff();
        _getUtcNow = getUtcNow ?? (() => SystemClock.UtcNow);
        _delay = delay ?? Task.Delay;
        _upProbeInterval = upProbeInterval ?? TimeSpan.FromSeconds(5);

        CurrentState = new AdbServerStateChanged(AdbServerPhase.Probing, null, 0, 0);
    }

    public AdbServerStateChanged CurrentState { get; private set; }

    public int Generation => _generation;

    public event Action<AdbServerStateChanged>? StateChanged;

    /// <summary>Power/session and stream events interrupt the existing probe backoff.</summary>
    public void RequestProbe(bool revalidateSession = true) {
        if (revalidateSession) {
            Interlocked.Exchange(ref _revalidateSession, 1);
        }
        Interlocked.Exchange(ref _refreshStatus, 1);
        _wake.Writer.TryWrite(0);
    }

    public async Task RunAsync(CancellationToken ct = default) {
        ct.ThrowIfCancellationRequested();
        while (!ct.IsCancellationRequested) {
            while (_wake.Reader.TryRead(out _)) { }
            AdbServerStateChanged state;
            try {
                state = await RunIterationAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) {
                Serilog.Log.Error(ex, "ADB supervisor iteration failed; recovery backoff continues.");
                _currentPhase = AdbServerPhase.Unavailable;
                EmitState(new AdbServerStateChanged(_currentPhase, null, 0, _generation, Reason: "Supervisor iteration failed"));
                state = CurrentState;
            }

            var delay = state.Phase == AdbServerPhase.Up
                ? _upProbeInterval
                : _backoff.NextDelay();

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var elapsed = _delay(delay, waitCts.Token);
            var wake = _wake.Reader.WaitToReadAsync(waitCts.Token).AsTask();
            await Task.WhenAny(elapsed, wake).ConfigureAwait(false);
            await waitCts.CancelAsync().ConfigureAwait(false);
            try {
                await Task.WhenAll(elapsed, wake).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (waitCts.IsCancellationRequested) {
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    public async Task<AdbServerStateChanged> RunIterationAsync(CancellationToken ct = default) {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _revalidateSession, 0) != 0) {
            _currentPhase = AdbServerPhase.Probing;
            _backoff.Reset();
        }
        var probeResult = await TryProbeServerAsync(ct).ConfigureAwait(false);

        if (probeResult.Success) {
            HandleProbeSuccess(probeResult);
            return CurrentState;
        }

        await HandleProbeFailureAsync(ct).ConfigureAwait(false);
        return CurrentState;
    }

    private async Task<(bool Success, int Version, AdbServerStatus? Status)> TryProbeServerAsync(CancellationToken ct) {
        bool refreshStatus = Interlocked.Exchange(ref _refreshStatus, 0) != 0;
        try {
            var connection = _connectionFactory();
            await using (connection.ConfigureAwait(false)) {
                int version = await AdbCapabilities.GetVersionAsync(connection, ct).ConfigureAwait(false);

                AdbServerStatus? status = null;
                // Query server-status if this is a new generation or version changed
                if (_currentPhase != AdbServerPhase.Up || version != _currentProtocolVersion || _generation == 0
                    || refreshStatus) {
                    try {
                        status = await AdbCapabilities.GetServerStatusAsync(connection, ct).ConfigureAwait(false);
                    }
                    catch (AdbProtocolException ex) when (ex.ErrorCode == AdbErrorCode.CommandFailed) {
                        // Older adb server without server-status command support
                        status = null;
                    }
                }
                else {
                    // Retain current status across liveness checks in the same generation
                    status = _currentStatus;
                }

                return (true, version, status);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) when (ex is AdbException or System.Net.Sockets.SocketException or TimeoutException or System.IO.IOException or OperationCanceledException) {
            return (false, 0, null);
        }
    }

    private void HandleProbeSuccess((bool Success, int Version, AdbServerStatus? Status) probe) {
        _outageStart = null;
        _backoff.Reset();

        bool isNewGeneration = _currentPhase != AdbServerPhase.Up ||
                               probe.Version != _currentProtocolVersion ||
                               (_currentStatus is not null && probe.Status is not null
                                && (probe.Status.Version != _currentStatus.Version || probe.Status.ExecutablePath != _currentStatus.ExecutablePath)) ||
                               _generation == 0;

        if (isNewGeneration) {
            _generation++;
            _currentProtocolVersion = probe.Version;
        }
        _currentStatus = probe.Status;

        _currentPhase = AdbServerPhase.Up;
        EmitState(new AdbServerStateChanged(
            AdbServerPhase.Up,
            _currentStatus,
            _currentProtocolVersion,
            _generation,
            _currentExecutablePath));
    }

    private async Task HandleProbeFailureAsync(CancellationToken ct) {
        var now = _getUtcNow();
        _outageStart ??= now;
        var elapsed = now - _outageStart.Value;

        // Executable is discovered only when recovery requires starting the server (ENGINEERING.md §6.4).
        _currentExecutablePath = _locator.Locate();

        if (_currentExecutablePath != null) {
            await HandleOutageWithExecutableAsync(_currentExecutablePath, elapsed, ct).ConfigureAwait(false);
        }
        else {
            HandleOutageWithoutExecutable(elapsed);
        }
    }

    private async Task HandleOutageWithExecutableAsync(string executablePath, TimeSpan elapsed, CancellationToken ct) {
        if (elapsed < TimeSpan.FromSeconds(10)) {
            _currentPhase = AdbServerPhase.Restoring;
            EmitState(new AdbServerStateChanged(
                AdbServerPhase.Restoring,
                null,
                0,
                _generation,
                executablePath,
                "Server down; attempting start-server"));
        }

        ProcessResult processResult;
        try {
            processResult = await _processRunner.StartServerAsync(executablePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or InvalidOperationException) {
            Serilog.Log.Warning(ex, "ADB supervisor could not launch recovery process.");
            processResult = new ProcessResult(-1, "", "ADB process launch failed", false);
        }

        if (processResult.ExitCode == 0) {
            // Immediate reprobe following successful start-server
            var reprobe = await TryProbeServerAsync(ct).ConfigureAwait(false);
            if (reprobe.Success) {
                HandleProbeSuccess(reprobe);
                return;
            }
        }

        // Process failed or immediate reprobe failed; evaluate outage duration against the 10-second threshold
        var updatedElapsed = _getUtcNow() - _outageStart!.Value;
        if (updatedElapsed >= TimeSpan.FromSeconds(10)) {
            _currentPhase = AdbServerPhase.Unavailable;
            string reason = processResult.TimedOut
                ? "start-server timed out after 20 seconds"
                : $"start-server failed with exit code {processResult.ExitCode}: {processResult.StandardError}".Trim();

            EmitState(new AdbServerStateChanged(
                AdbServerPhase.Unavailable,
                null,
                0,
                _generation,
                executablePath,
                reason));
        }
        else {
            _currentPhase = AdbServerPhase.Restoring;
            EmitState(new AdbServerStateChanged(
                AdbServerPhase.Restoring,
                null,
                0,
                _generation,
                executablePath,
                "start-server failed; retrying"));
        }
    }

    private void HandleOutageWithoutExecutable(TimeSpan elapsed) {
        if (elapsed < TimeSpan.FromSeconds(10)) {
            _currentPhase = AdbServerPhase.Restoring;
            EmitState(new AdbServerStateChanged(
                AdbServerPhase.Restoring,
                null,
                0,
                _generation,
                null,
                "adb.exe not found; searching"));
        }
        else {
            _currentPhase = AdbServerPhase.Unavailable;
            EmitState(new AdbServerStateChanged(
                AdbServerPhase.Unavailable,
                null,
                0,
                _generation,
                null,
                "adb.exe not found"));
        }
    }

    private void EmitState(AdbServerStateChanged newState) {
        CurrentState = newState;
        if (!newState.Equals(_lastEmittedState)) {
            _lastEmittedState = newState;
            if (StateChanged is not null) {
                foreach (Action<AdbServerStateChanged> observer in StateChanged.GetInvocationList()) {
                    try {
                        observer(newState);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) {
                        Serilog.Log.Error(ex, "ADB supervisor state observer failed.");
                    }
                }
            }
        }
    }
}
