# ADB access strategy: host protocol, device tracking, wireless discovery, pairing

- **Date:** 2026-09-16
- **Phase:** pre-Phase 0 (architecture); refreshed in Phases 4, 5, 6
- **Status:** current

## Problem

Decide, from first principles against the September 2026 Android tooling, how ADB View observes
ADB state: what to depend on, how to avoid polling, how to model wireless pairing versus wireless
connection, and how to get a trustworthy transport signal without parsing serial strings.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Android Debug Bridge (adb) — official guide | https://developer.android.com/tools/adb | last updated 2026-09-02 |
| AOSP adb host services documentation (`docs/dev/services.md`, `SERVICES.TXT`) | https://android.googlesource.com/platform/packages/modules/adb/+/HEAD/docs/dev/services.md | AOSP HEAD |
| AOSP `docs/dev/adb_wifi.md` — architecture of ADB Wi-Fi | https://android.googlesource.com/platform/packages/modules/adb/+/HEAD/docs/dev/adb_wifi.md | AOSP HEAD |
| `adb.1.md` man page (env vars, `server-status`, `wait-for` states) | https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/master/docs/user/adb.1.md | AOSP master |
| SDK Platform Tools release notes | https://developer.android.com/tools/releases/platform-tools | platform-tools 37.0.1 (July 2026) |
| AdvancedSharpAdbClient package page | https://www.nuget.org/packages/AdvancedSharpAdbClient | 3.6.16, published 2026-01-13; .NET 10 compatible |

## Version investigated

platform-tools **37.0.1**; adb Wi-Fi 2.0 (Android 17 + adb 37.x); Windows 11; .NET 10.

## Findings

1. **Host protocol.** The ADB server listens on TCP `127.0.0.1:5037`. A request is a 4-character
   hex length followed by the ASCII service name; the reply is `OKAY` or `FAIL` plus a
   length-prefixed reason. One-shot services close the connection; `track-*` services keep it open
   and push a new length-prefixed payload on every change.
2. **Event-driven device tracking exists in three variants:** `host:track-devices`,
   `host:track-devices-proto-binary`, `host:track-devices-proto-text`. Each payload is a complete
   device list, not a delta. The proto variants carry structured fields including the
   connection type (USB vs socket), bus address, product, model and device — i.e. a reliable
   transport signal that does not require inspecting the serial string.
3. **Event-driven mDNS tracking exists.** `adb mdns track-services` streams discovered services
   and supports `--proto-text` / `--proto-binary`; the official troubleshooting guide uses it as
   the canonical mDNS check. A host-service constant `track-mdns-services` appears in current
   Android tooling source, so the streaming service is reachable over the host protocol; the exact
   wire string must be confirmed against the installed adb (validation V-1). One-shot
   `host:mdns:services` and `host:mdns:check` also exist.
4. **Three distinct mDNS service types** (AOSP `adb_wifi.md`): `_adb._tcp` (legacy `adb tcpip`),
   `_adb-tls-pairing._tcp` (the device's pairing server is active — i.e. the user has the pairing
   screen open), `_adb-tls-connect._tcp` (the device's TLS server is advertising, whether or not
   this host is paired). Pairing and connecting are separate concepts; the pairing client also
   attempts a connect afterwards because `_adb-tls-connect` was already advertised before pairing
   began, so the host cannot rely on a connect "create" event.
5. **adb Wi-Fi 2.0** (Android 17 + adb 37.x; introduced with the 37.0.0 line): mDNS records carry `instance`, `service`, `ipv4`,
   `ipv6`, `port`, `product_model`, `build_version_sdk_full`, `given_name`, `serial`,
   `mdns_service_version`, `hostname`. Paired devices auto-connect when they join a trusted
   wireless network, so a device can appear in `track-devices` before a pairing call returns.
6. **mDNS backend.** Platform-tools **37.0.1** removed the deprecated `openscreen` backend.
   `libadbmdns` is now the only mDNS backend and `ADB_MDNS_OPENSCREEN` has no effect.
   `adb server-status` reports `version`, `mdns_enabled`, `mdns_backend` and the log path
   (`AdbServerStatus` in `adb_host.proto`). Historical guidance to force
   `ADB_MDNS_OPENSCREEN=1` is obsolete and must not be carried into ADB View.
7. **Device states** seen in current tooling: `device`, `offline`, `unauthorized`, `authorizing`,
   `connecting`, `recovery`, `rescue`, `sideload`, `bootloader`, `detached`, plus `host`.
8. **AdvancedSharpAdbClient 3.6.16** is not dead or obsolete: it was published **2026-01-13**
   and is compatible with .NET 10. It remains a viable general-purpose ADB client. It is not chosen
   here because the documented high-level API does not remove the custom work ADB View needs for
   structured proto device tracking with `connection_type`, streaming mDNS tracking, and
   `server-status`; mixing the library with a parallel custom host-protocol layer would enlarge the
   critical subsystem without eliminating that protocol code.

## Chosen approach

Speak the host protocol directly from C#: `AdbHostConnection` (framing + `OKAY`/`FAIL`), a
supervised `host:track-devices-proto-binary` stream, a supervised streaming mDNS tracker, a
one-shot `server-status` capability probe, `host:pair:<code>:<host>:<port>` for pairing, and
`host:transport:<serial>` + three constant shell commands for metadata. A ~40-line
forward-compatible protobuf field reader parses the proto payloads (known field numbers only,
unknown fields skipped by wire type). `adb.exe` is used only for `start-server` and as a documented
fallback for pairing, mDNS streaming, and `server-status`. Environment variables are never set.
Transport comes from `connection_type`; serial-shape parsing is confined to `Adb/DeviceIdentity.cs`
for hardware-serial recovery on wireless transports.

## Why

It removes polling entirely, gives a documented transport signal instead of heuristics, unlocks
Wi-Fi 2.0 metadata (real device names on pairing offers), keeps the critical path to one coherent
host-protocol implementation rather than combining a library with custom protocol extensions, and
is trivially fakeable — a scripted loopback server can reproduce every
recovery scenario in milliseconds.

## Rejected approaches

- **AdvancedSharpAdbClient 3.6.16** — current enough to be a legitimate option and .NET 10
  compatible, but it does not eliminate the custom modern host-protocol surfaces this architecture
  requires. Using it alongside bespoke tracking/mDNS/server-status code would create two ADB access
  paths in the most reliability-sensitive subsystem, so the smaller architecture is one focused
  host-protocol client.
- **Shelling `adb devices -l` on a timer** — polling plus fragile text parsing; forbidden by the brief.
- **Google.Protobuf + vendored `.proto` files** — a dependency and a regeneration ritual for three
  messages and about a dozen fields.
- **Our own mDNS browser (e.g. a .NET Bonjour/zeroconf library)** — a second multicast stack fighting
  the adb server and Windows for UDP 5353, and it would not see what the adb server sees (notably in
  WSL/VM/container setups where the server runs elsewhere).
- **Bundling platform-tools** — version skew on a machine-global singleton is the most common ADB
  failure mode; licensing and update burden on top.
- **Ever calling `host:kill`** — would break every other tool using the shared server.

## Compatibility concerns

platform-tools < 33 lacks the proto services → fall back to `host:track-devices-l` text parsing and
one-shot mDNS listing, logged once as degraded; transport may read `Unknown`. Pre-Android-17 devices
advertise fewer mDNS fields → offers fall back to instance-derived names. mDNS-blocked networks →
local `Wireless discovery unavailable` warning only; USB unaffected.

## Testing implications

- Unit: framing, payload caps, `FAIL` reasons, truncated frames, cancellation, proto reader with
  unknown-field skipping, malformed varints, identity resolution cases, state-literal mapping.
- Cross-check: for every captured fixture, binary parse must equal `--proto-text` parse.
- Fake server: device-list sequences, mid-stream death, `FAIL` on capability probe, mDNS appearance
  and withdrawal, pairing success/wrong-code/hang, server restart.
- Real hardware: capture transcripts for USB, wireless (Android ≤ 16 and Android 17), recovery,
  bootloader, sideload, unauthorized; commit them as fixtures.

## Open items

V-1 (exact host-service strings on installed adb 37.x), V-9 (protobuf reader vs proto-text on real
hardware), V-10 (Wi-Fi 2.0 auto-connect timing versus the pairing state machine), V-7 (battery via
`dumpsys battery` across OEMs).
