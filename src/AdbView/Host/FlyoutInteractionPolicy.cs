namespace AdbView.Host;

using System;
using System.Drawing;

/// <summary>Pure policies for content-size / initial-show / light-dismiss / v1 freeze (mutation-tested).</summary>
internal static class FlyoutInteractionPolicy {
    /// <summary>
    /// v1 (decision 0005): while the flyout is visible, content-size reports must not
    /// change native HWND bounds. Apply only when hidden (initial / next-show path).
    /// When applying while hidden, HWND bounds must actually update so WebView2 lays out
    /// at the presentation width (376 epx) before the next measure — otherwise the first
    /// hidden measure uses the WinForms default size and inflates height.
    /// </summary>
    public static bool ShouldApplyContentResizeToHwnd(bool flyoutVisible) =>
        !flyoutVisible;

    /// <summary>
    /// Initial show after the first valid content-size once the hidden HWND has been
    /// primed to 376 epx width (<see cref="AppHost"/>). A second settle report is not
    /// required: stable fixtures (empty) may only fire ResizeObserver once, and duplicate
    /// heights are coalesced by the frontend.
    /// </summary>
    public static bool ShouldCompleteInitialShow(int hiddenContentSizeReportsReceived) =>
        hiddenContentSizeReportsReceived >= 1;

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
