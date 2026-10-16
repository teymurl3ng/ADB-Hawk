namespace AdbView.Host;

using AdbView.State;

/// <summary>
/// Resolves <see cref="ThemePreference"/> + current Windows apps theme → effective <see cref="ThemeKind"/>.
/// </summary>
internal static class EffectiveTheme {
    public static ThemeKind Resolve(ThemePreference preference, ThemeKind systemAppsTheme) =>
        preference switch {
            ThemePreference.Light => ThemeKind.Light,
            ThemePreference.Dark => ThemeKind.Dark,
            _ => systemAppsTheme,
        };
}
