# Device display-name metadata (marketname / device_name)

- **Date:** 2026-09-21
- **Author:** agent
- **Phase:** Pre-Phase-10D polish (not Phase 10D)
- **Status:** current

## Problem

DeviceCard titles were showing tracker `model` values such as `SM_A065F` instead of
recognizable product names (`Galaxy A06`, `Redmi Note 14`). Need offline, OEM-supplied
candidates without an internet model database or hard-coded SM-* maps.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| AOSP `Build.MODEL` / `ro.product.model` | https://developer.android.com/reference/android/os/Build#MODEL | API 36 docs |
| AOSP `Settings.Global.DEVICE_NAME` | https://developer.android.com/reference/android/provider/Settings.Global#DEVICE_NAME | API 25+ |
| AOSP WirelessDebuggingFragment (device name → Build.MODEL fallback) | AOSP `packages/apps/Settings` WirelessDebuggingFragment | platform mainline |
| Xiaomi HyperOS `ro.product.marketname` | Xiaomi HyperOS developer docs (marketed device name) | HyperOS docs 2024–2025 |

## Version investigated

- platform-tools / host protocol: existing ADB View metadata shell path
- Android API 25+ for `settings get global device_name`
- OEM property `ro.product.marketname` (Xiaomi/HyperOS; optional elsewhere)

## Findings

1. Authorized Samsung / Xiaomi / Pixel / other OEMs use the same ADB shell transport after
   authorization; differences are metadata content, not protocol.
2. `Build.MODEL` is backed by `ro.product.model` (technical / certification model on some OEMs).
3. Wireless Debugging UI prefers `Settings.Global.DEVICE_NAME`, then `Build.MODEL`.
4. Xiaomi documents `ro.product.marketname` as the marketed/sales name and recommends falling
   back to `ro.product.model` when marketname is empty.
5. `device_name` is user-editable and may be a custom label (`Work Phone`); absence/`null` is normal.
6. Tracker proto `model` may normalize differently than shell `ro.product.model` (e.g. `SM_A065F` vs `SM-A065F`).

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Online GSMArena / model DB | Covers SM-* codes | Network, size, maintenance, R-13/offline violation |
| Hard-coded Samsung/Xiaomi maps | Fast offline | Unmaintainable; R-09/scope |
| Dump entire `getprop` | One round-trip | Large parse surface; unpredictable |
| Targeted fixed shell probes | Predictable, cached, offline | Extra shells on full probe only |

## Chosen approach

Central `DeviceDisplayName.Resolve`:

1. `ro.product.marketname`
2. `settings get global device_name`
3. `ro.product.model` (with conservative brand+model when model looks like a code)
4. tracker product / early given_name
5. `"Android device"`

Reject empty / `null` / `unknown` / transport-like (`adb-*`, host:port, `_adb-tls-*.tcp`).
`ExactModel` stays shell model; React renders `displayName` only.

## Why

Matches AOSP + Xiaomi guidance, stays offline, one semantic rule for flyout/notifications/pairing,
no protocol change, generation-cached with existing Phase 7 metadata lifecycle.

## Rejected approaches

- Internet lookup / bundled model tables
- Undocumented OnePlus/Oppo private props without live evidence
- Polling `device_name` for live renames
- Letting React implement fallback chains
- Using transport identity as DisplayName

## Compatibility concerns

Older Android may lack `device_name` or `marketname` — fall through to model/brand.
Emulators keep tracker identity; resolve normally when props exist.

## Testing implications

Hermetic resolver matrix (Samsung/Xiaomi/Pixel/rename/old/malformed), metadata refinement
integration (id / ConnectedSince stable), AllowedShellCommandSuffixes = 7, USB/Wi-Fi same
DisplayName from same shell metadata, mutations M1–M6.

## Open items

Live OEM acceptance on available authorized devices (Samsung A06 minimum).
