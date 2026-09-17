namespace AdbView.Bridge;

using AdbView.State;

internal static class BridgeProtocol {
    public const int Version = 1;

    /// <summary>
    /// JavaScript's Number.MAX_SAFE_INTEGER (2^53 - 1).
    /// Revisions above this cannot be represented losslessly by IEEE 754 double in the frontend.
    /// </summary>
    public const long MaxSafeRevision = 9007199254740991L;
}

internal sealed record SnapshotEnvelope {
    public string Type { get; init; } = "snapshot";
    public int Protocol { get; init; } = BridgeProtocol.Version;
    public required AppSnapshot Snapshot { get; init; }
}
