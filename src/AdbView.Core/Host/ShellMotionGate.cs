namespace AdbView.Host;

/// <summary>
/// The reduced-motion gate (decision 0013, forensics 2026-10-02/04). Pure decision logic so the
/// rule is testable: the OS client-area-animation probe is the authority, the gate follows it
/// immediately (the next animation call site reads it synchronously), and the appearance event
/// carries the REAL value instead of a hardcoded one — the 2026-10-04 audit found the only
/// production producer of <c>ShellAppearanceChangedEvent</c> passed <c>ReducedMotion = false</c>
/// unconditionally, which permanently defeated the gate.
/// </summary>
internal static class ShellMotionGate {
    /// <summary>
    /// Folds one OS probe into the gate. The ctor-context probe is unreliable on some machines
    /// (documented at the startup post), so the gate STARTS assuming animations enabled and is
    /// corrected by the first reliable probe at flyout open.
    /// </summary>
    /// <param name="osAnimationsEnabled">The SPI_GETCLIENTAREAANIMATION probe result.</param>
    /// <param name="currentGate">The gate value before this probe.</param>
    /// <returns>The new gate value and whether it changed (change = log + appearance post).</returns>
    public static (bool Enabled, bool Changed) Apply(bool osAnimationsEnabled, bool currentGate) =>
        (osAnimationsEnabled, osAnimationsEnabled != currentGate);
}
