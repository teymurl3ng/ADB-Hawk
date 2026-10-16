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
    DateTimeOffset At,
    long Sequence = 0) : AdbViewEvent;

/// <summary>Tracker stream failure; devices retained for 2 s grace (ENGINEERING.md §6.5 / §11).</summary>
internal sealed record DeviceTrackerFailedEvent(string Reason) : AdbViewEvent;

/// <summary>
/// Internal grace-expiry signal. Generation must match the active grace token or the event is ignored.
/// </summary>
internal sealed record DeviceTrackerGraceExpiredEvent(long Generation) : AdbViewEvent;

/// <summary>Complete authoritative mDNS service list from the ADB server (ENGINEERING.md §6.10).</summary>
internal sealed record MdnsServicesReceivedEvent(
    ImmutableArray<RawMdnsService> Services,
    DateTimeOffset At,
    long Sequence = 0) : AdbViewEvent;

/// <summary>
/// Complete soft-state service list from the first-party fallback browser (decision 0008
/// follow-through). Never satisfies the broken-discovery trigger — that audits the ADB source.
/// </summary>
internal sealed record MdnsFallbackReceivedEvent(
    ImmutableArray<RawMdnsService> Services,
    DateTimeOffset At,
    long Sequence = 0) : AdbViewEvent;

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
/// Quiet clear of PairedWaitingForDevice after 60 s (ENGINEERING §6.8 / §11 row 13).
/// </summary>
internal sealed record PairingWaitExpiredEvent(long Generation) : AdbViewEvent;

// ---------- QR pairing (decision 0010) ----------

/// <summary>QR session started; the card displays the backend-rendered code.</summary>
internal sealed record PairingQrStartedEvent(PairingQrView View) : AdbViewEvent;

/// <summary>
/// QR session cleared: user cancel (generation = active) or the sanctioned 2-minute expiry
/// (timer generation must match, like every other grace command). Quiet either way.
/// </summary>
internal sealed record PairingQrClearedEvent(long Generation) : AdbViewEvent;

/// <summary>
/// ENGINEERING.md §6.10 metadata event. Generation is NOT on this public shape —
/// StateEngine validates ExpectedGeneration on the internal work-item envelope.
/// </summary>
internal sealed record MetadataReceivedEvent(
    string DeviceId,
    int? Battery,
    string? Model,
    string? AndroidVersion,
    string? MarketingName = null,
    string? DeviceName = null,
    string? Brand = null,
    string? Manufacturer = null) : AdbViewEvent;

// ---------- discovery health (decision 0008) ----------

/// <summary>A measurement run started; Check again stays disabled until the result lands.</summary>
internal sealed record DiscoveryCheckStartedEvent : AdbViewEvent;

/// <summary>Structured result of one measurement run (probe outcome + environment evidence).</summary>
internal sealed record DiscoveryDiagnosisReceivedEvent(DiscoveryDiagnosis Diagnosis) : AdbViewEvent;

internal enum DiscoveryProbeOutcome {
    /// <summary>The probe sent queries and collected responses (count may still be zero).</summary>
    Measured,
    /// <summary>The probe could not run or observe the network (blocked, no usable interface, error).</summary>
    Unavailable,
}

/// <summary>
/// One-shot diagnosis payload. Process/adapter names are user-displayable labels chosen by the
/// Host layer; they must never contain pairing secrets or raw transport identifiers (R-13/R-15).
/// </summary>
internal sealed record DiscoveryDiagnosis(
    DiscoveryProbeOutcome ProbeOutcome,
    int AnnouncementCount,
    ImmutableArray<string> PortHolders,
    ImmutableArray<string> VirtualAdapters);

// ---------- discovery repairs (decision 0008 amendment) ----------

/// <summary>Closed action set (decision 0008 amendment). No other repair exists.</summary>
internal enum DiscoveryRepairActionKind {
    RestartAdb,
    StopService,
    StartService,
    DisableAdapter,
    EnableAdapter,
}

internal enum DiscoveryRepairOutcome {
    /// <summary>The action ran; a re-measure reports whether discovery recovered.</summary>
    Completed,
    /// <summary>The user declined the elevation prompt; nothing changed.</summary>
    Declined,
    /// <summary>The action could not be performed.</summary>
    Failed,
}

/// <summary>Backend-owned repair offer. Ids are opaque to the shell.</summary>
internal sealed record DiscoveryRepairDescriptor(
    string Id,
    DiscoveryRepairActionKind Kind,
    string? Target);

internal sealed record DiscoveryRepairCatalogEvent(ImmutableArray<DiscoveryRepairDescriptor> Repairs) : AdbViewEvent;

internal sealed record DiscoveryRepairStartedEvent(string RepairId) : AdbViewEvent;

internal sealed record DiscoveryRepairFinishedEvent(string RepairId, DiscoveryRepairOutcome Outcome) : AdbViewEvent;
