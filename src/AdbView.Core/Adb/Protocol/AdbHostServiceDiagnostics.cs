namespace AdbView.Adb.Protocol;

using System;

/// <summary>
/// Classifies ADB server FAIL diagnostics for host services.
/// AOSP (<c>sockets.cpp</c> @ 1cf2f017…): <c>unknown host service '&lt;service&gt;'</c>.
/// </summary>
internal static class AdbHostServiceDiagnostics {
    private const string UnknownHostServicePrefix = "unknown host service";

    /// <summary>
    /// True when the FAIL reason is the server's explicit unknown-host-service diagnostic.
    /// Generic CommandFailed reasons must not match.
    /// </summary>
    public static bool IsUnknownHostService(string? serverReason) {
        if (string.IsNullOrEmpty(serverReason)) {
            return false;
        }

        // Exact AOSP prefix; remainder is typically " 'host:…'".
        return serverReason.StartsWith(UnknownHostServicePrefix, StringComparison.Ordinal);
    }
}
