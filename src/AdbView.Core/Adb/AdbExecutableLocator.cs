namespace AdbView.Adb;

using System;
using System.IO;

internal interface IAdbExecutableLocator {
    string? Locate();
}

internal sealed class AdbExecutableLocator : IAdbExecutableLocator {
    private readonly Func<string, string?> _getEnv;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<Environment.SpecialFolder, string> _getFolderPath;

    public AdbExecutableLocator(
        Func<string, string?>? getEnv = null,
        Func<string, bool>? fileExists = null,
        Func<Environment.SpecialFolder, string>? getFolderPath = null) {
        _getEnv = getEnv ?? (k => Environment.GetEnvironmentVariable(k));
        _fileExists = fileExists ?? (p => File.Exists(p));
        _getFolderPath = getFolderPath ?? (f => Environment.GetFolderPath(f));
    }

    public string? Locate() {
        // 1. ANDROID_SDK_ROOT\platform-tools\adb.exe
        var sdkRoot = _getEnv("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(sdkRoot)) {
            var candidate = Path.Combine(sdkRoot.Trim(), "platform-tools", "adb.exe");
            if (TryValidateCandidate(candidate, out var fullPath)) {
                return fullPath;
            }
        }

        // 2. ANDROID_HOME\platform-tools\adb.exe
        var androidHome = _getEnv("ANDROID_HOME");
        if (!string.IsNullOrWhiteSpace(androidHome)) {
            var candidate = Path.Combine(androidHome.Trim(), "platform-tools", "adb.exe");
            if (TryValidateCandidate(candidate, out var fullPath)) {
                return fullPath;
            }
        }

        // 3. PATH entries (manually enumerated; empty/whitespace entries skipped to avoid CWD)
        var pathVar = _getEnv("PATH");
        if (!string.IsNullOrWhiteSpace(pathVar)) {
            var entries = pathVar.Split(Path.PathSeparator, StringSplitOptions.None);
            foreach (var rawEntry in entries) {
                if (string.IsNullOrWhiteSpace(rawEntry)) {
                    // Empty PATH entry on Windows/DOS means current directory — strictly ignored (R-13).
                    continue;
                }

                var entry = rawEntry.Trim();
                try {
                    if (entry.IndexOfAny(Path.GetInvalidPathChars()) >= 0) {
                        continue;
                    }

                    var candidate = Path.Combine(entry, "adb.exe");
                    if (TryValidateCandidate(candidate, out var fullPath)) {
                        return fullPath;
                    }
                }
                catch (ArgumentException) {
                    // Malformed entry safely skipped
                }
            }
        }

        // 4. %LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe
        var localAppData = _getEnv("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData)) {
            localAppData = _getFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        if (!string.IsNullOrWhiteSpace(localAppData)) {
            var candidate = Path.Combine(localAppData.Trim(), "Android", "Sdk", "platform-tools", "adb.exe");
            if (TryValidateCandidate(candidate, out var fullPath)) {
                return fullPath;
            }
        }

        // 5. %ProgramFiles%\Android\android-sdk\platform-tools\adb.exe
        var programFiles = _getEnv("ProgramFiles");
        if (string.IsNullOrWhiteSpace(programFiles)) {
            programFiles = _getFolderPath(Environment.SpecialFolder.ProgramFiles);
        }
        if (!string.IsNullOrWhiteSpace(programFiles)) {
            var candidate = Path.Combine(programFiles.Trim(), "Android", "android-sdk", "platform-tools", "adb.exe");
            if (TryValidateCandidate(candidate, out var fullPath)) {
                return fullPath;
            }
        }

        return null;
    }

    private bool TryValidateCandidate(string candidatePath, out string fullPath) {
        try {
            if (_fileExists(candidatePath)) {
                fullPath = Path.GetFullPath(candidatePath);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) {
            // Unreadable / security / access exception safely ignored
        }

        fullPath = string.Empty;
        return false;
    }
}
