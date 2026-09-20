namespace AdbView.Bridge;

internal interface IBridgeHost {
    void SendSnapshot(SnapshotEnvelope envelope);
    void SetPinned(bool isPinned);
    void HideFlyout();
    void ResizeFlyout(
        int clampedEffectiveHeight,
        ContentSizePhase phase = ContentSizePhase.Live,
        string? layoutKey = null);
}
