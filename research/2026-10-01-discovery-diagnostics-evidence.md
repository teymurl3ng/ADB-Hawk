# Discovery diagnostics: unprivileged evidence for broken mDNS browse

- **Date:** 2026-10-01
- **Author:** agent (Step 1 of the discovery-health plan; see docs/decisions/0008 once drafted)
- **Phase:** post-Phase-11 product hardening (discovery reliability; decision 0008)
- **Status:** current

## Problem

On hosts where the shared ADB server's mDNS browse silently returns empty complete lists
(`research/2026-09-19-host-mdns-empty-while-tcp-works.md`), Adb View shows zero offers and zero
errors because adb reports itself healthy. The product needs read-only, unprivileged evidence to
explain *why* discovery is not working and whether announcements exist on the wire that the ADB
server fails to receive:

1. Which processes currently hold UDP port 5353 (the discovery port)?
2. Which network adapters are virtual (Tailscale, Hyper-V, …) and therefore suspected of
   mis-directing multicast joins?
3. Do ADB service announcements actually exist on this network right now — measurable independently
   of the ADB server's broken browse?

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Microsoft Learn — GetExtendedUdpTable function (iphlpapi.h) | https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable | ms.date 2018-12-05, page updated 2024-02-22; fetched 2026-10-01 |
| Microsoft Learn — GetAdaptersAddresses function (iphlpapi.h) | https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getadaptersaddresses | ms.date 2018-12-05, page updated 2024-02-22; fetched 2026-10-01 |
| IETF RFC 6762 — Multicast DNS | https://www.rfc-editor.org/rfc/rfc6762.txt | fetched 2026-10-01 |
| IETF RFC 6763 — DNS-Based Service Discovery | https://www.rfc-editor.org/rfc/rfc6763.txt | referenced by 6762; fetched 2026-10-01 (browse semantics for Step 3) |
| In-repo evidence | research/2026-09-19-host-mdns-empty-while-tcp-works.md | 2026-09-19, status current |
| In-repo protocol pins | research/2026-09-16-adb-protocol-and-wireless.md | 2026-09-16, refreshed 2026-09-19 |

## Version investigated

| Component | Version |
|---|---|
| OS | Windows 11 `10.0.26100` (the broken-browse host; live test bed) |
| .NET SDK / runtime | 10.0.401 / 10.0.12 (`research/2026-09-16-toolchain-and-gates.md`) |
| CsWin32 | 0.3.333 (`Directory.Packages.props`) |
| platform-tools / adb | 37.0.1-15733141 |
| Device | Samsung A06 · Android 16 / SDK 36 |

## Findings

Facts below are traceable to the sources above; undocumented behaviours are marked *(undocumented —
live-validate)*.

1. **UDP endpoint ownership is readable without elevation claims.** `GetExtendedUdpTable` with
   `TableClass = UDP_TABLE_OWNER_PID` returns `MIB_UDPTABLE_OWNER_PID`, whose rows carry the local
   address, local port, and owning PID (`dwOwningPid`). Supported since Vista; IPv6 via
   `AF_INET6` + `MIB_UDP6TABLE_OWNER_PID`. The docs state **no privilege requirement** — the same
   data `netstat -ano` shows unprivileged — but the docs are silent on the word "privilege", so
   unprivileged operation on 26100 must be confirmed live. Buffer sizing follows the standard
   `ERROR_INSUFFICIENT_BUFFER` retry pattern.
2. **Adapter inventory is readable the same way.** `GetAdaptersAddresses` (15 KB working buffer,
   ≤3 retries on `ERROR_BUFFER_OVERFLOW`, `GAA_FLAG_INCLUDE_ALL_INTERFACES` for all NDIS interfaces)
   returns per-adapter `FriendlyName`, `Description`, `IfType`, `OperStatus`. No privilege
   requirement is stated. *(undocumented — live-validate)*
3. **"Virtual adapter" is a heuristic, not a flag.** `IfType` values (e.g. software loopback 24,
   `IF_TYPE_PROP_VIRTUAL` 53, tunnel 131) plus `Description`/`FriendlyName` string matching
   ("Tailscale", "Hyper-V", "WSL", "Virtual", "TAP") identify the known suspects. The docs do not
   provide a canonical virtual/physical bit. The exact match list must be pinned at scope time and
   kept in one owned place.
4. **A one-shot mDNS query is firewall-friendly by specification.** RFC 6762 §6.7 (Legacy Unicast
   Responses): a query sent from a source port other than 5353 "MUST" be answered by a UDP response
   sent "directly back to the querier, via unicast, to the query packet's source IP address and
   port", repeating the query ID and question; the cache-flush bit "MUST NOT" be set; record TTL
   "SHOULD NOT" exceed 10 seconds. §5.1: one-shot queries "MUST NOT" be sent from UDP source port
   5353 — i.e. the probe binds an **ephemeral** port. Inbound unicast responses to a socket we
   sent from are solicited traffic under Windows Firewall's stateful inspection *(undocumented for
   this exact case — live-validate; no firewall rule should be needed)*.
5. **Queries go to `224.0.0.251:5353` (IPv4).** The QU bit (§5.4) is optional for our use; a plain
   QM one-shot query from an ephemeral port already forces unicast legacy responses per §6.7.
   DNS-SD service browsing queries the PTR record of `_adb-tls-connect._tcp.local` /
   `_adb-tls-pairing._tcp.local` (RFC 6763); responses carry PTR answers (instance names) and
   typically SRV/TXT/A in the additional section. For evidence purposes, the distinct PTR instance
   names per service type are sufficient.
6. **Interface selection matters — it is the suspected root cause.** The Phase 6D evidence
   (`adb.log` `SetMulticastOutboundInterface` on Hyper-V-class adapters) suggests the ADB server
   sends its multicast query on the wrong interface. Our probe therefore sends one query per
   candidate interface (`IP_MULTICAST_IF` bound per send) across physical/operational adapters plus
   the default route interface, so the probe is independent of any single interface choice.
7. **Process names come from PIDs.** Rows give PIDs; names resolve via
   `System.Diagnostics.Process.GetProcessById` with a guarded catch for exited PIDs (R-08: a
   vanished PID is counted, not swallowed).

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| `GetOwnerModuleFromUdpEntry` (UDP_TABLE_OWNER_MODULE) | Module path without PID lookup | More marshalling for no gain — PID + `Process.GetProcessById` suffices; module info absent for some system rows |
| Shelling out to `netsh`/PowerShell for diagnostics | Zero native code | Child-process spawn + fragile text parsing per probe; against the repo's in-proc socket culture; slow |
| Bind UDP 5353 like a full browser | Receives multicast advertisements | Exactly the shared-port contention failure mode under diagnosis; forbidden for one-shot by RFC 6762 §5.1 |
| Zeroconf / dnssd.dll browse as the probe | Battle-tested browsing | New dependency / undocumented API for an evidence check; deferred to Step 3 where browsing is the product |
| No probe; explain from adb data alone | Smallest change | Cannot distinguish "nothing is advertising" from "ADB does not receive" — the core question |

## Chosen approach

1. `src/AdbView/Host/DiscoveryDiagnostics.cs` (Windows concern → `Host/`): CsWin32 entries
   appended to `NativeMethods.txt` — `GetExtendedUdpTable`, `GetAdaptersAddresses` (+ supporting
   structs). Returns, per snapshot demand: 5353 binder list (PID → process name, AF_INET + AF_INET6),
   adapter list with a pinned virtual-adapter heuristic.
2. One-shot reference probe in `src/AdbView/Adb/` (ADB discovery concern): raw UDP socket on an
   ephemeral port, one QM query per candidate interface per service type
   (`_adb-tls-connect._tcp.local`, `_adb-tls-pairing._tcp.local`), 3 s receive window, minimal DNS
   message parse (header/question/PTR), returns distinct instance names per service type. Strictly
   event- or user-triggered (card shown / "Check again") — never a timer (R-05).
3. Evidence assembly + broken-discovery trigger live in `Adb/`+`Host/` wiring and surface through
   the existing snapshot pipeline; wording owned by `State/Vocabulary.cs` (R-07/R-15).

## Why

No new dependency; fully unprivileged; the legacy-unicast probe avoids the exact shared-port
failure mode under investigation and needs no firewall rule by specification; the DNS message
parser written for the probe is the seed for the Step 3 fallback browser; every piece is
hermetically testable behind interface seams, with live validation on the one machine known to
exhibit the failure.

## Rejected approaches

- **Killing or restarting anything from the diagnostics path** — Step 2 owns actions behind
  approval buttons; diagnostics stay read-only.
- **Polling the probe on a timer** — R-05; evidence refreshes on events (flyout open / snapshot
  publish / explicit Check again).
- **Elevation for diagnostics** — nothing above needs admin; UAC is reserved for Step 2 repairs.
- **Treating `IfType` alone as "virtual"** — no canonical flag exists; heuristic list it is.

## Compatibility concerns

- PIDs can exit between enumeration and name lookup — guarded, counted, never swallowed (R-08).
- Adapters without multicast capability or down (`OperStatus != Up`) are excluded from probe sends.
- Public network profile / AP-isolated networks: probe will return empty — the card must present
  "no announcements on the network" as a distinct finding from "ADB does not receive what the probe
  sees" (both are evidence, different causes).
- ARM64: CsWin32 emits RID-specific code; the csproj already pins RID-specific `PlatformTarget`.
- The 10 s response TTL (§6.7) means probe results are momentary evidence, not a cache.

## Testing implications

- Unit: diagnostics collectors behind seams (fake UDP tables / adapter lists, including exited-PID
  and empty-table rows); DNS message parser against captured packets; trigger truth table.
- Captured fixture: a real legacy-unicast response from the Samsung A06 during live validation.
- Live matrix (docs/DEVICE-TEST-MATRIX.md rows to add): 5353 binder list matches `netstat -ano`;
  adapter flags match `Get-NetAdapter`; **the acceptance — probe sees services while
  `adb mdns services` is empty on this host**; probe returns empty on a network with no ADB device.

## Open items

- **CsWin32 deviation (resolved 2026-10-01, implementation):** requesting `GetExtendedUdpTable`
  through the CsWin32 0.3.333 generator OOMs its dependency-closure pass on this memory-constrained
  host — verified by A/B build (baseline `NativeMethods.txt` generates; with the API added the
  generator dies with `OutOfMemoryException`, reproducibly, after build-server shutdown). The
  implementation uses a single hand-written `DllImport` (`Iphlpapi.dll`, `DefaultDllImportSearchPaths
  = System32`) in `Host/DiscoveryDiagnostics.cs` instead; adapter enumeration uses BCL
  `NetworkInterface` (no native call needed). No new package; deviation recorded here and at the
  P/Invoke site.
- Firewall behaviour of legacy-unicast responses *(undocumented — live-validate on 26100)*.
- Virtual-adapter heuristic list pinned in `DiscoveryDiagnostics.VirtualAdapterHints`
  (Tailscale, Hyper-V/vEthernet, WSL, Docker, VMware, VirtualBox, Virtual, TAP-) — single owned
  location; extend only with field evidence.
- Links to Phase 6D open items V-1 (operator experiment) — the probe supersedes the manual
  experiment with an in-product measurement.
- Machine memory pressure (≈4–5 GB free) caused: the CsWin32 OOM above, Vitest worker SIGABRTs
  under default parallelism (suite green with `--maxWorkers 1`), and intermittent flakiness in
  threading tests (`BalloonNotificationSinkTests`, `Chaos Row26`) under load. Environmental; none
  are product defects.
