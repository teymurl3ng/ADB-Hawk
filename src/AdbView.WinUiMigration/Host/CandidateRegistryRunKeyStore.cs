namespace AdbView.WinUiMigration.Host;

using AdbView.Host;
using Microsoft.Win32;

// The linked registration contract uses its original name; remap ONLY that name.
internal sealed class CandidateRegistryRunKeyStore : IRunKeyStore {
    public const string ValueName = "ADB View WinUI Migration";
    internal static string MapName(string name) => name == StartupRegistrationContract.ValueName
        ? ValueName : throw new ArgumentOutOfRangeException(nameof(name));
    public string? GetValue(string name) {
        string candidateName = MapName(name);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegistrationContract.RunSubKey, writable: false);
        return key?.GetValue(candidateName) as string;
    }
    public void SetValue(string name, string value) {
        string candidateName = MapName(name);
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(StartupRegistrationContract.RunSubKey, writable: true)
            ?? throw new InvalidOperationException("Candidate startup storage unavailable.");
        key.SetValue(candidateName, value, RegistryValueKind.String);
    }
    public void DeleteValue(string name) {
        string candidateName = MapName(name);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegistrationContract.RunSubKey, writable: true);
        key?.DeleteValue(candidateName, throwOnMissingValue: false);
    }
    public string[] GetValueNames() => GetValue(StartupRegistrationContract.ValueName) is null ? [] : [StartupRegistrationContract.ValueName];
}
