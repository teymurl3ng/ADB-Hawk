namespace AdbView.Adb;

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AdbView.Adb.Model;

/// <summary>
/// Structured pairing endpoint from a verified <c>_adb-tls-pairing._tcp</c> discovery record.
/// Wire form for <c>host:pair</c>: IPv4 <c>host:port</c>, IPv6 <c>[host]:port</c>
/// (AOSP <c>services.cpp</c> splits password at the first colon after <c>pair:</c>).
/// </summary>
internal sealed record PairingEndpoint {
    public required string Host { get; init; }
    public required int Port { get; init; }

    /// <summary>ADB host:pair / CLI endpoint token (never includes the pairing code).</summary>
    public string WireEndpoint {
        get {
            if (IPAddress.TryParse(Host, out IPAddress? address)
                && address.AddressFamily == AddressFamily.InterNetworkV6) {
                return string.Create(CultureInfo.InvariantCulture, $"[{Host}]:{Port}");
            }

            return string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");
        }
    }

    public static bool TryCreate(string host, int port, out PairingEndpoint? endpoint) {
        endpoint = null;
        if (string.IsNullOrEmpty(host)) {
            return false;
        }

        if (port is < 1 or > 65535) {
            return false;
        }

        // Reject whitespace / empty after trim-equivalent checks without trimming input.
        foreach (char c in host) {
            if (char.IsWhiteSpace(c)) {
                return false;
            }
        }

        endpoint = new PairingEndpoint { Host = host, Port = port };
        return true;
    }

    /// <summary>
    /// Builds an endpoint only from a Pairing-kind mDNS service (never Connect / Legacy).
    /// Prefers IPv4 when present; otherwise the first IPv6 address.
    /// </summary>
    public static bool TryFromPairingService(RawMdnsService service, out PairingEndpoint? endpoint) {
        endpoint = null;
        if (service.Kind != MdnsServiceKind.Pairing
            || !string.Equals(service.ServiceType, MdnsServiceTypes.Pairing, StringComparison.Ordinal)) {
            return false;
        }

        string? host = null;
        if (!string.IsNullOrEmpty(service.Ipv4)) {
            host = service.Ipv4;
        }
        else if (service.Ipv6.Length > 0 && !string.IsNullOrEmpty(service.Ipv6[0])) {
            host = service.Ipv6[0];
        }

        if (host is null) {
            return false;
        }

        return TryCreate(host, service.Port, out endpoint);
    }
}
