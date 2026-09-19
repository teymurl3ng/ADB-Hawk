namespace AdbView.State;

using System;
using System.Collections.Immutable;

/// <summary>
/// Engine-private per-device metadata session. Generation is never exposed to AppSnapshot/Bridge.
/// </summary>
internal sealed record DeviceMetadataSession(
    long Generation,
    string AdbSerial,
    bool ConnectedEligible,
    DateTimeOffset? BatteryObservedAt);

/// <summary>
/// Narrow projection for DeviceMetadataLifecycle. No RawDevice / protocol details.
/// </summary>
internal sealed record MetadataProbeTargetView(
    string DeviceId,
    string AdbSerial,
    long Generation,
    DeviceStateKind State,
    int? BatteryPercent,
    DateTimeOffset? BatteryObservedAt);
