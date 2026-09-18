namespace AdbView.Adb.Model;

internal sealed record AdbServerStateChanged(
    AdbServerPhase Phase,
    AdbServerStatus? Status,
    int ProtocolVersion,
    int Generation,
    string? ExecutablePath = null,
    string? Reason = null);
