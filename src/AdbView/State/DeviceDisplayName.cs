namespace AdbView.State;

using System;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Single DeviceCard / notification / pairing display-name resolver.
/// Marketing name → Android device name → model → brand+model → safe fallback.
/// Transport identities are never presentation names.
/// </summary>
internal static partial class DeviceDisplayName {
    /// <summary>
    /// Resolve a human presentation title from device-supplied metadata candidates.
    /// </summary>
    /// <param name="marketingName"><c>ro.product.marketname</c></param>
    /// <param name="deviceName"><c>settings get global device_name</c></param>
    /// <param name="model"><c>ro.product.model</c> or tracker model (early)</param>
    /// <param name="brand"><c>ro.product.brand</c></param>
    /// <param name="manufacturer"><c>ro.product.manufacturer</c></param>
    /// <param name="product">Tracker product field (early fallback only)</param>
    /// <param name="givenName">mDNS given_name when not protocol-shaped</param>
    public static string Resolve(
        string? marketingName = null,
        string? deviceName = null,
        string? model = null,
        string? brand = null,
        string? manufacturer = null,
        string? product = null,
        string? givenName = null) {
        if (TryAccept(marketingName, out string market)) {
            return market;
        }

        if (TryAccept(deviceName, out string named)) {
            return named;
        }

        if (TryAccept(givenName, out string given)) {
            return given;
        }

        if (TryAccept(model, out string modelValue)) {
            string? brandLabel = PreferBrandLabel(brand, manufacturer);
            if (brandLabel is not null
                && LooksLikeTechnicalModelCode(modelValue)
                && !ModelAlreadyIncludesBrand(modelValue, brandLabel)) {
                return brandLabel + " " + modelValue;
            }

            return modelValue;
        }

        if (TryAccept(product, out string productValue)) {
            return productValue;
        }

        return Vocabulary.Pairing.DefaultDeviceName;
    }

    /// <summary>Early tracker-only path (no shell naming metadata yet).</summary>
    public static string ResolveEarly(string? givenName, string? model, string? product) =>
        Resolve(givenName: givenName, model: model, product: product);

    internal static bool TryAccept(string? raw, out string cleaned) {
        cleaned = string.Empty;
        if (raw is null) {
            return false;
        }

        string trimmed = NormalizeCandidate(raw);
        if (trimmed.Length == 0) {
            return false;
        }

        if (IsRejectedToken(trimmed) || IsTransportLike(trimmed)) {
            return false;
        }

        cleaned = trimmed;
        return true;
    }

    internal static string NormalizeCandidate(string raw) {
        // Trim CR/LF and surrounding whitespace; preserve internal Unicode.
        return raw.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    internal static bool IsRejectedToken(string value) {
        return value.Equals("null", StringComparison.OrdinalIgnoreCase)
            || value.Equals("unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Transport / protocol artifacts must never become DisplayName.</summary>
    internal static bool IsTransportLike(string value) {
        if (ProtocolInstanceRegex().IsMatch(value.Trim())) {
            return true;
        }

        if (EndpointRegex().IsMatch(value)) {
            return true;
        }

        if (TlsServiceResidueRegex().IsMatch(value)) {
            return true;
        }

        return false;
    }

    internal static string? PreferBrandLabel(string? brand, string? manufacturer) {
        if (TryAccept(brand, out string b)) {
            return TitleCaseBrand(b);
        }

        if (TryAccept(manufacturer, out string m)) {
            return TitleCaseBrand(m);
        }

        return null;
    }

    internal static string TitleCaseBrand(string value) {
        if (value.Length == 0) {
            return value;
        }

        // Cosmetic only: samsung → Samsung. Leave mixed-case OEM strings alone.
        bool allLower = true;
        foreach (char c in value) {
            if (char.IsLetter(c) && !char.IsLower(c)) {
                allLower = false;
                break;
            }
        }

        if (!allLower) {
            return value;
        }

        return char.ToUpper(value[0], CultureInfo.InvariantCulture) + value[1..];
    }

    internal static bool ModelAlreadyIncludesBrand(string model, string brand) {
        return model.StartsWith(brand + " ", StringComparison.OrdinalIgnoreCase)
            || model.Equals(brand, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Codes like SM-A065F / 24129PN74G lack spaces; marketed names usually contain spaces.
    /// </summary>
    internal static bool LooksLikeTechnicalModelCode(string model) =>
        !model.Contains(' ', StringComparison.Ordinal);

    [GeneratedRegex(@"^(adb|studio)-", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex ProtocolInstanceRegex();

    [GeneratedRegex(@"^.+:\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointRegex();

    [GeneratedRegex(@"_adb-tls-(?:connect|pairing)\._tcp", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TlsServiceResidueRegex();
}
