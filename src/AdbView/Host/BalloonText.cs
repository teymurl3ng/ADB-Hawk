namespace AdbView.Host;

using System;
using System.Text;

/// <summary>
/// Deterministic truncation to <c>NOTIFYICONDATAW</c> field capacities
/// (title 64 / body 256 WCHARs including null terminator).
/// </summary>
internal static class BalloonText {
    public const int TitleMaxChars = 63;
    public const int BodyMaxChars = 255;

    public static string TruncateTitle(string value) => Truncate(value, TitleMaxChars);

    public static string TruncateBody(string value) => Truncate(value, BodyMaxChars);

    public static string Truncate(string value, int maxChars) {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);

        if (value.Length <= maxChars) {
            return value;
        }

        var sb = new StringBuilder(maxChars);
        int count = 0;
        foreach (Rune rune in value.EnumerateRunes()) {
            int runeLen = rune.Utf16SequenceLength;
            if (count + runeLen > maxChars) {
                break;
            }

            sb.Append(rune.ToString());
            count += runeLen;
        }

        return sb.ToString();
    }
}
