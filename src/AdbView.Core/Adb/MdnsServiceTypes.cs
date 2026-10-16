namespace AdbView.Adb;

using AdbView.Adb.Model;

/// <summary>
/// Verified ADB mDNS service type constants.
/// AOSP pin: platform/packages/modules/adb @ 1cf2f017d312f73b3dc53bda85ef2610e35a80e9
/// (<c>ADB_FULL_MDNS_SERVICE_TYPE</c> in <c>adb_mdns.cpp</c>).
/// </summary>
internal static class MdnsServiceTypes {
    public const string Legacy = "_adb._tcp";
    public const string Pairing = "_adb-tls-pairing._tcp";
    public const string Connect = "_adb-tls-connect._tcp";

    public static MdnsServiceKind Classify(string? serviceType) {
        if (string.IsNullOrEmpty(serviceType)) {
            return MdnsServiceKind.Unknown;
        }

        if (string.Equals(serviceType, Legacy, System.StringComparison.Ordinal)) {
            return MdnsServiceKind.Legacy;
        }

        if (string.Equals(serviceType, Pairing, System.StringComparison.Ordinal)) {
            return MdnsServiceKind.Pairing;
        }

        if (string.Equals(serviceType, Connect, System.StringComparison.Ordinal)) {
            return MdnsServiceKind.Connect;
        }

        return MdnsServiceKind.Unknown;
    }
}
