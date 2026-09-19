namespace AdbView.State;

using System;
using System.Collections.Immutable;

/// <summary>
/// Engine-private bookkeeping for device-list ordering and tracker-down grace.
/// Not part of AppSnapshot / bridge contract.
/// </summary>
internal sealed record DeviceEngineState {
    public static DeviceEngineState Empty { get; } = new() {
        LatestAppliedListAt = null,
        ActiveGraceGeneration = 0,
    };

    public DateTimeOffset? LatestAppliedListAt { get; init; }

    /// <summary>Non-zero while a tracker-failure grace window is armed.</summary>
    public long ActiveGraceGeneration { get; init; }
}

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
    GraceCommand Grace) {
    public static ReduceResult Unchanged(AppSnapshot snapshot, DeviceEngineState deviceState) =>
        new(snapshot, deviceState, GraceCommand.None);
}
