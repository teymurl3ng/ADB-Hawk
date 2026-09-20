namespace AdbView.Host;

using System;
using System.Drawing;

/// <summary>Pure policies for content-size / initial-show / light-dismiss / v1 freeze (mutation-tested).</summary>
internal static class FlyoutInteractionPolicy {
    /// <summary>
    /// v1 (decision 0005): while the flyout is visible, content-size reports must not
    /// change native HWND bounds. Apply only when hidden (initial / next-show path).
    /// </summary>
    public static bool ShouldApplyContentResizeToHwnd(bool flyoutVisible) =>
        !flyoutVisible;

    public static bool ShouldClearInitialShowRequest(bool flyoutVisibleAfterShow) =>
        flyoutVisibleAfterShow;

    public static bool ShouldHonorLightDismiss(bool suppress, bool armed, bool pinned) =>
        !suppress && armed && !pinned;

    /// <summary>
    /// Unpinned bottom-taskbar placement helper (still used for show / DPI re-place).
    /// </summary>
    public static Rectangle BoundsFromFixedBottom(int x, int width, int height, int capturedBottom) =>
        new(x, capturedBottom - height, width, height);
}
