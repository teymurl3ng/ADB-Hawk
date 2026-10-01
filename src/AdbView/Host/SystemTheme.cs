namespace AdbView.Host;

using System;
using System.Windows.Forms;
using AdbView.State;
using Microsoft.Win32;
using Serilog;

/// <summary>
/// Reads Windows appearance preferences for shell.theme / transparency / forced colors.
/// Host-owned (WinForms HighContrast probe); State never references this type.
/// </summary>
internal static class SystemTheme {
    public static ThemeKind ReadAppsTheme() {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light && light == 1) {
                return ThemeKind.Light;
            }
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "SystemTheme: failed to read AppsUseLightTheme; defaulting Dark");
        }
#pragma warning restore CA1031

        return ThemeKind.Dark;
    }

    public static bool ReadTransparencyEnabled() {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("EnableTransparency") is int enabled) {
                return enabled != 0;
            }
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "SystemTheme: failed to read EnableTransparency; defaulting true");
        }
#pragma warning restore CA1031

        return true;
    }

    public static bool ReadForcedColors() {
        try {
            return SystemInformation.HighContrast;
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "SystemTheme: HighContrast probe failed");
            return false;
        }
#pragma warning restore CA1031
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>Hand-written P/Invoke (deliberate CsWin32 deviation, same precedent as
    /// GetExtendedUdpTable — the generator OOMs under this machine's memory pressure).</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = false)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    /// <summary>OS "show animations in Windows" (client-area animation) preference — decision
    /// 0013: when off, all flyout presentation animation reduces to instant steps.</summary>
    public static bool ReadClientAreaAnimation() {
        bool enabled = true;
#pragma warning disable CA1031 // R-08: probe failure is logged; the OS default (on) is kept.
        try {
            // Off (false) is a real, user-chosen state — only a probe failure keeps the default.
            return SystemParametersInfoW(SpiGetClientAreaAnimation, 0, ref enabled, 0) && enabled;
        }
        catch (Exception ex) {
            Log.Warning(ex, "SystemTheme: client-area animation probe failed; defaulting on");
            return true;
        }
#pragma warning restore CA1031
    }
}
