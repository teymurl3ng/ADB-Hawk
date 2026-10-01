namespace AdbView.Adb;

using System;
using System.Threading;
using System.Threading.Tasks;
using AdbView.Adb.Model;

/// <summary>
/// Isolated metadata probe domain (Phase 7A). No AppHost / snapshot wiring.
/// Fresh connection per command; per-command 5 s timeout; one retry after 2 s on failure.
/// </summary>
internal interface IMetadataProbe {
    Task<MetadataProbeResult> ProbeAsync(MetadataProbeTarget target, CancellationToken ct);

    Task<MetadataCommandResult> ProbeCommandAsync(
        MetadataProbeTarget target,
        MetadataCommand command,
        CancellationToken ct);
}

internal sealed class MetadataProbe : IMetadataProbe {
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IMetadataDeviceShellClient _shell;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MetadataProbe(
        IMetadataDeviceShellClient shell,
        Func<TimeSpan, CancellationToken, Task>? delay = null) {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _delay = delay ?? Task.Delay;
    }

    public async Task<MetadataProbeResult> ProbeAsync(MetadataProbeTarget target, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.AdbSerial);

        var battery = await ProbeCommandAsync(target, MetadataCommand.Battery, ct).ConfigureAwait(false);
        var model = await ProbeCommandAsync(target, MetadataCommand.ExactModel, ct).ConfigureAwait(false);
        var version = await ProbeCommandAsync(target, MetadataCommand.AndroidVersion, ct).ConfigureAwait(false);
        var marketing = await ProbeCommandAsync(target, MetadataCommand.MarketingName, ct).ConfigureAwait(false);
        var deviceName = await ProbeCommandAsync(target, MetadataCommand.DeviceName, ct).ConfigureAwait(false);
        var brand = await ProbeCommandAsync(target, MetadataCommand.Brand, ct).ConfigureAwait(false);
        var manufacturer = await ProbeCommandAsync(target, MetadataCommand.Manufacturer, ct).ConfigureAwait(false);

        return new MetadataProbeResult(
            target.DeviceId,
            target.Generation,
            battery.BatteryPercent,
            model.TextValue,
            version.TextValue,
            battery.Status,
            model.Status,
            version.Status,
            marketing.TextValue,
            deviceName.TextValue,
            brand.TextValue,
            manufacturer.TextValue,
            marketing.Status,
            deviceName.Status,
            brand.Status,
            manufacturer.Status);
    }

    public async Task<MetadataCommandResult> ProbeCommandAsync(
        MetadataProbeTarget target,
        MetadataCommand command,
        CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.AdbSerial);

        MetadataCommandResult first = await ExecuteOnceAsync(target, command, ct).ConfigureAwait(false);
        if (first.Status == MetadataFieldStatus.Ok
            || first.Status == MetadataFieldStatus.Cancelled) {
            return first;
        }

        try {
            await _delay(RetryDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return new MetadataCommandResult(command, MetadataFieldStatus.Cancelled, null, null);
        }

        return await ExecuteOnceAsync(target, command, ct).ConfigureAwait(false);
    }

    private async Task<MetadataCommandResult> ExecuteOnceAsync(
        MetadataProbeTarget target,
        MetadataCommand command,
        CancellationToken ct) {
        MetadataShellRawResult raw = await _shell
            .ExecuteFixedShellAsync(target.AdbSerial, command, ct)
            .ConfigureAwait(false);

        return MapRaw(command, raw);
    }

    private static MetadataCommandResult MapRaw(MetadataCommand command, MetadataShellRawResult raw) {
        MetadataFieldStatus status = raw.Kind switch {
            MetadataShellRawKind.Ok => MetadataFieldStatus.Ok,
            MetadataShellRawKind.TransportFailed => MetadataFieldStatus.TransportFailed,
            MetadataShellRawKind.ShellFailed => MetadataFieldStatus.ShellFailed,
            MetadataShellRawKind.Timeout => MetadataFieldStatus.Timeout,
            MetadataShellRawKind.Cancelled => MetadataFieldStatus.Cancelled,
            MetadataShellRawKind.Overflow => MetadataFieldStatus.Overflow,
            MetadataShellRawKind.ProtocolError => MetadataFieldStatus.Malformed,
            _ => MetadataFieldStatus.Malformed,
        };

        if (status != MetadataFieldStatus.Ok) {
            return new MetadataCommandResult(command, status, null, null);
        }

        return command switch {
            MetadataCommand.Battery => MapBattery(raw.Body.Span),
            MetadataCommand.ExactModel => MapGetprop(command, raw.Body.Span),
            MetadataCommand.AndroidVersion => MapGetprop(command, raw.Body.Span),
            MetadataCommand.MarketingName => MapGetprop(command, raw.Body.Span),
            MetadataCommand.DeviceName => MapGetprop(command, raw.Body.Span),
            MetadataCommand.Brand => MapGetprop(command, raw.Body.Span),
            MetadataCommand.Manufacturer => MapGetprop(command, raw.Body.Span),
            _ => new MetadataCommandResult(command, MetadataFieldStatus.Malformed, null, null),
        };
    }

    private static MetadataCommandResult MapBattery(ReadOnlySpan<byte> body) {
        int? level = MetadataParsers.TryParseBatteryPercent(body);
        if (level is null) {
            return new MetadataCommandResult(
                MetadataCommand.Battery,
                MetadataFieldStatus.Unavailable,
                null,
                null);
        }

        return new MetadataCommandResult(
            MetadataCommand.Battery,
            MetadataFieldStatus.Ok,
            level,
            null);
    }

    private static MetadataCommandResult MapGetprop(MetadataCommand command, ReadOnlySpan<byte> body) {
        string? value = MetadataParsers.TryParseGetpropValue(body);
        if (value is null) {
            return new MetadataCommandResult(command, MetadataFieldStatus.Unavailable, null, null);
        }

        return new MetadataCommandResult(command, MetadataFieldStatus.Ok, null, value);
    }
}
