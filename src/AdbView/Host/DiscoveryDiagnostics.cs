namespace AdbView.Host;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Serilog;

/// <summary>
/// Read-only discovery diagnostics (decision 0008). Everything here is unprivileged,
/// event/user-triggered, and strictly observational — repair actions are out of scope
/// for this layer (research/2026-10-01-discovery-diagnostics-evidence.md).
/// </summary>
internal sealed record NetworkAdapterProbeInfo(string Name, string Description, NetworkInterfaceType Type);

/// <summary>Test seam over the native network tables.</summary>
internal interface INetworkDiagnosticsSource {
    /// <summary>Distinct owning PIDs of UDP sockets bound to the discovery port (IPv4 + IPv6).</summary>
    IReadOnlyList<int> GetDiscoveryPortOwnerPids();

    /// <summary>Operational (Up) network adapters, loopback excluded.</summary>
    IReadOnlyList<NetworkAdapterProbeInfo> GetOperationalAdapters();

    /// <summary>IPv4 addresses of Up, non-loopback adapters — multicast query candidates.</summary>
    IReadOnlyList<IPAddress> GetMulticastCandidateAddresses();
}

internal sealed class WindowsNetworkDiagnosticsSource : INetworkDiagnosticsSource {
    internal const int DiscoveryPort = 5353;
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;
    private const uint UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;
    // MIB_UDPROW_OWNER_PID: localAddr(4) localPort(4) pid(4); MIB_UDP6ROW_OWNER_PID:
    // localAddr(16) localScopeId(4) localPort(4) pid(4). Ports are in network byte order.
    private const int V4RowSize = 12;
    private const int V6RowSize = 28;

    /// <summary>
    /// Hand-written P/Invoke (deliberate CsWin32 deviation): requesting this API through the
    /// CsWin32 generator OOMs its dependency-closure pass on memory-constrained hosts —
    /// verified 2026-10-01 (baseline generates; with this API the generator dies). The
    /// signature matches iphlpapi.h GetExtendedUdpTable with UDP_TABLE_OWNER_PID rows.
    /// </summary>
    [DllImport("Iphlpapi.dll", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedUdpTable(
        byte[]? pUdpTable,
        ref uint pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        uint ulAf,
        uint tableClass,
        uint reserved);

    public IReadOnlyList<int> GetDiscoveryPortOwnerPids() {
        var pids = new List<int>();
        CollectOwners(AfInet, V4RowSize, portOffset: 4, pidOffset: 8, pids);
        CollectOwners(AfInet6, V6RowSize, portOffset: 20, pidOffset: 24, pids);
        return pids;
    }

    private static void CollectOwners(uint family, int rowSize, int portOffset, int pidOffset, List<int> into) {
        uint size = 0;
        uint sizing = GetExtendedUdpTable(null, ref size, false, family, UdpTableOwnerPid, 0);
        if (sizing != ErrorInsufficientBuffer) {
            throw new InvalidOperationException(
                $"GetExtendedUdpTable sizing call returned {sizing}, expected ERROR_INSUFFICIENT_BUFFER ({ErrorInsufficientBuffer}).");
        }

        var buffer = new byte[size];
        uint result = GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0);
        if (result != 0) {
            throw new InvalidOperationException($"GetExtendedUdpTable failed with win32 error {result}.");
        }

        int entries = BitConverter.ToInt32(buffer, 0);
        for (int i = 0; i < entries; i++) {
            int row = 4 + (i * rowSize);
            if (row + rowSize > buffer.Length) {
                Log.Warning(
                    "Discovery diagnostics: UDP table truncated (entries={Entries}, row={Row}, buffer={Buffer}).",
                    entries,
                    i,
                    buffer.Length);
                return;
            }

            int localPort = ((buffer[row + portOffset] & 0xFF) << 8) | (buffer[row + portOffset + 1] & 0xFF);
            if (localPort != DiscoveryPort) {
                continue;
            }

            int pid = BitConverter.ToInt32(buffer, row + pidOffset);
            if (pid != 0) {
                into.Add(pid);
            }
        }
    }

    public IReadOnlyList<NetworkAdapterProbeInfo> GetOperationalAdapters() {
        var adapters = new List<NetworkAdapterProbeInfo>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) {
                continue;
            }

            adapters.Add(new NetworkAdapterProbeInfo(nic.Name, nic.Description, nic.NetworkInterfaceType));
        }

        return adapters;
    }

    public IReadOnlyList<IPAddress> GetMulticastCandidateAddresses() {
        var addresses = new List<IPAddress>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) {
                continue;
            }

            foreach (IPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses) {
                if (unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) {
                    addresses.Add(unicast.Address);
                }
            }
        }

        return addresses;
    }
}

/// <summary>Renderable evidence extracted from raw source data (pure logic; unit-tested).</summary>
internal sealed record DiscoveryEnvironmentEvidence(
    IReadOnlyList<string> PortHolderNames,
    IReadOnlyList<string> VirtualAdapterNames);

internal static class DiscoveryDiagnostics {
    /// <summary>
    /// Adapter Name/Description substrings treated as virtual network adapters. Heuristic by
    /// nature — Windows exposes no canonical virtual/physical flag (research note §Findings 3).
    /// </summary>
    internal static readonly string[] VirtualAdapterHints = [
        "tailscale",
        "hyper-v",
        "vethernet",
        "wsl",
        "docker",
        "vmware",
        "virtualbox",
        "virtual",
        "tap-",
    ];

    /// <summary>
    /// The ADB server's own process name — it legitimately binds the discovery port and must
    /// never be reported as contention.
    /// </summary>
    internal const string AdbProcessName = "adb";

    public static DiscoveryEnvironmentEvidence Collect(
        INetworkDiagnosticsSource source,
        int ownProcessId,
        Func<int, string?> resolveProcessName) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolveProcessName);

        var holders = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int pid in source.GetDiscoveryPortOwnerPids()) {
            if (pid == ownProcessId) {
                continue;
            }

            string? name = resolveProcessName(pid);
            if (name is null) {
                // Process exited between table enumeration and name lookup; the resolver logs
                // the gap (R-08). Nothing renderable remains for this PID.
                continue;
            }

            if (name.Equals(AdbProcessName, StringComparison.OrdinalIgnoreCase) || !seenNames.Add(name)) {
                continue;
            }

            holders.Add(name);
        }

        var virtualNames = new List<string>();
        foreach (NetworkAdapterProbeInfo adapter in source.GetOperationalAdapters()) {
            if (MatchesVirtualAdapter(adapter)) {
                virtualNames.Add(adapter.Name);
            }
        }

        return new DiscoveryEnvironmentEvidence(holders, virtualNames);
    }

    internal static bool MatchesVirtualAdapter(NetworkAdapterProbeInfo adapter) {
        foreach (string hint in VirtualAdapterHints) {
            if (adapter.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)
                || adapter.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }
}
