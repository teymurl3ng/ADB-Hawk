namespace AdbView.Adb.Model;

/// <summary>Closed set of metadata shell commands (ENGINEERING.md §6.9 / §12.1).</summary>
internal enum MetadataCommand {
    Battery,
    ExactModel,
    AndroidVersion,
}

/// <summary>Per-field outcome for an isolated metadata probe (no UI/state types).</summary>
internal enum MetadataFieldStatus {
    Ok,
    Unavailable,
    Timeout,
    TransportFailed,
    ShellFailed,
    Cancelled,
    Overflow,
    Malformed,
}

/// <summary>
/// Probe target. <see cref="AdbSerial"/> is the tracker-authoritative transport serial
/// used in <c>host:transport:</c> — never reconstruct from <see cref="DeviceId"/>.
/// <see cref="Generation"/> is carried for 7B stale rejection (serial change bumps it).
/// </summary>
internal sealed record MetadataProbeTarget(
    string DeviceId,
    string AdbSerial,
    long Generation);

/// <summary>Isolated domain result for one three-field (or subset) probe.</summary>
internal sealed record MetadataProbeResult(
    string DeviceId,
    long Generation,
    int? BatteryPercent,
    string? ExactModel,
    string? AndroidVersion,
    MetadataFieldStatus BatteryStatus,
    MetadataFieldStatus ExactModelStatus,
    MetadataFieldStatus AndroidVersionStatus);

/// <summary>Single-command outcome before aggregation.</summary>
internal sealed record MetadataCommandResult(
    MetadataCommand Command,
    MetadataFieldStatus Status,
    int? BatteryPercent,
    string? TextValue);
