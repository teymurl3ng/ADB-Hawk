namespace AdbView.NativeUi.Shell;

using System;
using System.Globalization;

/// <summary>Age text formatting — exact port of ui/src/bridge/age.ts.</summary>
internal static class AgeFormatter {
    public static string Format(string template, DateTimeOffset since, DateTimeOffset now) {
        var delta = now - since;
        if (delta < TimeSpan.Zero) {
            delta = TimeSpan.Zero;
        }

        string age;
        if (delta.TotalSeconds < 5) {
            age = "Just now";
        }
        else if (delta.TotalSeconds < 60) {
            age = string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalSeconds}s ago");
        }
        else if (delta.TotalMinutes < 60) {
            age = delta.TotalMinutes == 2
                ? "2 minutes ago"
                : string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalMinutes}m ago");
        }
        else {
            age = string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalHours}h ago");
        }

        return template.Replace("{age}", age, StringComparison.Ordinal);
    }
}

