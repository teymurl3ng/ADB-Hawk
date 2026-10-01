namespace AdbView.Host;

using System;
using Microsoft.Win32;
using Serilog;

/// <summary>
/// Per-user preferences stored under HKCU\Software\ADBView.
/// Notifications default ON; Theme defaults to System. Corrupt values fall back safely.
/// </summary>
internal interface IUserPreferences {
    bool NotificationsEnabled { get; }
    ThemePreference ThemePreference { get; }
    bool SetNotificationsEnabled(bool enabled);
    bool SetThemePreference(ThemePreference preference);
}

internal sealed class UserPreferences : IUserPreferences {
    internal const string KeyPath = @"Software\ADBView";
    internal const string NotificationsValueName = "NotificationsEnabled";
    internal const string ThemeValueName = "Theme";

    private readonly IPreferenceStore _store;

    public UserPreferences()
        : this(new RegistryPreferenceStore()) {
    }

    internal UserPreferences(IPreferenceStore store) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool NotificationsEnabled {
        get {
            try {
                object? raw = _store.GetValue(NotificationsValueName);
                if (raw is null) {
                    return true;
                }

                if (raw is int i) {
                    return i != 0;
                }

                if (raw is long l) {
                    return l != 0;
                }

                if (raw is string s && int.TryParse(s, out int parsed)) {
                    return parsed != 0;
                }
            }
#pragma warning disable CA1031
            catch (Exception ex) {
                Log.Warning(ex, "UserPreferences: failed to read NotificationsEnabled; defaulting true");
            }
#pragma warning restore CA1031

            return true;
        }
    }

    public ThemePreference ThemePreference {
        get {
            try {
                object? raw = _store.GetValue(ThemeValueName);
                if (raw is string s) {
                    return ParseTheme(s);
                }
            }
#pragma warning disable CA1031
            catch (Exception ex) {
                Log.Warning(ex, "UserPreferences: failed to read Theme; defaulting System");
            }
#pragma warning restore CA1031

            return ThemePreference.System;
        }
    }

    public bool SetNotificationsEnabled(bool enabled) {
        try {
            _store.SetValue(NotificationsValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
            return NotificationsEnabled == enabled;
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "UserPreferences: failed to write NotificationsEnabled={Enabled}", enabled);
            return false;
        }
#pragma warning restore CA1031
    }

    public bool SetThemePreference(ThemePreference preference) {
        try {
            _store.SetValue(ThemeValueName, FormatTheme(preference), RegistryValueKind.String);
            return ThemePreference == preference;
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "UserPreferences: failed to write Theme={Theme}", preference);
            return false;
        }
#pragma warning restore CA1031
    }

    internal static ThemePreference ParseTheme(string value) {
        if (string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase)) {
            return ThemePreference.Light;
        }

        if (string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase)) {
            return ThemePreference.Dark;
        }

        // System, empty, or corrupt → System
        return ThemePreference.System;
    }

    internal static string FormatTheme(ThemePreference preference) =>
        preference switch {
            ThemePreference.Light => "Light",
            ThemePreference.Dark => "Dark",
            _ => "System",
        };
}

internal interface IPreferenceStore {
    object? GetValue(string name);
    void SetValue(string name, object value, RegistryValueKind kind);
}

internal sealed class RegistryPreferenceStore : IPreferenceStore {
    public object? GetValue(string name) {
        using var key = Registry.CurrentUser.OpenSubKey(UserPreferences.KeyPath, writable: false);
        return key?.GetValue(name);
    }

    public void SetValue(string name, object value, RegistryValueKind kind) {
        using var key = Registry.CurrentUser.CreateSubKey(UserPreferences.KeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open HKCU\\" + UserPreferences.KeyPath);
        key.SetValue(name, value, kind);
    }
}
