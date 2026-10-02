namespace AdbView.Host;

using System.Drawing;
using AdbView.State;
using WpfMedia = System.Windows.Media;

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

    /// <summary>WPF brush twin for the native shell (decision 0015 Phase I1).</summary>
    public static WpfMedia.SolidColorBrush OpaqueBrush(ThemeKind theme) {
        var color = OpaqueBackColor(theme);
        var brush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
