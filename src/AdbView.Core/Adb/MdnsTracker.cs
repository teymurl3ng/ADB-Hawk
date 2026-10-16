namespace AdbView.Adb;

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.App;

/// <summary>
/// Raw ADB mDNS discovery tracker: preferred <c>host:track-mdns-services</c> complete-list
/// stream with generation-scoped CLI fallback to <c>adb.exe mdns track-services --proto-text</c>.
/// Emits discovery-domain events only; no State or Host UI wiring (Phase 6A).
/// </summary>
internal sealed class MdnsTracker {
    public const string PreferredService = "host:track-mdns-services";
    public const string OneShotService = "host:mdns:services";

    private static readonly TimeSpan MalformedLogInterval = TimeSpan.FromMinutes(1);

    private readonly Func<IAdbHostConnection> _connectionFactory;
    private readonly MdnsTrackerBackoff _backoff;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IAdbExecutableLocator _executableLocator;
    private readonly IMdnsTrackServicesCli _cli;
    private readonly Action<string>? _onDegraded;
    private readonly Action<string>? _onMalformedPayload;

    private int? _preferredUnsupportedGeneration;
    private int? _degradedLoggedGeneration;
    private DateTimeOffset? _lastMalformedLogAt;

    public MdnsTracker(
        Func<IAdbHostConnection>? connectionFactory = null,
        MdnsTrackerBackoff? backoff = null,
        Func<DateTimeOffset>? getUtcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IAdbExecutableLocator? executableLocator = null,
        IMdnsTrackServicesCli? cli = null,
        Action<string>? onDegraded = null,
        Action<string>? onMalformedPayload = null) {
        _connectionFactory = connectionFactory ?? (() => new AdbHostConnection());
        _backoff = backoff ?? new MdnsTrackerBackoff();
        _getUtcNow = getUtcNow ?? (() => SystemClock.UtcNow);
        _delay = delay ?? Task.Delay;
        _executableLocator = executableLocator ?? new AdbExecutableLocator();
        _cli = cli ?? new MdnsTrackServicesCli();
        _onDegraded = onDegraded;
        _onMalformedPayload = onMalformedPayload;
    }

    public event Action<MdnsServicesReceived>? ServicesReceived;
    public event Action<MdnsTrackerFailed>? Failed;

    /// <summary>
    /// Last-resort one-shot listing via <c>host:mdns:services</c>.
    /// Composition (not this tracker) decides when to call it (e.g. on visibility).
    /// </summary>
    public async Task<ImmutableArray<RawMdnsService>> QueryServicesOnceAsync(CancellationToken ct = default) {
        var connection = _connectionFactory();
        await using (connection.ConfigureAwait(false)) {
            var payload = await connection.ExecuteOneShotAsync(OneShotService, ct).ConfigureAwait(false);
            return MdnsServicesListParser.Parse(payload.Span);
        }
    }

    /// <summary>
    /// Runs discovery sessions for a known server generation until cancelled.
    /// Preferred-service unsupported is sticky for this generation only.
    /// </summary>
    public async Task RunAsync(int serverGeneration, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();

        while (!ct.IsCancellationRequested) {
            bool usePreferred = ShouldUsePreferred(serverGeneration);

            try {
                if (usePreferred) {
                    await RunPreferredSessionAsync(ct).ConfigureAwait(false);
                }
                else {
                    await RunCliSessionAsync(ct).ConfigureAwait(false);
                }

                EmitFailed(usePreferred
                    ? $"mDNS tracker stream ended ({PreferredService})."
                    : "mDNS tracker CLI stream ended.");
                await DelayBackoffAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            }
            catch (AdbProtocolException ex) when (
                ex.ErrorCode == AdbErrorCode.CommandFailed
                && usePreferred
                && AdbHostServiceDiagnostics.IsUnknownHostService(ex.ServerReason)) {
                MarkPreferredUnsupported(serverGeneration, ex.ServerReason ?? ex.Message);
                continue;
            }
            catch (AdbException ex) {
                EmitFailed(ex.Message);
                await DelayBackoffAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or System.IO.IOException
                or System.ComponentModel.Win32Exception or InvalidOperationException) {
                EmitFailed(ex.Message);
                await DelayBackoffAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunPreferredSessionAsync(CancellationToken ct) {
        var connection = _connectionFactory();
        await using (connection.ConfigureAwait(false)) {
            await foreach (var frame in connection.ExecuteFramedStreamAsync(PreferredService, ct).ConfigureAwait(false)) {
                ct.ThrowIfCancellationRequested();
                try {
                    var services = MdnsServicesProtoParser.Parse(frame.Span);
                    EmitServices(services);
                }
                catch (AdbProtocolException ex) when (IsPayloadParseFailure(ex)) {
                    LogMalformedRateLimited(ex.Message);
                }
            }
        }
    }

    private async Task RunCliSessionAsync(CancellationToken ct) {
        string? exe = _executableLocator.Locate();
        if (string.IsNullOrEmpty(exe)) {
            throw new AdbConnectionException("adb.exe not found for mDNS track-services fallback.");
        }

        await foreach (var document in _cli.ReadProtoTextDocumentsAsync(exe, ct).ConfigureAwait(false)) {
            ct.ThrowIfCancellationRequested();
            try {
                var services = MdnsServicesProtoTextParser.Parse(document.Span);
                EmitServices(services);
            }
            catch (AdbProtocolException ex) when (IsPayloadParseFailure(ex)) {
                LogMalformedRateLimited(ex.Message);
            }
        }
    }

    private void EmitServices(ImmutableArray<RawMdnsService> services) {
        ServicesReceived?.Invoke(new MdnsServicesReceived(services, _getUtcNow()));
        _backoff.Reset();
    }

    private bool ShouldUsePreferred(int serverGeneration) =>
        _preferredUnsupportedGeneration != serverGeneration;

    private void MarkPreferredUnsupported(int serverGeneration, string reason) {
        _preferredUnsupportedGeneration = serverGeneration;
        if (_degradedLoggedGeneration != serverGeneration) {
            _degradedLoggedGeneration = serverGeneration;
            _onDegraded?.Invoke(
                $"Preferred mDNS tracking unavailable for server generation {serverGeneration}; falling back to CLI `{MdnsTrackServicesCli.Arguments}`. Reason: {reason}");
        }
    }

    private static bool IsPayloadParseFailure(AdbProtocolException ex) =>
        ex.ErrorCode is AdbErrorCode.MalformedLength
            or AdbErrorCode.UnexpectedEof
            or AdbErrorCode.PayloadTooLarge;

    private void LogMalformedRateLimited(string message) {
        var now = _getUtcNow();
        if (_lastMalformedLogAt is DateTimeOffset last && now - last < MalformedLogInterval) {
            return;
        }

        _lastMalformedLogAt = now;
        _onMalformedPayload?.Invoke(message);
    }

    private void EmitFailed(string reason) =>
        Failed?.Invoke(new MdnsTrackerFailed(reason));

    private async Task DelayBackoffAsync(CancellationToken ct) {
        TimeSpan delay = _backoff.NextDelay();
        await _delay(delay, ct).ConfigureAwait(false);
    }
}
