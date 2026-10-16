namespace AdbView.WinUiMigration.Host;

// Presentation suppression only; Core remains the owner of aggregate meaning/lifetime.
internal static class BubbleVisibility {
    public static bool ShouldShow(bool enabled, bool flyoutVisible, bool fullScreen, bool suspended, bool hasState) =>
        enabled && hasState && !flyoutVisible && !fullScreen && !suspended;
}
