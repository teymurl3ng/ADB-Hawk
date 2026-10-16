namespace AdbView.Adb.Model;

using System;

/// <summary>Domain pairing phases (ENGINEERING.md §6.8). Cleared = operation absent.</summary>
internal enum PairingPhase {
    AwaitingCode = 0,
    Pairing = 1,
    PairedWaitingForDevice = 2,
    Failed = 3,
    Cleared = 4,
}

/// <summary>Safe machine/domain failure classification — not user copy.</summary>
internal enum PairingFailureKind {
    None = 0,
    Rejected = 1,
    Timeout = 2,
    ProtocolFailure = 3,
    MalformedResponse = 4,
    Cancelled = 5,
    RateLimited = 6,
    InvalidRequest = 7,
}

/// <summary>Domain progress (ENGINEERING.md §6.10). Never carries the pairing code.</summary>
internal sealed record PairingProgressed(
    string OfferId,
    PairingPhase Phase,
    string? FailureReason,
    PairingFailureKind FailureKind,
    DateTimeOffset At);
