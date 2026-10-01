#pragma warning disable CA1707
#pragma warning disable CA1861

namespace AdbView.Tests.Host;

using System;
using System.Collections.Generic;
using System.IO;
using AdbView.Host;
using AdbView.State;
using Microsoft.Win32;
using Xunit;

public sealed class StartupRegistrationTests {
    private sealed class FakeRunKey : IRunKeyStore {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string name) =>
            _values.TryGetValue(name, out string? v) ? v : null;

        public void SetValue(string name, string value) => _values[name] = value;

        public void DeleteValue(string name) => _values.Remove(name);

        public string[] GetValueNames() {
            var names = new string[_values.Count];
            _values.Keys.CopyTo(names, 0);
            return names;
        }

        public int Count => _values.Count;
    }

    [Fact]
    public void A_Disabled_Enable_Writes_Quoted_Path_And_Startup() {
        var store = new FakeRunKey();
        string exe = @"C:\Program Files\ADBView\AdbView.exe";
        var reg = new StartupRegistration(store, () => exe);

        Assert.False(reg.GetState().IsRegistered);
        Assert.True(reg.Enable());

        string? cmd = store.GetValue(StartupRegistrationContract.ValueName);
        Assert.Equal("\"" + exe + "\" --startup", cmd);
        Assert.True(reg.GetState().IsRegistered);
    }

    [Fact]
    public void B_Enable_Twice_Single_Value() {
        var store = new FakeRunKey();
        store.SetValue("OtherApp", "C:\\Other\\app.exe");
        var reg = new StartupRegistration(store, () => @"D:\ADB\AdbView.exe");

        Assert.True(reg.Enable());
        Assert.True(reg.Enable());
        Assert.Equal(2, store.Count);
        Assert.Equal(1, CountNamed(store, StartupRegistrationContract.ValueName));
    }

    [Fact]
    public void C_Enabled_Disable_Removes_Value() {
        var store = new FakeRunKey();
        var reg = new StartupRegistration(store, () => @"C:\AdbView.exe");
        Assert.True(reg.Enable());
        Assert.True(reg.Disable());
        Assert.Null(store.GetValue(StartupRegistrationContract.ValueName));
        Assert.False(reg.GetState().IsRegistered);
    }

    [Fact]
    public void D_E_Path_With_Spaces_Quoted_Startup_Once() {
        string cmd = StartupRegistrationContract.FormatCommand(@"C:\Users\Test User\Apps\AdbView.exe");
        Assert.StartsWith("\"", cmd, StringComparison.Ordinal);
        Assert.EndsWith("\" --startup", cmd, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(cmd, "--startup"));
        Assert.True(StartupRegistrationContract.TryParseCommand(cmd, out string? path));
        Assert.Equal(@"C:\Users\Test User\Apps\AdbView.exe", path);
    }

    [Fact]
    public void F_Stale_Path_Enable_Corrects_To_Current() {
        var store = new FakeRunKey();
        store.SetValue(StartupRegistrationContract.ValueName, "\"C:\\Old\\AdbView.exe\" --startup");
        string current = @"C:\New\AdbView.exe";
        var reg = new StartupRegistration(store, () => current);
        Assert.True(reg.Enable());
        Assert.Equal("\"" + current + "\" --startup", store.GetValue(StartupRegistrationContract.ValueName));
    }

    [Fact]
    public void G_Unrelated_Run_Entries_Preserved() {
        var store = new FakeRunKey();
        store.SetValue("KeepMe", "keep.exe");
        var reg = new StartupRegistration(store, () => @"C:\AdbView.exe");
        Assert.True(reg.Enable());
        Assert.True(reg.Disable());
        Assert.Equal("keep.exe", store.GetValue("KeepMe"));
    }

    [Fact]
    public void H_Installer_App_Contract_Same_Value_Name_And_Argument() {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string iss = File.ReadAllText(Path.Combine(root, "installer", "AdbView.iss"));
        Assert.Contains("ValueName: \"" + StartupRegistrationContract.ValueName + "\"", iss, StringComparison.Ordinal);
        Assert.Contains("--startup", iss, StringComparison.Ordinal);
        Assert.Equal("ADB View", StartupRegistrationContract.ValueName);
        Assert.Equal("--startup", StartupRegistrationContract.StartupArgument);
    }

    [Fact]
    public void Rapid_Enable_Disable_No_Duplicate_Entries() {
        var store = new FakeRunKey();
        store.SetValue("Other", "x");
        var reg = new StartupRegistration(store, () => @"C:\Portable\AdbView.exe");
        for (int i = 0; i < 25; i++) {
            Assert.True(reg.Enable());
            Assert.True(reg.Disable());
        }

        Assert.Null(store.GetValue(StartupRegistrationContract.ValueName));
        Assert.Equal(1, store.Count);
    }

    private static int CountNamed(FakeRunKey store, string name) {
        int n = 0;
        foreach (string v in store.GetValueNames()) {
            if (string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) {
                n++;
            }
        }

        return n;
    }

    private static int CountOccurrences(string haystack, string needle) {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}

public sealed class UserPreferencesTests {
    private sealed class FakeStore : IPreferenceStore {
        private readonly Dictionary<string, (object Value, RegistryValueKind Kind)> _data = new(StringComparer.OrdinalIgnoreCase);

        public object? GetValue(string name) =>
            _data.TryGetValue(name, out var e) ? e.Value : null;

        public void SetValue(string name, object value, RegistryValueKind kind) =>
            _data[name] = (value, kind);
    }

    [Fact]
    public void Defaults_When_Absent() {
        var prefs = new UserPreferences(new FakeStore());
        Assert.True(prefs.NotificationsEnabled);
        Assert.Equal(ThemePreference.System, prefs.ThemePreference);
    }

    [Fact]
    public void Notifications_Persist_RoundTrip() {
        var store = new FakeStore();
        var prefs = new UserPreferences(store);
        Assert.True(prefs.SetNotificationsEnabled(false));
        Assert.False(prefs.NotificationsEnabled);
        Assert.True(prefs.SetNotificationsEnabled(true));
        Assert.True(prefs.NotificationsEnabled);
    }

    [Fact]
    public void Theme_Corrupt_Falls_Back_System() {
        var store = new FakeStore();
        store.SetValue(UserPreferences.ThemeValueName, "Neon", RegistryValueKind.String);
        var prefs = new UserPreferences(store);
        Assert.Equal(ThemePreference.System, prefs.ThemePreference);
    }

    [Fact]
    public void Theme_RoundTrip_And_Rapid_Toggle() {
        var store = new FakeStore();
        var prefs = new UserPreferences(store);
        ThemePreference[] cycle = [ThemePreference.System, ThemePreference.Light, ThemePreference.Dark];
        for (int i = 0; i < 50; i++) {
            ThemePreference next = cycle[i % cycle.Length];
            Assert.True(prefs.SetThemePreference(next));
            Assert.Equal(next, prefs.ThemePreference);
        }
    }
}

public sealed class EffectiveThemeTests {
    [Fact]
    public void Resolve_Matrix() {
        Assert.Equal(ThemeKind.Light, EffectiveTheme.Resolve(ThemePreference.System, ThemeKind.Light));
        Assert.Equal(ThemeKind.Dark, EffectiveTheme.Resolve(ThemePreference.System, ThemeKind.Dark));
        Assert.Equal(ThemeKind.Light, EffectiveTheme.Resolve(ThemePreference.Light, ThemeKind.Dark));
        Assert.Equal(ThemeKind.Dark, EffectiveTheme.Resolve(ThemePreference.Dark, ThemeKind.Light));
    }

    [Fact]
    public void Override_Ignores_System_Change_Until_System_Selected() {
        Assert.Equal(ThemeKind.Dark, EffectiveTheme.Resolve(ThemePreference.Dark, ThemeKind.Light));
        Assert.Equal(ThemeKind.Dark, EffectiveTheme.Resolve(ThemePreference.Dark, ThemeKind.Dark));
        Assert.Equal(ThemeKind.Light, EffectiveTheme.Resolve(ThemePreference.System, ThemeKind.Light));
    }
}
