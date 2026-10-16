namespace AdbView.Host;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb;
using AdbView.State;
using Serilog;

/// <summary>Elevated (or plain) process launch seam — fire-and-observe; output is never parsed
/// (research/2026-10-01-repair-actions-and-verify.md §Findings 4).</summary>
internal enum ProcessLaunchOutcome { Launched, Declined, Failed }

internal sealed record ProcessLaunchResult(ProcessLaunchOutcome Outcome, int? ExitCode, int? NativeError);

internal interface IElevatedProcessLauncher {
    Task<ProcessLaunchResult> LaunchAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>
/// Real launcher: one UAC consent per fixed command line (runas verb). UseShellExecute is
/// required for the verb, so stdio cannot be redirected — outcomes come from observing system
/// state, not from parsing output.
/// </summary>
internal sealed class WindowsElevatedProcessLauncher : IElevatedProcessLauncher {
    internal static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);

    public async Task<ProcessLaunchResult> LaunchAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct) {
        var psi = new ProcessStartInfo {
            FileName = fileName,
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (string argument in arguments) {
            psi.ArgumentList.Add(argument);
        }

        try {
            using var process = Process.Start(psi);
            if (process is null) {
                return new ProcessLaunchResult(ProcessLaunchOutcome.Failed, null, null);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ExitWait);
            try {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return new ProcessLaunchResult(ProcessLaunchOutcome.Launched, process.ExitCode, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                // Elevating too long — failed rather than blocking the card forever.
                return new ProcessLaunchResult(ProcessLaunchOutcome.Failed, null, null);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) {
            // ERROR_CANCELLED: the user declined the elevation prompt. A deliberate no-op.
            Log.Information("Elevated repair declined by user ({File}).", fileName);
            return new ProcessLaunchResult(ProcessLaunchOutcome.Declined, null, ex.NativeErrorCode);
        }
        catch (Win32Exception ex) {
            Log.Warning(ex, "Elevated repair launch failed ({File}, error {Code}).", fileName, ex.NativeErrorCode);
            return new ProcessLaunchResult(ProcessLaunchOutcome.Failed, null, ex.NativeErrorCode);
        }
    }
}

/// <summary>Read-only service observation (no elevation): sc.exe query exit codes.</summary>
internal interface IServiceStateSource {
    /// <summary>True when the named service exists and is running; false when stopped/absent; null when unknown.</summary>
    Task<bool?> IsServiceRunningAsync(string serviceName, CancellationToken ct);
}

internal sealed class ScServiceStateSource : IServiceStateSource {
    public async Task<bool?> IsServiceRunningAsync(string serviceName, CancellationToken ct) {
        try {
            var psi = new ProcessStartInfo {
                FileName = "sc.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("query");
            psi.ArgumentList.Add(serviceName);

            using var process = Process.Start(psi);
            if (process is null) {
                return null;
            }

            string output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0) {
                return false; // 1060 = not installed; any other failure = not running.
            }

            return output.Contains("RUNNING", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) {
            throw;
        }
#pragma warning disable CA1031 // R-08: any query failure is logged AND reported as "unknown" (null).
        catch (Exception ex) {
            Log.Warning(ex, "Service state query failed for {Service}.", serviceName);
            return null;
        }
#pragma warning restore CA1031
    }
}

/// <summary>
/// Owns the closed repair action set of decision 0008 (amendment): approval-gated, evidence-gated,
/// verify-and-report. Every command line is fixed except a charset-validated adapter/service
/// target; the frontend only ever sends opaque ids.
/// </summary>
internal sealed class RepairCoordinator {
    internal const string TailscaleServiceName = "Tailscale";

    /// <summary>Characters permitted in adapter/service targets on a command line (amendment).</summary>
    internal static readonly char[] AllowedTargetChars = [' ', '(', ')', '-', '.', '_', '\''];

    private readonly IAdbProcessRunner _processRunner;
    private readonly IAdbExecutableLocator _executableLocator;
    private readonly INetworkDiagnosticsSource _diagnosticsSource;
    private readonly IElevatedProcessLauncher _launcher;
    private readonly IServiceStateSource _serviceState;
    private readonly Action<AdbViewEvent> _post;
    private readonly Func<Task> _runDiscoveryCheck;

    private ImmutableArray<DiscoveryRepairDescriptor> _catalog =
        ImmutableArray<DiscoveryRepairDescriptor>.Empty;

    // Applied-but-not-reverted actions unlock the existing undo entries for this session.
    private readonly HashSet<string> _stoppedServices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabledAdapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _bookkeepingGate = new();

    // Restart recovery: armed when Restart ADB completes; fires the re-measure once, on the
    // first mDNS frame of the new server session (event-driven; R-05).
    private long _preKillGeneration;
    private DateTimeOffset _restartCompletedAt = DateTimeOffset.MinValue;
    private bool _awaitingRestartMeasure;
    private long _lastMdnsSequence;
    private long _restartMdnsSequence;
    private int _busy;

    public RepairCoordinator(
        IAdbProcessRunner processRunner,
        IAdbExecutableLocator executableLocator,
        INetworkDiagnosticsSource diagnosticsSource,
        IElevatedProcessLauncher launcher,
        IServiceStateSource serviceState,
        Action<AdbViewEvent> post,
        Func<Task> runDiscoveryCheck) {
        _processRunner = processRunner;
        _executableLocator = executableLocator;
        _diagnosticsSource = diagnosticsSource;
        _launcher = launcher;
        _serviceState = serviceState;
        _post = post;
        _runDiscoveryCheck = runDiscoveryCheck;
    }

    internal static bool IsValidTarget(string target) {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 64) {
            return false;
        }

        foreach (char c in target) {
            if (!(char.IsAsciiLetterOrDigit(c) || Array.IndexOf(AllowedTargetChars, c) >= 0)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rebuilds the evidence-gated offer set. Called when the card appears and after each action.</summary>
    public async Task RebuildCatalogAsync(CancellationToken ct) {
        var builder = ImmutableArray.CreateBuilder<DiscoveryRepairDescriptor>();

        // Restart ADB: always offered while the card is visible (adb resolution happens at run time).
        builder.Add(new DiscoveryRepairDescriptor("restart-adb", DiscoveryRepairActionKind.RestartAdb, null));

        // Admin repairs only for measured findings (decision 0008 amendment).
        bool? tailscaleRunning = await _serviceState
            .IsServiceRunningAsync(TailscaleServiceName, ct)
            .ConfigureAwait(false);
        if (tailscaleRunning == true) {
            builder.Add(new DiscoveryRepairDescriptor(
                "stop-service:" + TailscaleServiceName,
                DiscoveryRepairActionKind.StopService,
                TailscaleServiceName));
        }
        else if (WasServiceStopped(TailscaleServiceName)) {
            // Stopped by us this session — offer the undo even though it no longer "runs".
            builder.Add(new DiscoveryRepairDescriptor(
                "start-service:" + TailscaleServiceName,
                DiscoveryRepairActionKind.StartService,
                TailscaleServiceName));
        }

        var presentAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NetworkAdapterProbeInfo adapter in _diagnosticsSource.GetOperationalAdapters()) {
            if (!DiscoveryDiagnostics.MatchesVirtualAdapter(adapter) || !IsValidTarget(adapter.Name)) {
                continue;
            }

            presentAdapters.Add(adapter.Name);
            builder.Add(new DiscoveryRepairDescriptor(
                "disable-adapter:" + adapter.Name,
                DiscoveryRepairActionKind.DisableAdapter,
                adapter.Name));
        }

        // Disabled adapters vanish from the Up-enumeration; the undo entry keeps them reachable.
        string[] disabledAdapters;
        lock (_bookkeepingGate) {
            disabledAdapters = _disabledAdapters.ToArray();
        }
        foreach (string disabled in disabledAdapters) {
            if (!presentAdapters.Contains(disabled) && IsValidTarget(disabled)) {
                builder.Add(new DiscoveryRepairDescriptor(
                    "enable-adapter:" + disabled,
                    DiscoveryRepairActionKind.EnableAdapter,
                    disabled));
            }
        }

        _catalog = builder.ToImmutable();
        _post(new DiscoveryRepairCatalogEvent(_catalog));
    }

    /// <summary>Runs the repair identified by <paramref name="repairId"/> (opaque id from the card).</summary>
    public async Task RunAsync(string repairId, long currentServerGeneration, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(repairId);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) {
            return;
        }

        bool started = false;
        bool finished = false;
        try {
            var descriptor = _catalog.FirstOrDefault(r => r.Id == repairId);
            if (descriptor is null) {
                Log.Warning("Repair request for unknown id {RepairId} ignored.", repairId);
                return;
            }

            _post(new DiscoveryRepairStartedEvent(repairId));
            started = true;
            var outcome = descriptor.Kind switch {
                DiscoveryRepairActionKind.RestartAdb => await RestartAdbAsync(currentServerGeneration, ct).ConfigureAwait(false),
                DiscoveryRepairActionKind.StopService => await LaunchAdminAsync(
                    "sc.exe", ["stop", descriptor.Target!], ct).ConfigureAwait(false),
                DiscoveryRepairActionKind.StartService => await LaunchAdminAsync(
                    "sc.exe", ["start", descriptor.Target!], ct).ConfigureAwait(false),
                DiscoveryRepairActionKind.DisableAdapter => await LaunchAdminAsync(
                    "netsh.exe",
                    ["interface", "set", "interface", "name=" + descriptor.Target!, "admin=disable"],
                    ct).ConfigureAwait(false),
                DiscoveryRepairActionKind.EnableAdapter => await LaunchAdminAsync(
                    "netsh.exe",
                    ["interface", "set", "interface", "name=" + descriptor.Target!, "admin=enable"],
                    ct).ConfigureAwait(false),
                _ => DiscoveryRepairOutcome.Failed,
            };

            _post(new DiscoveryRepairFinishedEvent(repairId, outcome));
            finished = true;

            if (outcome == DiscoveryRepairOutcome.Completed) {
                lock (_bookkeepingGate) {
                    switch (descriptor.Kind) {
                        case DiscoveryRepairActionKind.StopService:
                            _stoppedServices.Add(descriptor.Target!);
                            break;
                        case DiscoveryRepairActionKind.StartService:
                            _stoppedServices.Remove(descriptor.Target!);
                            break;
                        case DiscoveryRepairActionKind.DisableAdapter:
                            _disabledAdapters.Add(descriptor.Target!);
                            break;
                        case DiscoveryRepairActionKind.EnableAdapter:
                            _disabledAdapters.Remove(descriptor.Target!);
                            break;
                    }
                }

                // Undo actions unlock; completed primary actions leave the card.
                await RebuildCatalogAsync(ct).ConfigureAwait(false);
                if (descriptor.Kind is DiscoveryRepairActionKind.StopService
                    or DiscoveryRepairActionKind.DisableAdapter) {
                    await _runDiscoveryCheck().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            Log.Warning(ex, "Discovery repair failed ({RepairId}).", repairId);
            if (started && !finished) {
                _post(new DiscoveryRepairFinishedEvent(repairId, DiscoveryRepairOutcome.Failed));
            }
        }
        finally {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// AppHost forwards supervisor Up states. After a Restart ADB, the first generation above the
    /// pre-kill one means the server is back; the re-measure then waits (once) for the first mDNS
    /// frame of the new session via <see cref="OnSnapshotPublished"/>.
    /// </summary>
    public void OnServerUp(long generation) {
        if (!_awaitingServerRestart || generation <= _preKillGeneration) {
            return;
        }

        _awaitingServerRestart = false;
        _awaitingRestartMeasure = true;
        Log.Information(
            "Repair recovery: server generation {Generation} up after restart; awaiting first mDNS frame.",
            generation);
    }

    /// <summary>
    /// AppHost forwards snapshot publishes; fires the post-restart re-measure exactly once, on the
    /// first mDNS frame that arrives after the server came back (checked before the card-clear
    /// reset — the restart itself may transiently clear the card). Card-clear resets session
    /// repair bookkeeping (fresh episode, fresh catalog).
    /// </summary>
    public void OnSnapshotPublished(AppSnapshot snapshot, DeviceEngineState deviceState) {
        _lastMdnsSequence = deviceState.LatestMdnsSequence;
        if (_awaitingRestartMeasure
            && (deviceState.LatestMdnsSequence > 0
                ? deviceState.LatestMdnsSequence > _restartMdnsSequence
                : deviceState.LatestAppliedMdnsAt is DateTimeOffset frame && frame >= _restartCompletedAt)) {
            _awaitingRestartMeasure = false;
            _ = MeasureAfterRestartAsync();
        }

        if (snapshot.DiscoveryHealth is not null) {
            return;
        }

        // Card cleared: reset the episode's offer set, retaining undo bookkeeping. The restart-recovery
        // flags are NOT touched — during a restart the card legitimately vanishes (server down →
        // Restoring), and the one-shot post-restart measure must still fire.
        if (_catalog.IsEmpty) {
            return;
        }

        _catalog = ImmutableArray<DiscoveryRepairDescriptor>.Empty;
    }

    private async Task MeasureAfterRestartAsync() {
        try {
            await _runDiscoveryCheck().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            Log.Warning(ex, "Discovery repair verification failed.");
        }
    }

    private bool _awaitingServerRestart;

    private async Task<DiscoveryRepairOutcome> RestartAdbAsync(long currentServerGeneration, CancellationToken ct) {
        string? adb = _executableLocator.Locate();
        if (adb is null) {
            Log.Warning("Restart ADB not run: adb.exe not found.");
            return DiscoveryRepairOutcome.Failed;
        }

        // Decision 0008 amendment: the single sanctioned kill. Recovery is the supervisor's.
        var result = await _processRunner
            .KillServerAsync(adb, ct)
            .ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0) {
            Log.Warning("Restart ADB failed (exit {ExitCode}, timeout {TimedOut}).", result.ExitCode, result.TimedOut);
            return DiscoveryRepairOutcome.Failed;
        }

        _preKillGeneration = currentServerGeneration;
        _restartCompletedAt = App.SystemClock.UtcNow;
        _restartMdnsSequence = _lastMdnsSequence;
        _awaitingServerRestart = true;
        return DiscoveryRepairOutcome.Completed;
    }

    private async Task<DiscoveryRepairOutcome> LaunchAdminAsync(
        string fileName,
        string[] arguments,
        CancellationToken ct) {
        var result = await _launcher.LaunchAsync(fileName, arguments, ct).ConfigureAwait(false);
        return result.Outcome switch {
            ProcessLaunchOutcome.Launched when result.ExitCode == 0 => DiscoveryRepairOutcome.Completed,
            ProcessLaunchOutcome.Declined => DiscoveryRepairOutcome.Declined,
            _ => DiscoveryRepairOutcome.Failed,
        };
    }

    private bool WasServiceStopped(string service) {
        lock (_bookkeepingGate) {
            return _stoppedServices.Contains(service);
        }
    }
}
