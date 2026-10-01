namespace AdbView.Host;

using System;
using System.IO;
using Microsoft.Win32;
using Serilog;

/// <summary>
/// Shared contract with installer/AdbView.iss — single HKCU Run value for Start with Windows.
/// </summary>
internal static class StartupRegistrationContract {
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ADB View";
    public const string StartupArgument = "--startup";

    /// <summary>
    /// Builds the exact Run command: quoted absolute path + single --startup.
    /// </summary>
    public static string FormatCommand(string executablePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string full = Path.GetFullPath(executablePath);
        return "\"" + full + "\" " + StartupArgument;
    }

    public static bool TryParseCommand(string? command, out string? executablePath) {
        executablePath = null;
        if (string.IsNullOrWhiteSpace(command)) {
            return false;
        }

        string trimmed = command.Trim();
        if (trimmed.StartsWith('\"')) {
            int end = trimmed.IndexOf('\"', 1);
            if (end <= 1) {
                return false;
            }

            executablePath = trimmed[1..end];
            string rest = trimmed[(end + 1)..].Trim();
            return rest.Equals(StartupArgument, StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith(StartupArgument + " ", StringComparison.OrdinalIgnoreCase);
        }

        // Unquoted path: split on first " --startup"
        int arg = trimmed.IndexOf(" " + StartupArgument, StringComparison.OrdinalIgnoreCase);
        if (arg <= 0) {
            return false;
        }

        executablePath = trimmed[..arg].Trim();
        return executablePath.Length > 0;
    }
}

internal interface IStartupRegistration {
    StartupRegistrationState GetState();
    bool Enable();
    bool Disable();
}

internal sealed record StartupRegistrationState(bool IsRegistered, string? Command);

internal interface IRunKeyStore {
    string? GetValue(string name);
    void SetValue(string name, string value);
    void DeleteValue(string name);
    string[] GetValueNames();
}

internal sealed class RegistryRunKeyStore : IRunKeyStore {
    public string? GetValue(string name) {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistrationContract.RunSubKey, writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value) {
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistrationContract.RunSubKey, writable: true)
            ?? throw new InvalidOperationException("Could not open HKCU Run key");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name) {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistrationContract.RunSubKey, writable: true);
        if (key is null) {
            return;
        }

        try {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (ArgumentException) {
            // missing value
        }
    }

    public string[] GetValueNames() {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistrationContract.RunSubKey, writable: false);
        return key?.GetValueNames() ?? Array.Empty<string>();
    }
}

internal sealed class StartupRegistration : IStartupRegistration {
    private readonly IRunKeyStore _store;
    private readonly Func<string> _executablePath;

    public StartupRegistration()
        : this(new RegistryRunKeyStore(), static () => Environment.ProcessPath
            ?? throw new InvalidOperationException("ProcessPath unavailable")) {
    }

    internal StartupRegistration(IRunKeyStore store, Func<string> executablePath) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
    }

    public StartupRegistrationState GetState() {
        try {
            string? command = _store.GetValue(StartupRegistrationContract.ValueName);
            if (string.IsNullOrWhiteSpace(command)) {
                return new StartupRegistrationState(false, null);
            }

            bool valid = StartupRegistrationContract.TryParseCommand(command, out _);
            return new StartupRegistrationState(valid, command);
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "StartupRegistration: failed to read Run value");
            return new StartupRegistrationState(false, null);
        }
#pragma warning restore CA1031
    }

    public bool Enable() {
        try {
            string path = _executablePath();
            string command = StartupRegistrationContract.FormatCommand(path);
            _store.SetValue(StartupRegistrationContract.ValueName, command);
            StartupRegistrationState state = GetState();
            return state.IsRegistered
                && string.Equals(state.Command, command, StringComparison.OrdinalIgnoreCase);
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "StartupRegistration: failed to enable");
            return false;
        }
#pragma warning restore CA1031
    }

    public bool Disable() {
        try {
            _store.DeleteValue(StartupRegistrationContract.ValueName);
            return !GetState().IsRegistered;
        }
#pragma warning disable CA1031
        catch (Exception ex) {
            Log.Warning(ex, "StartupRegistration: failed to disable");
            return false;
        }
#pragma warning restore CA1031
    }
}
