namespace AdbView.Adb;

using System;

/// <summary>
/// Six-digit pairing code as a string (leading zeros preserved).
/// Exactly six ASCII decimal digits; no trim, spaces, signs, or Unicode digits.
/// </summary>
internal static class PairingCode {
    public const int RequiredLength = 6;

    public static bool IsValid(string? code) {
        if (code is null || code.Length != RequiredLength) {
            return false;
        }

        for (int i = 0; i < code.Length; i++) {
            char c = code[i];
            if (c is < '0' or > '9') {
                return false;
            }
        }

        return true;
    }
}
