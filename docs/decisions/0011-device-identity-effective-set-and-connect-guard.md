# 0011 — Device identity uses the effective discovery set; post-pair connect guard

- **Date:** 2026-10-01
- **Status:** ACCEPTED — explicit human contract change (Phase 12 follow-up)
- **Phase:** 12 (double-card defect found in live QR-pairing validation)

## Issue

Live QR-pairing validation showed the same phone as **two** `Connected · Wi-Fi` cards. Audit
(`DeviceIdentity` rule 2c, `DeviceViewMapper.MapList` seen-Id merge):

- adb has **no cross-port dedup**: each transport is a separate `ip:port` entry (`adb connect`
  is idempotent only per-endpoint). A stale transport from an earlier session plus a fresh one
  is genuinely two rows in `track-devices` — the field hypothesis ("dead connection") confirmed
  as a real adb-level state.
- The UI collapses two transports into one card **only** when both resolve to the same hardware
  serial via mDNS (rule 2c: exact IP+port match against `_adb-tls-connect._tcp` rows).
- **The defect:** `ReduceDeviceList` fed that resolution only the **ADB server's** mDNS list.
  On hosts where the ADB browse is broken (the discovery-health origin), resolution always
  misses, every Wi-Fi device keeps a per-port identity, nothing merges, and both transports
  render as separate cards with duplicated metadata probes. The fallback browser — built to
  replace exactly the missing data — was never fed to identity.

## Decision

1. **Identity enrichment source = the effective service set.** `ReduceDeviceList` normalizes
   devices against `EffectiveServices(LatestMdnsServices, LatestFallbackServices)` — the same
   adb-authoritative/fallback-fills rule as offers. Rule 2c's algorithm is unchanged; only the
   data source widens. Consequence: two transports whose connect rows resolve to the same
   hardware serial collapse to `wifi:<serial>` and the existing seen-Id merge yields one card.
   USB↔Wi-Fi same-serial remains two cards (pinned, §6.7).
2. **Post-pair connect guard (0009 amendment):** `PostPairConnectCoordinator` skips the
   `host:connect` when the tracker already shows a Connected Wi-Fi device whose address host
   equals the connect row's IP (any port) — the phone is already reachable over Wi-Fi; a second
   transport adds nothing and is the very mechanism that produced the double card alongside the
   server's own auto-connect.

## Honest limits

- adb may still hold the stale transport server-side; with (1) it is invisible in the UI and
  dies when TCP teardown is noticed. The product does **not** issue `host:disconnect` (rejected:
  outside the observation scope; would need its own decision).
- If a connect row lacks a `serial` field or the tracker reports an address form the row doesn't
  match, collapse can still miss — logged findings, not silently absorbed.

## Testing

- Reducer: two `ip:port` transports + fallback connect rows (same serial) → **one** card; the
  adb-source-only variant behaves exactly as before; USB↔Wi-Fi same-serial still two cards.
- Coordinator: same-host connected Wi-Fi device → no connect; different host → fires as before.
- Live matrix row: real double-transport scenario collapses to one card.

## Amendment (2026-10-02) — serial extraction + host-only match (live-verified gap)

The original 0011 collapse was tested against rows carrying an explicit `Serial` field — but
production fallback rows never had one (the browser parses only `name`/`api` from TXT), so the
fix never fired in the field and one phone still rendered as two cards. Amendment:

1. **Serial extraction moved into the identity layer**
   (`DeviceIdentity.ExtractSerialFromInstanceName`): the AOSP instance name carries the serial —
   TLS-connect `adb-<serial>-<6 random alphanum>` (greedy middle tolerates dashes), legacy
   `adb-<serial>`. The Host mapper delegates to it so fallback rows carry `Serial`.
2. **Host-only second pass**: exact IP+port matching stranded a STALE transport (its port has no
   advertisement anymore — the phone re-announces only the current port). When no exact match
   resolves, any Connect row for the same host (IP) supplies the serial. One phone = one LAN IP;
   distinct phones have distinct IPs (guard test included).
3. Mapper tests pin extraction; reducer tests are production-shaped (rows without `Serial`,
   stale-port scenario); USB↔Wi-Fi two-card and no-discovery degradation behaviors unchanged.

Rejected: `host:disconnect` of the stale transport (outside observation scope — the collapsed
UI hides it and adb reaps it when TCP teardown is noticed).
