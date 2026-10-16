namespace AdbView.Adb.Model;

using System;
using System.Collections.Immutable;

/// <summary>
/// Raw discovery-domain mDNS service row. No UI labels or pairing-offer mapping.
/// Schema: platform-tools 37.0.1 FileDescriptor + cuttlefish mdns_tracker complete-list send.
/// </summary>
internal sealed record RawMdnsService {
    public required string InstanceName { get; init; }
    public required string ServiceType { get; init; }
    public required MdnsServiceKind Kind { get; init; }

    public string? Domain { get; init; }
    public string? Ipv4 { get; init; }
    public ImmutableArray<string> Ipv6 { get; init; } = ImmutableArray<string>.Empty;
    public int Port { get; init; }

    public string? ProductModel { get; init; }
    public string? BuildVersionSdkFull { get; init; }
    public string? GivenName { get; init; }
    public string? Serial { get; init; }
    public string? MdnsServiceVersion { get; init; }
    public string? Hostname { get; init; }

    /// <summary>Only meaningful for TLS connect services; null when absent.</summary>
    public bool? KnownDevice { get; init; }

    public required RawMdnsFormat Format { get; init; }

    /// <summary>
    /// Stable raw discovery identity from verified fields: service type + instance name.
    /// PairingOffer Id is intentionally not frozen here (Phase 6B/6C).
    /// </summary>
    public string DiscoveryKey => ServiceType + "\u001f" + InstanceName;
}

internal enum MdnsServiceKind {
    Unknown = 0,
    Legacy = 1,
    Pairing = 2,
    Connect = 3,
}

internal enum RawMdnsFormat {
    ProtoBinary = 0,
    ProtoText = 1,
    ServicesList = 2,
    /// <summary>First-party DNS-SD fallback browser rows (decision 0008 follow-through).</summary>
    Fallback = 3,
}

/// <summary>Complete mDNS service-list snapshot (ENGINEERING.md §6.10 naming).</summary>
internal sealed record MdnsServicesReceived(ImmutableArray<RawMdnsService> Services, DateTimeOffset At);

/// <summary>mDNS tracker session failure requiring reconnect (ENGINEERING.md §6.10).</summary>
internal sealed record MdnsTrackerFailed(string Reason);
