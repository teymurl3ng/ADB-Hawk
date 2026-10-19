namespace AdbView.WinUiMigration.Host;

using AdbView.Host;
using Microsoft.Win32;

// Executable-only storage. Never instantiate the linked production default store.
internal sealed class CandidateRegistryPreferenceStore : IPreferenceStore {
    public const string KeyPath = @"Software\ADBView.WinUiMigration";
    public object? GetValue(string name) {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(name);
    }
    public void SetValue(string name, object value, RegistryValueKind kind) {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("Candidate preferences: storage is unavailable.");
        key.SetValue(name, value, kind);
    }
}
