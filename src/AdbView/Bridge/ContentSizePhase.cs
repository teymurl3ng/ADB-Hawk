namespace AdbView.Bridge;

/// <summary>
/// How a content-size report should drive the native flyout.
/// Live intermediates during a structural CSS transition must not fight the animator.
/// </summary>
internal enum ContentSizePhase {
    /// <summary>Ordinary content-driven resize (immediate).</summary>
    Live = 0,

    /// <summary>Authoritative final height for a structural expand/collapse (native animates).</summary>
    Structural = 1,

    /// <summary>Post-animation truthful measurement; snaps if needed.</summary>
    Reconcile = 2,
}
