namespace AdbView.State;

using System;
using System.Collections.Immutable;
using AdbView.Adb.Model;

/// <summary>
/// Engine-private bookkeeping for device-list ordering, mDNS discovery, pairing wait,
/// and wireless-warning timers. Not part of AppSnapshot / bridge contract.
/// </summary>
internal sealed record DeviceEngineState {
    public static DeviceEngineState Empty { get; } = new() {
        LatestAppliedListAt = null,
        ActiveGraceGeneration = 0,
        LatestAppliedMdnsAt = null,
        LatestMdnsServices = ImmutableArray<RawMdnsService>.Empty,
        LatestFallbackServices = ImmutableArray<RawMdnsService>.Empty,
        LatestAppliedFallbackAt = null,
        OfferBindings = ImmutableDictionary<string, PairingOfferBinding>.Empty,
        WirelessFailed = false,
        ActiveWirelessRestoringGeneration = 0,
        ActiveWirelessUnavailableGeneration = 0,
        ActivePairingWaitGeneration = 0,
        ActivePairingOfferId = null,
        ActivePairingHost = null,
        ActivePairingSerial = null,
        ActivePairingDisplayName = null,
        NextMetadataGeneration = 0,
        MetadataSessions = ImmutableDictionary<string, DeviceMetadataSession>.Empty,
        ServerMdnsEnabled = null,
        LatestDiscoveryDiagnosis = null,
        DiscoveryCheckRunning = false,
    };

    public DateTimeOffset? LatestAppliedListAt { get; init; }

    /// <summary>Non-zero while a tracker-failure grace window is armed.</summary>
    public long ActiveGraceGeneration { get; init; }

    public DateTimeOffset? LatestAppliedMdnsAt { get; init; }

    public ImmutableArray<RawMdnsService> LatestMdnsServices { get; init; } = ImmutableArray<RawMdnsService>.Empty;

    /// <summary>Latest fallback-browser list (decision 0008 follow-through); fills only the adb gap.</summary>
    public ImmutableArray<RawMdnsService> LatestFallbackServices { get; init; } = ImmutableArray<RawMdnsService>.Empty;

    public DateTimeOffset? LatestAppliedFallbackAt { get; init; }

    public ImmutableDictionary<string, PairingOfferBinding> OfferBindings { get; init; } =
        ImmutableDictionary<string, PairingOfferBinding>.Empty;

    public bool WirelessFailed { get; init; }

    public long ActiveWirelessRestoringGeneration { get; init; }

    public long ActiveWirelessUnavailableGeneration { get; init; }

    public long ActivePairingWaitGeneration { get; init; }

    public string? ActivePairingOfferId { get; init; }

    public string? ActivePairingHost { get; init; }

    public string? ActivePairingSerial { get; init; }

    public string? ActivePairingDisplayName { get; init; }

    /// <summary>Monotonic allocator for metadata generations (never resets on device removal).</summary>
    public long NextMetadataGeneration { get; init; }

    public ImmutableDictionary<string, DeviceMetadataSession> MetadataSessions { get; init; } =
        ImmutableDictionary<string, DeviceMetadataSession>.Empty;

    /// <summary>adb server-status mdns_enabled (decision 0008 trigger input). Null until reported.</summary>
    public bool? ServerMdnsEnabled { get; init; }

    /// <summary>Last measurement result; dropped whenever the health card clears (momentary evidence).</summary>
    public DiscoveryDiagnosis? LatestDiscoveryDiagnosis { get; init; }

    /// <summary>True between DiscoveryCheckStartedEvent and its diagnosis; disables Check again.</summary>
    public bool DiscoveryCheckRunning { get; init; }

    /// <summary>Backend-owned repair offers for the visible card (decision 0008 amendment).</summary>
    public ImmutableArray<DiscoveryRepairDescriptor> RepairCatalog { get; init; } =
        ImmutableArray<DiscoveryRepairDescriptor>.Empty;

    /// <summary>Per-repair progress; absent = available. Completed entries are hidden from the card.</summary>
    public ImmutableDictionary<string, DiscoveryRepairOutcome> RepairResults { get; init; } =
        ImmutableDictionary<string, DiscoveryRepairOutcome>.Empty;

    /// <summary>Repair currently executing (button disabled). Null when none.</summary>
    public string? RunningRepairId { get; init; }

    /// <summary>Completed repair whose outcome the next measurement should report. Null when none.</summary>
    public string? AwaitingMeasureRepairId { get; init; }

    /// <summary>Momentary post-action result line; cleared on the next evidence change.</summary>
    public string? RepairResultLine { get; init; }
}

/// <summary>Resolved pairing offer endpoint kept out of AppSnapshot.</summary>
internal sealed record PairingOfferBinding(
    string OfferId,
    string Host,
    int Port,
    string? Serial,
    string DisplayName);

internal enum GraceCommandKind {
    None,
    Schedule,
    Cancel,
}

internal readonly record struct GraceCommand(GraceCommandKind Kind, long Generation) {
    public static GraceCommand None { get; } = new(GraceCommandKind.None, 0);
    public static GraceCommand Schedule(long generation) => new(GraceCommandKind.Schedule, generation);
    public static GraceCommand Cancel { get; } = new(GraceCommandKind.Cancel, 0);
}

internal readonly record struct ReduceResult(
    AppSnapshot Snapshot,
    DeviceEngineState DeviceState,
    GraceCommand Grace,
    GraceCommand PairingWait,
    GraceCommand WirelessRestoring,
    GraceCommand WirelessUnavailable) {
    public static ReduceResult Unchanged(AppSnapshot snapshot, DeviceEngineState deviceState) =>
        new(snapshot, deviceState, GraceCommand.None, GraceCommand.None, GraceCommand.None, GraceCommand.None);

    public static ReduceResult With(
        AppSnapshot snapshot,
        DeviceEngineState deviceState,
        GraceCommand? grace = null,
        GraceCommand? pairingWait = null,
        GraceCommand? wirelessRestoring = null,
        GraceCommand? wirelessUnavailable = null) =>
        new(
            snapshot,
            deviceState,
            grace ?? GraceCommand.None,
            pairingWait ?? GraceCommand.None,
            wirelessRestoring ?? GraceCommand.None,
            wirelessUnavailable ?? GraceCommand.None);
}
