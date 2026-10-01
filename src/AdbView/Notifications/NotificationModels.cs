namespace AdbView.Notifications;

using System;
using System.Collections.Immutable;

/// <summary>Frozen Phase 8 notification kinds (<c>ENGINEERING.md</c> §8.1).</summary>
internal enum NotificationKind {
    DeviceConnected,
    DeviceDisconnected,
    AuthorizationRequired,
    DeviceProblem,
    PairingAvailable,
    PairingResult,
    AdbUnavailable,
    DiscoveryBroken,
    DevicesChanged,
}

/// <summary>Maps to tray balloon severity (NIIF_INFO / WARNING / ERROR in 8B).</summary>
internal enum NotificationSeverity {
    Info,
    Warning,
    Error,
}

/// <summary>
/// Dedup / pending key. Subject = deviceId | offerId | <c>"global"</c>.
/// Never DisplayName alone.
/// </summary>
internal readonly record struct NotificationKey(NotificationKind Kind, string Subject) {
    public string Composite => Kind + "\u001f" + Subject;
}

internal sealed record NotificationIntent(
    NotificationKey Key,
    string Title,
    string Body,
    NotificationSeverity Severity);

/// <summary>
/// Internal-only notifier bookkeeping. Must never appear in AppSnapshot / Bridge / UI.
/// </summary>
internal sealed record NotifierState {
    public static NotifierState Create(DateTimeOffset processStartedAt) => new() {
        ProcessStartedAt = processStartedAt,
        SuppressUntil = processStartedAt.AddSeconds(5),
        BaselineEstablished = false,
        AdbUnavailableOutageNotified = false,
        DiscoveryBrokenNotified = false,
        LastEmittedAt = ImmutableDictionary<string, DateTimeOffset>.Empty,
        LastPairingOfferEmittedAt = ImmutableDictionary<string, DateTimeOffset>.Empty,
        RecentBurstEmits = ImmutableArray<DateTimeOffset>.Empty,
    };

    public required DateTimeOffset ProcessStartedAt { get; init; }
    public DateTimeOffset SuppressUntil { get; init; }
    public bool BaselineEstablished { get; init; }
    public bool ShuttingDown { get; init; }
    public bool Suspended { get; init; }

    /// <summary>True after a successful AdbUnavailable emit until health leaves AdbUnavailable.</summary>
    public bool AdbUnavailableOutageNotified { get; init; }

    /// <summary>True after a DiscoveryBroken emit until the discovery-health card clears (decision 0008 amendment).</summary>
    public bool DiscoveryBrokenNotified { get; init; }

    public ImmutableDictionary<string, DateTimeOffset> LastEmittedAt { get; init; }
        = ImmutableDictionary<string, DateTimeOffset>.Empty;

    public ImmutableDictionary<string, DateTimeOffset> LastPairingOfferEmittedAt { get; init; }
        = ImmutableDictionary<string, DateTimeOffset>.Empty;

    /// <summary>Emit timestamps of non-<see cref="NotificationKind.PairingResult"/> notifications.</summary>
    public ImmutableArray<DateTimeOffset> RecentBurstEmits { get; init; }
        = ImmutableArray<DateTimeOffset>.Empty;
}

internal readonly record struct PendingHold(
    NotificationKey Key,
    DateTimeOffset DueAt,
    DateTimeOffset DetectedAt,
    string? DisplayNameHint = null);

/// <summary>Pure policy output for one snapshot transition.</summary>
internal sealed record NotificationDecision(
    ImmutableArray<PendingHold> ArmHolds,
    ImmutableArray<NotificationKey> CancelKeys,
    ImmutableArray<NotificationIntent> ImmediateIntents,
    NotifierState NextState);
