namespace AdbView.Host;

using System.Drawing;
using AdbView.State;

/// <summary>
/// v1 material policy: intentional opaque surfaces only.
/// Live Acrylic under transparent WebView2 is deferred (decision 0004).
/// </summary>
internal static class ShellMaterialPolicy {
    /// <summary>Production always reports opaque; Acrylic deferred.</summary>
    public static bool ResolveTransparencyEnabled() => false;

    public static Color OpaqueBackColor(ThemeKind theme) =>
        theme == ThemeKind.Light
            ? Color.FromArgb(255, 243, 244, 246) // #F3F4F6
            : Color.FromArgb(255, 32, 35, 41);   // #202329
}
