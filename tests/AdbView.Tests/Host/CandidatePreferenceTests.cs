namespace AdbView.Tests.Host;

using AdbView.Bridge;
using AdbView.Host;
using AdbView.Notifications;
using AdbView.Presentation;
using AdbView.State;
using AdbView.Tests.Boundaries;
using AdbView.WinUiMigration.Host;
using Microsoft.Win32;
using Xunit;

public sealed class CandidatePreferenceTests {
    [Fact]
    public async Task SavedNotificationPolicyInitializesTheRuntimeAndThemeFlowsThroughTheReducer() {
        var store = new Store(); store.Values["NotificationsEnabled"] = 0; store.Values["Theme"] = "Light";
        var preferences = new UserPreferences(store);
        using var runtime = new BackendRuntime(FixtureSnapshotProvider.GetSnapshot("empty"));
        using var mailbox = new SnapshotMailbox(_ => true, _ => { });
        using var session = new LiveBackendSession(mailbox, NullNotificationSink.Instance,
            exception => throw new InvalidOperationException("Unexpected preference delivery failure.", exception), runtime,
            _ => { /* No socket services in this preference composition test. */ }, notificationsEnabled: preferences.NotificationsEnabled);
        Assert.False(runtime.NotificationCoordinator!.NotificationsEnabled);
        var owner = new CandidatePreferenceController(preferences, session.SetNotificationsEnabled,
            value => session.PostAppearance(new ShellAppearanceChangedEvent(EffectiveTheme.Resolve(value, ThemeKind.Dark), false, false, false)));
        owner.Execute(0);
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.Equal(ThemeKind.Light, runtime.StateEngine.CurrentSnapshot.Shell.Theme);
        Assert.False(runtime.NotificationCoordinator.NotificationsEnabled); Assert.Empty(store.Writes);
        owner.Execute(3); owner.Execute(12);
        await runtime.StateEngine.DrainAsync().ConfigureAwait(true);
        Assert.True(runtime.NotificationCoordinator.NotificationsEnabled);
        Assert.Equal(ThemeKind.Dark, runtime.StateEngine.CurrentSnapshot.Shell.Theme);
        Assert.Equal(["NotificationsEnabled", "Theme"], store.Writes);
    }
    [Fact]
    public void DefaultsAndSavedValuesUseTheExistingParserAcrossControllerReload() {
        var store = new Store();
        CandidatePreferenceController owner = Owner(store);
        Assert.Equal(TrayPreferenceState.Default, owner.Execute(0)); Assert.Empty(store.Writes);
        Assert.False(owner.Execute(3).Notifications);
        Assert.True(owner.Execute(4).Bubble);
        Assert.Equal(ThemePreference.Light, owner.Execute(11).Theme);
        var reloaded = Owner(store).Execute(0);
        Assert.Equal(new TrayPreferenceState(false, false, true, ThemePreference.Light), reloaded);
        Assert.Equal(["NotificationsEnabled", "StatusBubbleEnabled", "Theme"], store.Writes);
        Assert.Equal(RegistryValueKind.DWord, store.Kinds["NotificationsEnabled"]);
        Assert.Equal(RegistryValueKind.DWord, store.Kinds["StatusBubbleEnabled"]);
        Assert.Equal(RegistryValueKind.String, store.Kinds["Theme"]);
    }

    [Theory]
    [InlineData(3u, "NotificationsEnabled")]
    [InlineData(4u, "StatusBubbleEnabled")]
    [InlineData(10u, "Theme")]
    [InlineData(11u, "Theme")]
    [InlineData(12u, "Theme")]
    public void EachPreferenceCommandWritesOnlyItsOwnValue(uint command, string name) {
        var store = new Store(); Owner(store).Execute(command);
        Assert.Equal(name, Assert.Single(store.Writes));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(11u)]
    public void FailedWriteReadsBackActualChecksAndPolicyInsteadOfTheRequestedValue(uint command) {
        var store = new Store { FailWrite = true };
        bool? notifications = null; ThemePreference? theme = null;
        var owner = new CandidatePreferenceController(new UserPreferences(store), value => notifications = value, value => theme = value);
        Assert.Equal(TrayPreferenceState.Default, owner.Execute(command));
        Assert.True(notifications); Assert.Equal(ThemePreference.System, theme);
        Assert.Single(store.Writes); Assert.Empty(store.Values);
    }

    [Fact]
    public void ReadOnlyRefreshAppliesExternalChangesWithoutAnyWrites() {
        var store = new Store(); bool? enabled = null; ThemePreference? applied = null;
        var owner = new CandidatePreferenceController(new UserPreferences(store), value => enabled = value, value => applied = value);
        owner.Execute(0);
        store.Values["NotificationsEnabled"] = 0; store.Values["StatusBubbleEnabled"] = 1; store.Values["Theme"] = "Dark";
        Assert.Equal(new TrayPreferenceState(false, false, true, ThemePreference.Dark), owner.Execute(0));
        Assert.False(enabled); Assert.Equal(ThemePreference.Dark, applied); Assert.Empty(store.Writes);
    }

    [Fact]
    public void CorruptValuesRetainExistingDefaultsAndRefreshDoesNotRepairTheStore() {
        var store = new Store();
        store.Values["NotificationsEnabled"] = "bad"; store.Values["StatusBubbleEnabled"] = "bad"; store.Values["Theme"] = "Neon";
        Assert.Equal(TrayPreferenceState.Default, Owner(store).Execute(0)); Assert.Empty(store.Writes);
    }

    [Fact]
    public void StartupIsExplicitlyUncomposedAndCannotWriteAnyPreferenceOrRunValue() {
        var store = new Store(); var owner = Owner(store);
        Assert.False(owner.Execute(2).Startup); Assert.False(owner.Execute(2).Startup); Assert.Empty(store.Writes);
        Assert.Throws<ArgumentOutOfRangeException>(() => owner.Execute(99)); Assert.Empty(store.Writes);
    }

    [Fact]
    public void CandidateRegistryOwnerHasAnIsolatedWriteTargetAndNoStartupAdapter() {
        Assert.IsAssignableFrom<IPreferenceStore>(new CandidateRegistryPreferenceStore()); // Construction performs no registry I/O.
        Assert.Equal(@"Software\ADBView.WinUiMigration", CandidateRegistryPreferenceStore.KeyPath);
        Assert.NotEqual(UserPreferences.KeyPath, CandidateRegistryPreferenceStore.KeyPath);
        string adapter = File.ReadAllText(Repo.RootFile("src", "AdbView.WinUiMigration", "Host", "CandidateRegistryPreferenceStore.cs"));
        Assert.Contains("CreateSubKey(KeyPath", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentVersion", adapter, StringComparison.Ordinal);
        string window = File.ReadAllText(Repo.RootFile("src", "AdbView.WinUiMigration", "CandidateWindow.xaml.cs"));
        Assert.Contains("bool notificationsEnabled = preferences.NotificationsEnabled", window, StringComparison.Ordinal);
        Assert.Contains("notificationsEnabled: notificationsEnabled", window, StringComparison.Ordinal);
        Assert.Contains("new StartupRegistration(new CandidateRegistryRunKeyStore()", window, StringComparison.Ordinal);
    }

    private static CandidatePreferenceController Owner(Store store) => new(new UserPreferences(store), _ => { }, _ => { });
    private sealed class Store : IPreferenceStore {
        public Dictionary<string, object> Values { get; } = [];
        public Dictionary<string, RegistryValueKind> Kinds { get; } = [];
        public List<string> Writes { get; } = [];
        public bool FailWrite { get; init; }
        public object? GetValue(string name) => Values.GetValueOrDefault(name);
        public void SetValue(string name, object value, RegistryValueKind kind) {
            Writes.Add(name);
            if (FailWrite) { throw new UnauthorizedAccessException("Injected candidate preference write denial."); }
            Values[name] = value; Kinds[name] = kind;
        }
    }
}
