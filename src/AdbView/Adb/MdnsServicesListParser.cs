namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AdbView.Adb.Model;
using AdbView.Adb.Protocol;

/// <summary>
/// Parses one-shot <c>host:mdns:services</c> / <c>adb mdns services</c> text listings.
/// Format (observed 37.0.1): <c>instance\tservice_type\thost:port\n</c>.
/// IPv6 hosts use bracket form when present; do not split on the first colon.
/// </summary>
internal static class MdnsServicesListParser {
    public static ImmutableArray<RawMdnsService> Parse(ReadOnlySpan<byte> payload) =>
        Parse(Encoding.UTF8.GetString(payload));

    public static ImmutableArray<RawMdnsService> Parse(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return ImmutableArray<RawMdnsService>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<RawMdnsService>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
            string line = rawLine.Trim();
            if (line.Length == 0) {
                continue;
            }

            // Skip the human header line from CLI: "List of discovered mdns services"
            if (line.StartsWith("List of discovered", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 3) {
                // Soft-skip malformed lines inside an otherwise recoverable listing.
                continue;
            }

            string instance = parts[0].Trim();
            string serviceType = parts[1].Trim();
            string endpoint = parts[2].Trim();

            if (!TrySplitHostPort(endpoint, out string? host, out int port)) {
                continue;
            }

            string? ipv4 = null;
            var ipv6 = ImmutableArray<string>.Empty;
            if (IPAddress.TryParse(host, out IPAddress? address)) {
                if (address.AddressFamily == AddressFamily.InterNetwork) {
                    ipv4 = host;
                }
                else if (address.AddressFamily == AddressFamily.InterNetworkV6) {
                    ipv6 = ImmutableArray.Create(host);
                }
            }
            else {
                ipv4 = host;
            }

            var service = new RawMdnsService {
                InstanceName = instance,
                ServiceType = serviceType,
                Kind = MdnsServiceTypes.Classify(serviceType),
                Ipv4 = ipv4,
                Ipv6 = ipv6,
                Port = port,
                Format = RawMdnsFormat.ServicesList,
            };

            if (string.IsNullOrEmpty(service.InstanceName) && string.IsNullOrEmpty(service.ServiceType)) {
                continue;
            }

            if (!seen.Add(service.DiscoveryKey)) {
                continue;
            }

            builder.Add(service);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Splits <c>host:port</c> or <c>[ipv6]:port</c> without treating IPv6 colons as the port separator.
    /// </summary>
    internal static bool TrySplitHostPort(string endpoint, out string? host, out int port) {
        host = null;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint)) {
            return false;
        }

        endpoint = endpoint.Trim();

        if (endpoint.StartsWith('[')) {
            int close = endpoint.IndexOf(']', StringComparison.Ordinal);
            if (close <= 1 || close + 2 >= endpoint.Length || endpoint[close + 1] != ':') {
                return false;
            }

            host = endpoint[1..close];
            return int.TryParse(endpoint[(close + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out port);
        }

        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1) {
            return false;
        }

        // Reject naive first-colon splits for bare IPv6 (more than one colon, no brackets).
        if (endpoint.AsSpan().Count(':') > 1) {
            return false;
        }

        host = endpoint[..colon];
        return int.TryParse(endpoint[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port);
    }
}
