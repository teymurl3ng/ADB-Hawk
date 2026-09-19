namespace AdbView.State;

using System;
using System.Collections.Immutable;
using AdbView.Adb.Model;

internal abstract record AdbViewEvent;

internal sealed record AdbServerStateChangedEvent(AdbServerStateChanged State) : AdbViewEvent;

internal sealed record SetPinnedEvent(bool Pinned) : AdbViewEvent;

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
