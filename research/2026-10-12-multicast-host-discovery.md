# Bounded QR discovery and Windows multicast evidence

- **Date:** 2026-10-12
- **Author:** Codex
- **Phase:** 12.3 / 12.5 discovery hardening, explicitly requested by owner
- **Status:** current

## Problem

The fallback queries once at QR start, before a delayed scan can open the phone's
advertisement window. Its active socket uses default multicast egress; passive
memberships are acquired only at startup. These are verified code observations,
not proof of why this host receives no services. Existing reports of shared UDP
5353 and virtual adapters establish correlation, not causation. Live acceptance
and Android legacy-unicast response behavior remain unverified.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Multicast DNS, querying, legacy unicast, multiple interfaces/responders | https://www.rfc-editor.org/rfc/rfc6762 | RFC 6762, February 2013; consulted 2026-10-12 |
| Windows IPPROTO_IP socket options | https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options | Updated 2026-05-07; consulted 2026-10-12 |
| Socket.ReceiveMessageFrom | https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.receivemessagefrom?view=net-10.0 | .NET 10 API; consulted 2026-10-12 |
| NetworkChange.NetworkAddressChanged | https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange.networkaddresschanged?view=net-10.0 | .NET 10 API; consulted 2026-10-12 |
| Using SO_REUSEADDR and SO_EXCLUSIVEADDRUSE | https://learn.microsoft.com/en-us/windows/win32/winsock/using-so-reuseaddr-and-so-exclusiveaddruse | Windows Winsock reference; consulted 2026-10-12 |

## Version investigated

Repository baseline 82f3df8; pinned .NET SDK 10.0.401 / net10.0-windows;
current execution host reports Windows 10.0.26100.0 and PowerShell 7.6.6.
Prior affected-host evidence records Windows 11 build 26100, adb 37.x and
Samsung Galaxy A06 / Android 16. The harness records current OS, PowerShell,
ADB server response and adapters on each run; exact current adb/device versions
and host-mode results are NOT EXERCISED until the owner runs it.

## Findings

- RFC 6762 sections 5.1 and 6.7 specify responses to ephemeral-port queries;
  that specification does not prove the owner's phone answers them.
- IP_MULTICAST_IF selects outgoing IPv4 multicast traffic using a four-byte
  IPv4 address in network byte order. It does not select receiving interfaces.
- ReceiveMessageFrom returns IPPacketInformation; PacketInformation must be
  enabled before bind so packets queued before the first receive have metadata.
- NetworkAddressChanged supplies an event for address changes. Resume is
  already exposed by the application's PowerAndSession lifecycle.
- RFC 6762 section 15 and Windows reuse rules require care with shared sockets;
  a successful shared bind/join does not prove all multicast arrives. A/B/C
  measurement is needed before changing query source port or response class.
- Passive events cannot cover announcements lost to this host. A short retry
  window uses the already sanctioned TTL-maintenance loop, not a new timer.
  Two-second repetition is a bounded owner-requested reliability tradeoff,
  not a claim that RFC 6762 mandates this cadence.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| One burst at QR start | Low traffic | Misses delayed scans on an active-only host |
| Bounded 2 s QR window in existing loop | Covers delayed scan; injected-clock tests | Extra local queries for at most 2 min |
| Lower the background floor | Simple | Continuous traffic unrelated to user intent |
| Explicit egress on each eligible IPv4 address | Avoids one route choice | Does not fix firewall or phone response policy |
| Shared-port QU queries / known-host unicast | May help if only passive works | Evidence required; conditional Task 4 |

## Chosen approach

First add a standalone, unprivileged A/B/C harness (15 s per mode) with full-message
DNS compression parsing, interface metadata and a markdown report. Read
host:mdns:services over an existing localhost ADB socket, labelled as the
equivalent of adb mdns services; do not invoke a CLI that can auto-start a server.
Then add a maximum 2 min fast-query window to the existing 200 ms loop, ending
on QR termination/handoff or server-generation change. Re-read the eligible
Up, multicast-capable, non-loopback IPv4 addresses per burst, sharing the
diagnostics provider used by the reference probe. On address-change/resume,
set a pending flag and debounce 1 s in that same loop, replacing the passive
socket to discard obsolete memberships and acquire current ones.
Receive drains are nonblocking: the existing delay supplies the 200 ms slice;
the prior two blocking 200 ms receive windows otherwise extended that slice.
Failed bind setup disposes its newly allocated socket before propagating failure.

## Why

This keeps the ADB stream authoritative per instance, preserves the 60 s floor
outside explicit QR attempts, and measures the unknown transport behavior
without changing the server, firewall, adapters, UI, or phone.

## Rejected approaches

No Android app/listener, ADB restart/environment mutation, new timer, manual IP
entry, package, or UI wording. Shared-port queries and direct unicast are deferred
until owner results show A/B empty and C receiving the relevant service.

## Compatibility concerns

IPv4 only, consistent with the browser's endpoint model. Virtual interfaces
are not excluded by name: supported multicast capability is the filter.
Failed joins/sends are logged; an empty egress list retains default-route sends.
Network events are unsubscribed at disposal. A failed passive rebind leaves the
active path alive. The harness reports unavailable collectors/modes as errors,
never as successful zero replies, and uses no elevation.

## Testing implications

Offline harness query/parser/loopback tests; frozen-clock QR cadence, timeout,
stop and unchanged-floor tests; fake-socket egress tests; event/debounce/rejoin
and active-only failure tests. Full verify gate required. All real-phone rows
remain NOT EXERCISED; no inferred live pass from mocks.

## Open items

Owner A/B/C results, legacy-unicast behavior, actual egress cause, resume/roam
acceptance, and successful pairing on the affected host remain unresolved.
