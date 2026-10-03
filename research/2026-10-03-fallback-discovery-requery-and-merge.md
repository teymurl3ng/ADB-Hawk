# Fallback discovery: QR-start query burst + instance-level merge

- **Date:** 2026-10-03
- **Author:** coding agent (ZCode session, auditapproved by owner)
- **Phase:** decision 0015 native-UI migration, post-I2 hardening
- **Status:** current

## Problem

On the owner's machine (2026-10-03), wireless QR pairing never completes: the phone's
`_adb-tls-pairing._tcp` announcement never reaches the app's effective discovery set.
Two independent causes were proven:

1. `adb mdns services` returns an **empty list** (adb's own browse is deaf) while UDP 5353 is
   shared by two `chrome.exe` processes and one `svchost.exe` (Windows mDNS), with a Tailscale
   adapter up. This matches `research/2026-09-19-host-mdns-empty-while-tcp-works.md`
   (SO_REUSEADDR contention) and the in-app health card evidence.
2. The fallback browser could not cover the gap within a QR attempt window: every observed
   QR attempt (1.2–31 s) died inside one empty-set requery silence gap, because
   `ComputeRequeryDelay` returns the 60 s `RequeryFloor` when the instance set is empty
   (`MdnsFallbackBrowser.cs:188-190`) and the only burst fires at browser start
   (`MdnsFallbackBrowser.cs:92,105`).

Additionally, the "adb authoritative" effective-set rule (`EffectiveServices`,
`StateReducer.cs:536-547`, decisions 0008/0011) replaces the whole set with adb's rows whenever
adb reports **any** rows — a half-deaf adb (some connect rows, no pairing row) masks exactly the
fallback pairing row the QR flow needs.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| RFC 6762 (mDNS), §4.1 requery guidance, §6 multi-packet | https://www.rfc-editor.org/rfc/rfc6762 | 2013-02 |
| AOSP adb_wifi.md (pairing service lifecycle) | https://android.googlesource.com/platform/packages/modules/adb/+/main/docs/adb_wifi.md | current main, 2026-10 |
| Existing project research: dnssd fallback browser | research/2026-10-01-dnssd-fallback-browser.md | 2026-10-01 |
| Existing project research: host mdns empty while tcp works | research/2026-09-19-host-mdns-empty-while-tcp-works.md | 2026-09-19 |
| Windows mDNS + SO_REUSEADDR sharing | observed on this host (netstat 2026-10-03: 0.0.0.0:5353 held by chrome ×2 + svchost) | Windows 11 26100 |

## Version investigated

Windows 11 10.0.26100 x64; platform-tools adb 37.x (mdns backend ndk); app main @ `3f3f8b4`;
device Samsung Galaxy A06 (Android 16).

## Findings

- RFC 6762 §4.1 sanctions requerying at ≤80 % of remaining TTL; it does not forbid an
  additional query burst on an explicit local user action. Aggressive continuous querying is
  what the RFC discourages (battery/network load); one burst per QR-session start is a
  user-initiated, rate-limited event (bounded by how often a human starts pairing).
- AOSP adb_wifi.md: the phone starts its TLS pairing server and announces
  `_adb-tls-pairing._tcp` **only while the pairing dialog is open** — a window of seconds to a
  few minutes. A querier silent during that window misses the service entirely (announcements
  are multicast; an active-only querier that shared-bind failed never hears them).
- Observed (this host, 2026-10-03): the fallback browser ran active-only or passive-bound on
  the shared socket, but its only query burst predated every QR attempt; `ComputeRequeryDelay`
  kept the next burst 60 s away. `adb mdns services` empty throughout.
- Documentation is silent on Windows SO_REUSEADDR share semantics for mDNS; behavior observed
  matches the prior research notes.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Query burst triggered by QR-session start (chosen) | Event-driven (R-05-clean: reaction to explicit user action, not a timer); directly covers the announcement window | Needs a thread-safe trigger into the browser loop |
| Shrink the empty-set requery floor globally | One-line change | Continuous background polling the RFC discourages; R-05 timer discipline |
| Per-kind effective-set merge | Fixes masking for Pairing kind only | Still masks fallback *connect* rows; kind-grained exceptions are harder to reason about |
| Instance-level (DiscoveryKey) union merge (chosen) | Fixes masking for every kind; adb rows still win per key; no duplicates by construction; identity (DiscoveryKey) unchanged | Reducer change + contract note (0016) |
| Multicast egress interface pinning | Fixes wrong-adapter egress | Highest-risk socket change; deferred to its own research pass |

## Chosen approach

1. `MdnsFallbackBrowser.TriggerQuery()`: sets an interlocked flag the run loop honors on its
   next slice (≤ `LoopSlice` latency) by sending a query burst immediately. Exposed via
   `MdnsFallbackLifecycle.TriggerQuery()`; AppHost calls it when a QR pairing session starts
   (`StartQrPairingSession`), covering the phone's announcement window.
2. `MdnsEffectiveSet.Merge(adb, fallback)` in `State/`: instance-level union by
   `RawMdnsService.DiscoveryKey` — adb rows win per key, fallback rows fill only keys adb does
   not report. Used by the reducer's `EffectiveServices`, `QrPairingSession.OnSnapshotPublished`,
   and `PostPairConnectCoordinator`. QR handoff precondition (4) is unchanged otherwise.

## Why

The QR flow is otherwise correct end-to-end (audit 2026-10-03: preconditions 1–5 verified in
code); both changes remove proven environmental-coverage gaps without touching adb citizenship
(R-14: no adb kill/config; we only time our own fallback queries and merge our own view).
Instance-level merge supersedes the whole-set rule of 0008/0011 (see
docs/decisions/0016-fallback-discovery-merge-and-qr-burst.md); duplicate-device risk is
bounded by the same DiscoveryKey identity the offer bindings and double-card fix (0011)
already rely on.
