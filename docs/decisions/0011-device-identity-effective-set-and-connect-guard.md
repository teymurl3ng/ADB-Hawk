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
