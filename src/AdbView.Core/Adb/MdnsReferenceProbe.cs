namespace AdbView.Adb;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

internal enum MdnsProbeStatus {
    /// <summary>Queries were sent and the receive window completed (count may still be zero).</summary>
    Measured,
    /// <summary>The probe could not run or observe the network (no usable interface, socket error).</summary>
    Unavailable,
}

/// <summary>Result of one reference measurement (decision 0008).</summary>
internal sealed record MdnsProbeResult(MdnsProbeStatus Status, int AnnouncementCount) {
    public static MdnsProbeResult Unavailable { get; } = new(MdnsProbeStatus.Unavailable, 0);
}

/// <summary>
/// One-shot mDNS reference probe (research/2026-10-01-discovery-diagnostics-evidence.md).
/// Sends legacy-unicast PTR queries — ephemeral source port, never 5353, per RFC 6762 §6.7/§5.1 —
/// on each candidate interface for the ADB service types and counts distinct advertised instances.
/// Event-driven only: callers invoke it on flyout open or Check again; never scheduled (R-05).
/// </summary>
internal static class MdnsReferenceProbe {
    internal static readonly TimeSpan ReceiveWindow = TimeSpan.FromSeconds(3);
    internal static readonly string[] ServiceDomains = [
        "_adb-tls-connect._tcp.local",
        "_adb-tls-pairing._tcp.local",
    ];

    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<MdnsProbeResult> ProbeAsync(
        IReadOnlyList<IPAddress> interfaceAddresses,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(interfaceAddresses);
        if (interfaceAddresses.Count == 0) {
            return MdnsProbeResult.Unavailable;
        }

        try {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Ephemeral source port: legacy one-shot querier. Responses arrive unicast to this
            // socket (RFC 6762 §6.7) — solicited traffic, no firewall rule required.
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            ushort messageId = (ushort)Environment.TickCount;
            var queries = new List<byte[]>(ServiceDomains.Length);
            foreach (string domain in ServiceDomains) {
                queries.Add(DnsMessageCodec.BuildQuery(messageId, domain));
            }

            foreach (IPAddress address in interfaceAddresses) {
                // Per-interface send: independent of any single multicast route choice — the
                // suspected adb failure mode (research/2026-09-19 note, §D.1).
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    address.GetAddressBytes());

                foreach (byte[] query in queries) {
                    await socket
                        .SendToAsync(query, SocketFlags.None, MulticastEndpoint, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[1500]; // legacy responses are small single datagrams
            long deadlineTicks = Environment.TickCount64 + (long)ReceiveWindow.TotalMilliseconds;
            while (Environment.TickCount64 < deadlineTicks) {
                int remainingMicroseconds = (int)Math.Min(
                    (deadlineTicks - Environment.TickCount64) * 1000,
                    int.MaxValue);
                if (!socket.Poll(remainingMicroseconds, SelectMode.SelectRead)) {
                    break;
                }

                int received = socket.Receive(buffer, SocketFlags.None);
                if (received > 0) {
                    DnsMessageCodec.TryCollectPtrTargets(buffer.AsSpan(0, received), targets);
                }
            }

            return new MdnsProbeResult(MdnsProbeStatus.Measured, targets.Count);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (SocketException ex) {
            Log.Warning(ex, "mDNS reference probe failed: {SocketError}", ex.SocketErrorCode);
            return MdnsProbeResult.Unavailable;
        }
#pragma warning disable CA1031 // R-08: any failure is logged AND becomes probe-outcome state.
        catch (Exception ex) {
            Log.Warning(ex, "mDNS reference probe failed unexpectedly.");
            return MdnsProbeResult.Unavailable;
        }
#pragma warning restore CA1031
    }
}
