namespace AdbView.State;

using System;
using System.Collections.Immutable;
using AdbView.Adb.Model;

internal abstract record AdbViewEvent;

internal sealed record AdbServerStateChangedEvent(AdbServerStateChanged State) : AdbViewEvent;

internal sealed record SetPinnedEvent(bool Pinned) : AdbViewEvent;

/// <summary>Windows appearance → shell theme / transparency / motion / contrast.</summary>
internal sealed record ShellAppearanceChangedEvent(
    ThemeKind Theme,
    bool TransparencyEnabled,
    bool ForcedColors,
    bool ReducedMotion) : AdbViewEvent;

/// <summary>Complete authoritative device list from DeviceTracker (ENGINEERING.md §6.10).</summary>
internal sealed record DeviceListReceivedEvent(
    ImmutableArray<RawDevice> Devices,
    DateTimeOffset At) : AdbViewEvent;

/// <summary>Tracker stream failure; devices retained for 2 s grace (ENGINEERING.md §6.5 / §11).</summary>
internal sealed record DeviceTrackerFailedEvent(string Reason) : AdbViewEvent;

/// <summary>
/// Internal grace-expiry signal. Generation must match the active grace token or the event is ignored.
/// </summary>
internal sealed record DeviceTrackerGraceExpiredEvent(long Generation) : AdbViewEvent;

/// <summary>Complete authoritative mDNS service list (ENGINEERING.md §6.10).</summary>
internal sealed record MdnsServicesReceivedEvent(
    ImmutableArray<RawMdnsService> Services,
    DateTimeOffset At) : AdbViewEvent;

/// <summary>mDNS tracker failure; local wireless warning only (ENGINEERING.md §7.4 / §11).</summary>
internal sealed record MdnsTrackerFailedEvent(string Reason) : AdbViewEvent;

/// <summary>
/// Delayed wireless-discovery Restoring arm (ENGINEERING.md §11 row 8 — after 5 s).
/// </summary>
internal sealed record WirelessDiscoveryRestoringExpiredEvent(long Generation) : AdbViewEvent;

/// <summary>
/// Delayed wireless-discovery Unavailable arm (ENGINEERING.md §11 row 9 — after 60 s).
/// </summary>
internal sealed record WirelessDiscoveryUnavailableExpiredEvent(long Generation) : AdbViewEvent;

/// <summary>Domain pairing progress adapted into State (ENGINEERING.md §6.10).</summary>
internal sealed record PairingProgressedEvent(
    string OfferId,
    AdbView.Adb.Model.PairingPhase Phase,
    string? FailureReason,
    AdbView.Adb.Model.PairingFailureKind FailureKind,
    string? DisplayName) : AdbViewEvent;

/// <summary>
/// Quiet clear of PairedWaitingForDevice after 60 s (ENGINEERING.md §6.8 / §11 row 13).
/// </summary>
internal sealed record PairingWaitExpiredEvent(long Generation) : AdbViewEvent;

/// <summary>
/// ENGINEERING.md §6.10 metadata event. Generation is NOT on this public shape —
/// StateEngine validates ExpectedGeneration on the internal work-item envelope.
/// </summary>
internal sealed record MetadataReceivedEvent(
    string DeviceId,
    int? Battery,
    string? Model,
    string? AndroidVersion) : AdbViewEvent;
