namespace AdbView.Host;

using System;
using System.Drawing;

/// <summary>Pure policies for content-size / initial-show / light-dismiss / live sizing (mutation-tested).</summary>
internal static class FlyoutInteractionPolicy {
    /// <summary>
    /// Decision 0012 (amending 0005): the HWND height follows the reported content height in
    /// instant steps, visible or hidden. Apply when the reported (clamped) height differs from
    /// the currently applied height; equal heights are suppressed so the hidden-priming
    /// requirement stays intact (HWND bounds must actually change on the priming path so
    /// WebView2 lays out at the presentation width 376 epx before the next measure — otherwise
    /// the first hidden measure uses the WinForms default size and inflates height). Steps are
    /// never animated: one native resize per settled content change.
    /// </summary>
    public static bool ShouldApplyContentResize(int appliedHeight, int reportedHeight) =>
        appliedHeight != reportedHeight;

    /// <summary>
    /// Initial show after the first valid content-size once the hidden HWND has been
    /// primed to 376 epx width (<see cref="AppHost"/>). A second settle report is not
    /// required: stable fixtures (empty) may only fire ResizeObserver once, and duplicate
    /// heights are coalesced by the frontend. With live sizing (0012) the launch no longer
    /// freezes a stale height — later reports correct the visible window.
    /// </summary>
    public static bool ShouldCompleteInitialShow(int hiddenContentSizeReportsReceived) =>
        hiddenContentSizeReportsReceived >= 1;

    public static bool ShouldClearInitialShowRequest(bool flyoutVisibleAfterShow) =>
        flyoutVisibleAfterShow;

    public static bool ShouldHonorLightDismiss(bool suppress, bool armed, bool pinned) =>
        !suppress && armed && !pinned;

    /// <summary>Manual show satisfies the pending initial-show request (decision 0012 cleanup):
    /// the user already has the flyout; a later automatic show would be surprising.</summary>
    public static bool ShouldClearPendingInitialShowOnManualShow(bool pending, bool completed) =>
        pending && !completed;

    /// <summary>
    /// Unpinned bottom-taskbar placement helper (still used for show / DPI re-place).
    /// </summary>
    public static Rectangle BoundsFromFixedBottom(int x, int width, int height, int capturedBottom) =>
        new(x, capturedBottom - height, width, height);
}
