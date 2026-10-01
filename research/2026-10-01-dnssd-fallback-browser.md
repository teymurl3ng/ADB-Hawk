# First-party DNS-SD fallback browser

- **Date:** 2026-10-01
- **Author:** agent (Phase 12 Step 3)
- **Phase:** 12.3 (discovery health)
- **Status:** current

## Problem

On the affected host the shared ADB server's mDNS browse silently returns empty
(`research/2026-09-19-host-mdns-empty-while-tcp-works.md`) while the network itself delivers ADB
announcements (Phase 6A proved the LAN does; the Step 1 probe measures them). The robustness fix
is a first-party DNS-SD browser in Adb View that listens to the same network announcements
directly and fills the discovery gap when the ADB server's browse is broken — without touching
the server (R-14) and without reconfiguring anything.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| IETF RFC 6763 — DNS-Based Service Discovery | https://www.rfc-editor.org/rfc/rfc6763.txt | fetched 2026-10-01 |
| IETF RFC 6762 — Multicast DNS (legacy unicast, TTL, goodbyes) | https://www.rfc-editor.org/rfc/rfc6762.txt | fetched 2026-10-01 |
| AOSP adb daemon `mdns.cpp` (advertisement side) | https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/daemon/mdns.cpp | main as of fetch 2026-10-01 |
| Microsoft Learn — DnsServiceBrowse (windns.h) | https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsservicebrowse | fetched via search 2026-10-01 |
| Microsoft Learn — Windows.Networking.ServiceDiscovery.Dnssd (WinRT) | https://learn.microsoft.com/en-us/uwp/api/windows.networking.servicediscovery.dnssd | fetched via search 2026-10-01 |
| In-repo pins | research/2026-09-16-adb-protocol-and-wireless.md (MdnsServices wire schema; service-type constants @ 1cf2f017) | 2026-09-16/19, current |
| In-repo evidence | research/2026-09-19-host-mdns-empty-while-tcp-works.md; research/2026-10-01-discovery-diagnostics-evidence.md | current |

## Version investigated

Windows 11 `10.0.26100` · platform-tools 37.0.1 · Samsung A06 Android 16 · .NET 10 · CsWin32 0.3.333.

## Findings

1. **Browse mechanics (RFC 6763 §4).** A client queries PTR for `<Service>.local`
   (`_adb-tls-connect._tcp.local`, `_adb-tls-pairing._tcp.local`, `_adb._tcp.local`); responders
   return PTR answers naming `<Instance>.<Service>.local` and SHOULD include SRV/TXT/A in the
   additional section; a client must also resolve SRV/TXT itself when omitted. The Step 1 codec
   (`DnsMessageCodec`) already handles PTR; SRV/TXT/A parsing is an extension, not new ground.
2. **ADB advertisement shape (AOSP `daemon/mdns.cpp`, main 2026-10-01).** `_adb._tcp` instance =
   `adb-<serial>`; `_adb-tls-connect._tcp` instance = `adb-<guid>` where guid is
   `persist.adb.wifi.guid` (`adb-<serial>-<6 random>` format documented in the GUID comment).
   TXT keys for both: `v` (secure service version), `name` (`ro.product.model`), `api`
   (`ro.build.version.sdk`). The pairing service (`_adb-tls-pairing._tcp`) is registered by the
   pairing server while a pairing window is open (per the pinned 2026-09-16 research). Mapping:
   `name` → product/display model evidence, `api` → SDK. The repo's MdnsServices proto fields
   (product_model, build_version_sdk_full, given_name, serial, mdns_service_version, hostname)
   are the ADB SERVER's parsed view of these same wire records — a first-party browser can fill
   the fields the TXT actually carries (`name`, `api`) and derive the rest via existing identity
   logic.
3. **Continuous browsing needs soft state.** RFC 6762: records carry TTLs; withdrawal is
   event-driven via TTL-0 "goodbye" announcements, but packets can be missed, so a browser MUST
   treat its list as soft state with TTL-based eviction (requery around 80 % TTL per §4.1/§6).
   **No pure event mechanism exists for staleness-proof browsing** — this is the R-05 case: the
   research note documents it, and an `ENGINEERING.md §11/R-05` sanction for a single low-rate
   TTL/requery timer (only while the ADB server is Up) is required and recorded in decision 0008
   follow-through.
4. **Port strategy.** RFC 6762 §5.1/§6.7: compliant queriers send from port 5353; legacy
   one-shot queriers (ephemeral port) MUST receive unicast legacy responses. Passive listening
   for multicast announcements requires being joined to 224.0.0.251 **on port 5353** (multicast
   delivery = group membership + destination port). Phase 6D evidence shows multiple processes
   (adb, msedge) sharing 0.0.0.0:5353 on this host — Windows SO_REUSEADDR permits shared
   multicast binds, and joined sockets each receive the group's datagrams. If the shared bind
   fails, active-only browsing (ephemeral-port queries, unicast answers) remains fully
   functional by specification.
5. **Windows ships two documented DNS-SD surfaces:** `DnsServiceBrowse` (windns.h / dnsapi.dll,
   callback-based) and WinRT `Windows.Networking.ServiceDiscovery.Dnssd`. Both are official
   (no Bonjour dependency). No `dnssd.dll` ships with Windows — that name is Apple Bonjour's.
   Trade-off vs raw sockets: the OS API hides packet/TTL handling but introduces callback
   marshalling, CsWin32 generation for a new header family (the generator OOM'd on
   `GetExtendedUdpTable` under this machine's memory pressure — A/B-verified in
   research/2026-10-01-discovery-diagnostics-evidence.md), and hides exactly the behavior
   (which interface, which port, what TTL) we are debugging.
6. **Firewall.** A shared bind on UDP 5353 is an inbound-listen surface: Windows Firewall shows
   its first-listen prompt for our per-user app (installer cannot pre-authorize; `PrivilegesRequired=lowest`).
   The ephemeral-port active path needs no rule (solicited unicast, per Step 1 findings).
   The 5353 passive path therefore is best-effort: try shared bind, tolerate failure (active-only
   mode), and document the prompt.
7. **Merge semantics.** The ADB server's stream is authoritative when it works. The browser's
   list fills only the gap: when the tracker reports zero services, browser-discovered
   instances feed the same `RawMdnsService` pipeline (offers, identity enrichment) so the UI is
   identical whichever source won. `known_device` / pairing-kind classification maps from the
   service type exactly as `MdnsServiceTypes.Classify` does today.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Raw UDP socket browser in `Adb/` | No dependency; matches socket culture + existing codec; full visibility (we are debugging mDNS behavior); testable with captured packets | Multicast/port/TTL handling is ours; shared-5353 bind realities |
| `DnsServiceBrowse` (windns) | OS-documented; OS handles TTL/announce details | CsWin32 OOM risk on this machine; callback/P-Invoke marshalling; opaque behavior; windns API is synchronous-callback hybrid awkward from async code |
| WinRT Dnssd (DnssdServiceWatcher) | High-level | First WinRT usage in a codebase whose research deliberately chose Win32/BCL (`2026-09-16-windows-host-integration.md`); same opacity |
| NuGet Zeroconf | Battle-tested | R-09 paperwork + dependency for something the codec already 60 % covers; avoid-list culture |

## Chosen approach

1. **Raw-socket browser in `src/AdbView/Adb/`** (`MdnsFallbackBrowser` + `DnsMessageCodec`
   extensions for SRV/TXT/A): dual-path — passive shared-bind listener on 5353 (join
   224.0.0.251 per operational interface; degrade to active-only on bind failure) + active
   ephemeral-port legacy queries (two-shot burst per RFC 6762 on session start and on
   TTL-requery). Session runs while the ADB server is Up; stops on non-Up phases (same
   generation keying as the trackers). No foreground timers besides the single sanctioned
   TTL-requery/expiry tick (ENGINEERING.md §11 edit + this note per R-05).
2. **State merge:** new `MdnsFallbackReceivedEvent(services, at)` → `DeviceEngineState` keeps
   browser rows separately; the reducer computes the effective service set as
   `adb services if non-empty else fallback rows` (adb authoritative; fallback fills only the
   gap). Offer mapping, identity enrichment, and the Step 1 trigger consume the effective set;
   the Step 1 trigger keeps using the ADB-source list (the card explains the ADB gap even while
   the fallback bridges it).
3. **TTL policy:** instance entries expire at TTL (capped 75 min floor 5 s); goodbyes (TTL 0)
   evict immediately; requery burst at ~80 % TTL for still-wanted types.
4. **Live acceptance (this host):** `adb mdns services` empty while the browser reports
   `_adb-tls-connect._tcp`/`_adb._tcp` rows and the flyout shows the device normally.

## Why

No new dependency and no new Windows API projection; reuses the Step 1 codec and probe patterns
(ephemeral legacy queries are firewall-safe and already proven on this host); the dual-path
design degrades gracefully under the exact port-contention conditions being worked around; the
merge keeps the ADB server authoritative and the change invisible when browse works.

## Rejected approaches

- `DnsServiceBrowse`/WinRT Dnssd — opaque to the failure being debugged; generator OOM risk;
  WinRT layer breaks the hosting precedent (research note §Compatibility).
- NuGet Zeroconf — dependency for in-house code the codec largely covers.
- Always-union of adb + browser rows — would silently mask a half-working ADB browse that the
  product's own mission is to observe truthfully.
- Replacing the tracker — the fallback is additive; `MdnsTracker` and its frozen fallback chain
  (ENGINEERING §6/§7) are unchanged.

## Compatibility concerns

- Windows Firewall first-listen prompt for the passive 5353 path (documented; decline = active-only).
- AP-isolated / Public-profile networks: browser finds nothing — same truth as today.
- Devices whose responders ignore legacy (ephemeral) queries: missed by active path when the
  passive bind fails — acceptable degradation, still better than today's silent empty.
- Sleep/resume: session restarts on server-generation and resume paths like the trackers.
- ARM64: pure BCL sockets; no RID-specific code.

## Testing implications

- `DnsMessageCodec` SRV/TXT/A parse fixtures (including compression pointers across records).
- Browser session tests with a scripted fake responder socket: PTR burst → unicast response with
  additional records; goodbye eviction; TTL expiry; bind-failure → active-only degradation;
  generation stop/start.
- Reducer merge truth table: adb non-empty wins; adb empty + fallback rows → offers appear;
  both empty → nothing; fallback rows must NOT satisfy the broken-discovery trigger (it audits
  the ADB source).
- Live matrix rows: acceptance on the affected host; firewall prompt noted; battery of the
  Step 1 acceptance unchanged.

## Open items

- ENGINEERING.md §11 row for the TTL/requery tick (R-05 paperwork) — drafted with decision note.
- Whether `_adb._tcp` (legacy) should also fill the effective set or remain enrichment-only
  (today the tracker classifies it enrichment-only) — default: keep enrichment-only.
- Exact requery cadence constant (RFC 6762 guidance: not more than once an hour for well-known
  types; we use ~80 % of the smallest observed TTL, floor 60 s).
