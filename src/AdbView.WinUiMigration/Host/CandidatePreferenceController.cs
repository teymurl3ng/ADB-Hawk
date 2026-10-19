namespace AdbView.WinUiMigration.Host;

using AdbView.Host;
using Serilog;

internal sealed record TrayPreferenceState(bool Startup, bool Notifications, bool Bubble, ThemePreference Theme) {
    public static TrayPreferenceState Default { get; } = new(false, true, false, ThemePreference.System);
}

// Owns only preference intent/readback. Device, pin and effective appearance truth stay in StateEngine.
internal sealed class CandidatePreferenceController(IUserPreferences preferences, Action<bool> applyNotifications,
    Action<ThemePreference> applyTheme, IStartupRegistration? startup = null) {
    public TrayPreferenceState Execute(uint command) {
        bool accepted = command switch {
            0 => true,
            2 => startup is null ? StartupUncomposed() : startup.GetState().IsRegistered ? startup.Disable() : startup.Enable(),
            3 => preferences.SetNotificationsEnabled(!preferences.NotificationsEnabled),
            4 => preferences.SetStatusBubbleEnabled(!preferences.StatusBubbleEnabled),
            10 => preferences.SetThemePreference(ThemePreference.System),
            11 => preferences.SetThemePreference(ThemePreference.Light),
            12 => preferences.SetThemePreference(ThemePreference.Dark),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        if (!accepted) { Log.Warning("Candidate preferences: command {Command} was not persisted; refreshing actual values", command); }
        var state = new TrayPreferenceState(startup?.GetState().IsRegistered ?? false, preferences.NotificationsEnabled, preferences.StatusBubbleEnabled, preferences.ThemePreference);
        applyNotifications(state.Notifications); applyTheme(state.Theme);
        Log.Debug("Candidate preferences readback: notifications {Notifications}, bubble {Bubble}, theme {Theme}", state.Notifications, state.Bubble, state.Theme);
        return state;
    }
    private static bool StartupUncomposed() {
        Log.Warning("Candidate startup registration remains uncomposed in M08; no Run value was changed");
        return false;
    }
}
