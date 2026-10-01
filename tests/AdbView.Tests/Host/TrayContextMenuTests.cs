#pragma warning disable CA1707

namespace AdbView.Tests.Host;

using System;
using System.IO;
using AdbView.Host;
using Microsoft.Win32;
using Xunit;

public sealed class TrayContextMenuTests {
    private sealed class FakePrefs : IUserPreferences {
        public bool NotificationsEnabled { get; set; } = true;
        public ThemePreference ThemePreference { get; set; } = ThemePreference.System;

        public bool SetNotificationsEnabled(bool enabled) {
            NotificationsEnabled = enabled;
            return true;
        }

        public bool SetThemePreference(ThemePreference preference) {
            ThemePreference = preference;
            return true;
        }
    }

    private sealed class FakeStartup : IStartupRegistration {
        public bool Registered { get; set; }

        public StartupRegistrationState GetState() =>
            new(Registered, Registered ? "\"C:\\AdbView.exe\" --startup" : null);

        public bool Enable() {
            Registered = true;
            return true;
        }

        public bool Disable() {
            Registered = false;
            return true;
        }
    }

    private static readonly string[] ExpectedTopLevel = [
        "Open ADB View",
        "",
        "Start with Windows",
        "Notifications",
        "Theme",
        "",
        "Exit",
    ];

    private static readonly string[] ExpectedTheme = ["System", "Light", "Dark"];

    [Fact]
    public void Menu_Order_And_Theme_Children() {
        using var tray = new TrayIcon(new FakePrefs(), new FakeStartup());
        Assert.Equal(ExpectedTopLevel, tray.ContextMenuItemTexts);
        Assert.Equal(ExpectedTheme, tray.ThemeMenuItemTexts);
    }

    [Fact]
    public void Opening_Refresh_Reads_Authoritative_State() {
        var prefs = new FakePrefs {
            NotificationsEnabled = true,
            ThemePreference = ThemePreference.System,
        };
        var startup = new FakeStartup { Registered = false };
        using var tray = new TrayIcon(prefs, startup);
        tray.RefreshMenuChecksForTests();
        Assert.False(tray.StartupChecked);
        Assert.True(tray.NotificationsChecked);
        Assert.True(tray.ThemeSystemChecked);
        Assert.False(tray.ThemeLightChecked);
        Assert.False(tray.ThemeDarkChecked);

        // External change (installer / registry / another writer).
        startup.Registered = true;
        prefs.NotificationsEnabled = false;
        prefs.ThemePreference = ThemePreference.Dark;
        tray.RefreshMenuChecksForTests();

        Assert.True(tray.StartupChecked);
        Assert.False(tray.NotificationsChecked);
        Assert.False(tray.ThemeSystemChecked);
        Assert.False(tray.ThemeLightChecked);
        Assert.True(tray.ThemeDarkChecked);
    }

    [Fact]
    public void Theme_Exclusivity_Exactly_One_Checked() {
        var prefs = new FakePrefs();
        using var tray = new TrayIcon(prefs, new FakeStartup());
        foreach (ThemePreference p in Enum.GetValues<ThemePreference>()) {
            prefs.ThemePreference = p;
            tray.RefreshMenuChecksForTests();
            int checkedCount =
                (tray.ThemeSystemChecked ? 1 : 0)
                + (tray.ThemeLightChecked ? 1 : 0)
                + (tray.ThemeDarkChecked ? 1 : 0);
            Assert.Equal(1, checkedCount);
        }
    }

    [Fact]
    public void Explorer_Recovery_Leaves_Menu_Intact() {
        using var tray = new TrayIcon(new FakePrefs(), new FakeStartup());
        tray.HandleTaskbarCreated();
        Assert.Equal(ExpectedTopLevel, tray.ContextMenuItemTexts);
        Assert.Equal(ExpectedTheme, tray.ThemeMenuItemTexts);
    }
}

/// <summary>
/// M7 sentinel: AppHost must resolve effective theme through EffectiveTheme (native + snapshot).
/// </summary>
public sealed class EffectiveThemeSourceConsistencyTests {
    [Fact]
    public void AppHost_Uses_EffectiveTheme_Resolve_For_Shell_Appearance() {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string appHost = File.ReadAllText(Path.Combine(root, "src", "AdbView", "Host", "AppHost.cs"));
        Assert.Contains("EffectiveTheme.Resolve", appHost, StringComparison.Ordinal);
        Assert.Contains("ResolveEffectiveTheme()", appHost, StringComparison.Ordinal);
        // Must not bypass preference by posting SystemTheme alone for shell appearance.
        Assert.DoesNotContain(
            "new ShellAppearanceChangedEvent(\r\n            SystemTheme.ReadAppsTheme()",
            appHost,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new ShellAppearanceChangedEvent(\n            SystemTheme.ReadAppsTheme()",
            appHost,
            StringComparison.Ordinal);
    }
}
