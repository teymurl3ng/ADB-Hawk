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
}
