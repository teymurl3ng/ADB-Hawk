namespace AdbView.Host;

/// <summary>
/// User-selected theme mode for the flyout (tray menu). Distinct from <see cref="State.ThemeKind"/>,
/// which is the effective Light/Dark delivered to native + WebView.
/// </summary>
internal enum ThemePreference {
    System = 0,
    Light = 1,
    Dark = 2,
}
