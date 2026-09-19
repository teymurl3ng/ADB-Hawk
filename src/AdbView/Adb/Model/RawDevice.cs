namespace AdbView.Adb.Model;

using System;
using System.Collections.Immutable;

/// <summary>
/// Raw tracker-domain device row. No identity, UI labels, or timestamps beyond list At.
/// Schema pin: platform/packages/modules/adb @ 1cf2f017d312f73b3dc53bda85ef2610e35a80e9
/// </summary>
internal sealed record RawDevice {
    public required string Serial { get; init; }

    /// <summary>Proto <c>ConnectionState</c> enum integer when <see cref="Format"/> is ProtoBinary; otherwise null.</summary>
    public int? ProtoState { get; init; }

    /// <summary>Text state token (e.g. <c>device</c>) when <see cref="Format"/> is TrackDevicesL; otherwise null.</summary>
    public string? StateToken { get; init; }

    /// <summary>Proto <c>ConnectionType</c> enum integer; null when absent (text fallback).</summary>
    public int? ConnectionType { get; init; }

    public string? Product { get; init; }
    public string? Model { get; init; }
    public string? Device { get; init; }

    public required RawDeviceFormat Format { get; init; }
}

internal enum RawDeviceFormat {
    ProtoBinary = 0,
    TrackDevicesL = 1,
}

/// <summary>Complete device-list snapshot from the tracker (ENGINEERING.md §6.10).</summary>
internal sealed record DeviceListReceived(ImmutableArray<RawDevice> Devices, DateTimeOffset At);

/// <summary>Tracker session failure requiring reconnect (ENGINEERING.md §6.10).</summary>
internal sealed record DeviceTrackerFailed(string Reason);
