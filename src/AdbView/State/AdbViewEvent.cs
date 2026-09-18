namespace AdbView.State;

using AdbView.Adb.Model;

internal abstract record AdbViewEvent;

internal sealed record AdbServerStateChangedEvent(AdbServerStateChanged State) : AdbViewEvent;

internal sealed record SetPinnedEvent(bool Pinned) : AdbViewEvent;
