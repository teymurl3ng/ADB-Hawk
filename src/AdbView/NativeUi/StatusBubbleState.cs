namespace AdbView.NativeUi;

using AdbView.State;

/// <summary>Bubble colour family (decision 0020 / DESIGN.md §30). Mirrors the tray marker
/// families; the bubble never invents a colour of its own.</summary>
internal enum BubbleColor {
    None,
    Green,
    Amber,
    Blue,
    Red,
}

/// <summary>Whether a state persists while its condition holds or shows briefly and hides.</summary>
internal enum BubbleLifetime {
    /// <summary>Persists while the condition is true (connected / attention / pairing available).</summary>
    Permanent,

    /// <summary>Shows for the transient window, then the bubble hides (decision 0020).</summary>
    Transient,
}

/// <summary>What the bubble shows right now. <c>null</c> means "nothing to report — stay hidden".</summary>
internal sealed record BubbleState(BubbleColor Color, BubbleLifetime Lifetime, string Detail);

/// <summary>
/// Maps the product's existing aggregate tray status onto the bubble's state (DESIGN.md §30).
/// Pure: no UI, no timers, no Windows APIs — the presenter owns lifetime, the window owns pixels.
/// </summary>
internal static class StatusBubblePolicy {
    /// <summary>The aggregate has nothing to say: no devices, no offers, ADB healthy.</summary>
    public static BubbleState? Nothing => null;

    /// <summary>The "just disconnected" episode (decision 0020): today a drop collapses the
    /// aggregate straight to idle, so this state exists only here.</summary>
    public static BubbleState Disconnected(string detail) =>
        new(BubbleColor.Red, BubbleLifetime.Transient, detail);

    /// <summary>DESIGN.md §30: green connected, amber attention, blue/cyan pairing available are
    /// permanent; red global failure and blue special mode are transient; idle is hidden.</summary>
    public static BubbleState? FromTrayStatus(TrayStatusKind status, string detail) => status switch {
        TrayStatusKind.Connected => new BubbleState(BubbleColor.Green, BubbleLifetime.Permanent, detail),
        TrayStatusKind.Attention => new BubbleState(BubbleColor.Amber, BubbleLifetime.Permanent, detail),
        TrayStatusKind.PairingAvailable => new BubbleState(BubbleColor.Blue, BubbleLifetime.Permanent, detail),
        TrayStatusKind.Failure => new BubbleState(BubbleColor.Red, BubbleLifetime.Transient, detail),
        TrayStatusKind.Special => new BubbleState(BubbleColor.Blue, BubbleLifetime.Transient, detail),
        _ => Nothing,
    };

    /// <summary>True when this aggregate means "a phone is attached and usable" — used to know
    /// whether a later idle aggregate is a drop worth reporting.</summary>
    public static bool IsConnection(TrayStatusKind status) =>
        status is TrayStatusKind.Connected or TrayStatusKind.Attention;

    /// <summary>The resource key for a colour family's base token (the bubble's fill).</summary>
    public static string TokenKey(BubbleColor color) => color switch {
        BubbleColor.Green => "Win.Green",
        BubbleColor.Amber => "Win.Amber",
        BubbleColor.Blue => "Win.Sky",
        BubbleColor.Red => "Win.Red",
        _ => "Win.BorderSubtle",
    };
}
