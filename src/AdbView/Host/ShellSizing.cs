namespace AdbView.Host;

/// <summary>
/// Self-sizing math for the native flyout (decision 0021, amending the native gap left by
/// decisions 0012/0013): the window follows its content as ONE instant step per settled change —
/// never interpolated, never per frame — with the tray-adjacent (bottom) edge frozen and the
/// 560 DIP ceiling enforced, beyond which the stable scroll container takes over. The historical
/// jank this area is feared for came from per-frame CHILD resizes under WebView2, a cause
/// decision 0015 removed; the instant-step contract of 0012 is unchanged.
/// Pure math: no WPF, no timers, no Windows APIs.
/// </summary>
internal static class ShellSizing {
    /// <summary>DESIGN §4 width is fixed; only height is self-sized.</summary>
    public const double WidthDip = 376;

    /// <summary>ENGINEERING row 29 / DESIGN §19: overflow past this scrolls in the stable container.</summary>
    public const double MaxHeightDip = 560;

    /// <summary>Floor: header + the smallest meaningful content (prevents collapse on transient emptiness).</summary>
    public const double MinHeightDip = 160;

    /// <summary>
    /// Computes the window's next height for a measured content height.
    /// </summary>
    /// <param name="desiredContentHeightDip">The shell's full desired height (measured with infinite
    /// available height), including header and padding.</param>
    /// <param name="workAreaTopDip">The top of the monitor's work area in DIP; the window's top edge
    /// must never rise above it.</param>
    /// <param name="currentBottomDip">The window's current bottom edge in DIP (frozen by contract).</param>
    /// <returns>The target height and top edge, or null when the change is below the step threshold.</returns>
    public static (double Height, double Top)? Step(
        double desiredContentHeightDip,
        double workAreaTopDip,
        double currentBottomDip) {
        double clamped = Math.Clamp(desiredContentHeightDip, MinHeightDip, MaxHeightDip);

        // Freeze the bottom edge: growth extends upwards, shrink pulls the top down.
        double top = currentBottomDip - clamped;

        // Never poke above the work area: cap the height so the top edge stays inside.
        double available = currentBottomDip - workAreaTopDip;
        if (top < workAreaTopDip - 0.5) {
            clamped = Math.Min(clamped, Math.Max(MinHeightDip, available));
            top = currentBottomDip - clamped;
        }

        return (clamped, top);
    }

    /// <summary>True when the target differs from the applied height enough to be worth a step
    /// (sub-DIP differences are layout noise; the 0012 dedup rule).</summary>
    public static bool IsWorthAStep(double targetHeightDip, double appliedHeightDip) =>
        Math.Abs(targetHeightDip - appliedHeightDip) >= 0.5;
}
