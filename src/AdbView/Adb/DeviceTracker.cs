namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;
using AdbView.App;

/// <summary>
/// Raw ADB device tracker: consumes complete device-list frames from
/// <c>host:track-devices-proto-binary</c> with per-generation fallback to
/// <c>host:track-devices-l</c>. Emits domain events only; no State/UI wiring (Phase 5A).
/// </summary>
internal sealed class DeviceTracker {
    public const string ProtoBinaryService = "host:track-devices-proto-binary";
    public const string TextLongService = "host:track-devices-l";

    private static readonly TimeSpan MalformedLogInterval = TimeSpan.FromMinutes(1);

    private readonly Func<IAdbHostConnection> _connectionFactory;
    private readonly DeviceTrackerBackoff _backoff;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _onDegraded;
    private readonly Action<string>? _onMalformedPayload;

    private int? _protoUnsupportedGeneration;
    private int? _degradedLoggedGeneration;
    private DateTimeOffset? _lastMalformedLogAt;

    public DeviceTracker(
        Func<IAdbHostConnection>? connectionFactory = null,
        DeviceTrackerBackoff? backoff = null,
        Func<DateTimeOffset>? getUtcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? onDegraded = null,
        Action<string>? onMalformedPayload = null) {
        _connectionFactory = connectionFactory ?? (() => new AdbHostConnection());
        _backoff = backoff ?? new DeviceTrackerBackoff();
        _getUtcNow = getUtcNow ?? (() => SystemClock.UtcNow);
        _delay = delay ?? Task.Delay;
        _onDegraded = onDegraded;
        _onMalformedPayload = onMalformedPayload;
    }

    public event Action<DeviceListReceived>? ListReceived;
    public event Action<DeviceTrackerFailed>? Failed;

    /// <summary>
    /// Runs tracker sessions for a known server generation until <paramref name="ct"/> is cancelled.
    /// Proto unsupported is sticky for this generation only.
    /// </summary>
    public async Task RunAsync(int serverGeneration, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();

        while (!ct.IsCancellationRequested) {
            bool usedProto = ShouldUseProto(serverGeneration);
            string service = usedProto ? ProtoBinaryService : TextLongService;

            try {
                await RunSessionAsync(service, usedProto, ct).ConfigureAwait(false);

                // Clean EOF at frame boundary → session ended; reconnect with backoff.
                EmitFailed($"Tracker stream ended ({service}).");
                await DelayBackoffAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            }
            catch (AdbProtocolException ex) when (
                ex.ErrorCode == AdbErrorCode.CommandFailed
                && usedProto
                && AdbHostServiceDiagnostics.IsUnknownHostService(ex.ServerReason)) {
                // Explicit unknown host service → fall back for this generation (no backoff).
                MarkProtoUnsupported(serverGeneration, ex.ServerReason ?? ex.Message);
                continue;
            }
            catch (AdbException ex) {
                EmitFailed(ex.Message);
                await DelayBackoffAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or System.IO.IOException) {
                EmitFailed(ex.Message);
                await DelayBackoffAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSessionAsync(string service, bool protoMode, CancellationToken ct) {
        var connection = _connectionFactory();
        await using (connection.ConfigureAwait(false)) {
            await foreach (var frame in connection.ExecuteFramedStreamAsync(service, ct).ConfigureAwait(false)) {
                ct.ThrowIfCancellationRequested();

                try {
                    var devices = protoMode
                        ? DevicesProtoParser.Parse(frame.Span)
                        : TrackDevicesLParser.Parse(frame.Span);

                    var at = _getUtcNow();
                    ListReceived?.Invoke(new DeviceListReceived(devices, at));
                    _backoff.Reset();
                }
                catch (AdbProtocolException ex) when (IsPayloadParseFailure(ex)) {
                    // Valid ADB frame + bad body: drop update, keep stream (ENGINEERING.md §11 row 7).
                    LogMalformedRateLimited(ex.Message);
                }
            }
        }
    }

    private bool ShouldUseProto(int serverGeneration) =>
        _protoUnsupportedGeneration != serverGeneration;

    private void MarkProtoUnsupported(int serverGeneration, string reason) {
        _protoUnsupportedGeneration = serverGeneration;
        if (_degradedLoggedGeneration != serverGeneration) {
            _degradedLoggedGeneration = serverGeneration;
            _onDegraded?.Invoke(
                $"Proto device tracking unavailable for server generation {serverGeneration}; falling back to {TextLongService}. Reason: {reason}");
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
        Failed?.Invoke(new DeviceTrackerFailed(reason));

    private async Task DelayBackoffAsync(CancellationToken ct) {
        TimeSpan delay = _backoff.NextDelay();
        await _delay(delay, ct).ConfigureAwait(false);
    }
}
