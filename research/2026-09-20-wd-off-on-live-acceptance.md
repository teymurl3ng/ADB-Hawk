# Live Wireless Debugging OFF/ON acceptance (Samsung USB + WD)

- **Date:** 2026-09-20
- **Device:** Samsung SM-A065F (`R8VXA02SNKH`) USB; peer SM-S921W Wi-Fi ignored for A06-scoped checks
- **Method:** `settings put global adb_wifi_enabled` 0/1 while USB remained attached; sample `adb devices -l` + `adb mdns services`
- **Grace:** waited ≥4 s after OFF (beyond ENGINEERING 2 s DeviceTracker grace)
- **Artifact:** `artifacts/diagnosis/wd-off-on-live.jsonl`
- **Script:** `scripts/capture-wd-off-on.ps1` (one-off capture; removed from tree after acceptance — results live in this note / artifacts if retained)

## Results (raw tracker/mDNS)

| Check | Result |
|---|---|
| USB retention | **PASS** — USB serial present every sample |
| Stale Wi-Fi removal | **PASS** — A06 `_adb-tls-connect` device + mDNS gone by T1; still gone after grace |
| Stale offer removal | **NOT_EXERCISED** — no `_adb-tls-pairing` (pairing UI not open) |
| Clean reappearance after WD ON | **PASS** — A06 connect service + Wi-Fi device returned by T5 (~5 s) |

## AppSnapshot / UI

Correlate flyout DeviceViews with the same window; no PairingOffer/PairingOperation present without pairing advertisement.
