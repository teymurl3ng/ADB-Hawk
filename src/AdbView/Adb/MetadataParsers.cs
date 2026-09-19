namespace AdbView.Adb;

using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdbView.Adb.Model;

/// <summary>
/// Strict parsers for metadata shell bodies (research/2026-09-19-metadata-host-transport-shell.md).
/// </summary>
internal static partial class MetadataParsers {
    /// <summary>
    /// AOSP <c>BatteryService</c> dumpsys prints <c>  level: N</c> as its own line.
    /// Must not match <c>Capacity level:</c> or OEM log fragments containing <c>level:</c>.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*level:[ \t]*(?<level>-?\d+)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BatteryLevelLineRegex();

    public static int? TryParseBatteryPercent(ReadOnlySpan<byte> rawUtf8) {
        string text = Encoding.UTF8.GetString(rawUtf8);
        Match match = BatteryLevelLineRegex().Match(text);
        if (!match.Success) {
            return null;
        }

        if (!int.TryParse(
                match.Groups["level"].ValueSpan,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int level)) {
            return null;
        }

        if (level < 0 || level > 100) {
            return null;
        }

        return level;
    }

    public static string? TryParseGetpropValue(ReadOnlySpan<byte> rawUtf8) {
        string text = Encoding.UTF8.GetString(rawUtf8).Trim();
        return text.Length == 0 ? null : text;
    }
}
