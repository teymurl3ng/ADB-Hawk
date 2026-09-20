namespace AdbView.State;

using System;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using AdbView.Adb.Model;

/// <summary>
/// Single presentation mapper for pairing-facing device labels (flyout + notifications).
/// Raw mDNS instance names (adb-* / studio-*) are never user-facing fallbacks.
/// </summary>
internal static partial class PairingDisplayName {
    public static string Resolve(RawMdnsService service, ImmutableArray<DeviceView> devices) {
        if (!string.IsNullOrWhiteSpace(service.GivenName)
            && !IsProtocolInstanceLabel(service.GivenName)) {
            return service.GivenName.Trim();
        }

        if (!string.IsNullOrEmpty(service.Serial) && !devices.IsDefaultOrEmpty) {
            foreach (DeviceView device in devices) {
                if (device.Detail.Serial is string serial
                    && string.Equals(serial, service.Serial, StringComparison.Ordinal)) {
                    return device.DisplayName;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(service.ProductModel)
            && !IsProtocolInstanceLabel(service.ProductModel)) {
            return service.ProductModel.Trim();
        }

        return Vocabulary.Pairing.DefaultDeviceName;
    }

    /// <summary>True for adb-* / studio-* protocol instance identifiers.</summary>
    public static bool IsProtocolInstanceLabel(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return false;
        }

        return ProtocolInstanceRegex().IsMatch(value.Trim());
    }

    [GeneratedRegex(@"^(adb|studio)-", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex ProtocolInstanceRegex();
}
