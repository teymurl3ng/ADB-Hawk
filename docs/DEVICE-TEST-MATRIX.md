# Device test matrix

Real-hardware checklist for ADB View. Mocks and `FakeAdbServer` do not prove
ADB integration; rows here record what was observed on physical devices.

Legend:

- **PASS** — exercised live on the named hardware and observed correct product behaviour
- **not exercised / hermetically covered** — not live-observed in this run; covered by
  automated `FakeAdbServer` / unit / composition tests
- **deferred** — out of scope for the current phase (see `PHASES.md`)

---

## Phase 5 — Device tracking (2026-09-19)

**Closure commit:** `a64ae7e`

**Platform:** Windows 11 · production Release `AdbView.exe` (no `--test-fixture`)

**Device:** Samsung Galaxy A06 family · serial `R8VXA02SNKH` · USB debugging authorized

| Scenario | Result | Notes |
| -------- | ------ | ----- |
| USB device appears in production flyout | **PASS** | Display name `SM_A065F`, state `Connected`, transport `USB`, id `usb:R8VXA02SNKH` |
| Headed `DeviceCard` (non-fixture) | **PASS** | One card; not fixture data; geometry unchanged |
| Expanded Model / Serial | **PASS** | Model `SM_A065F`, Serial `R8VXA02SNKH` |
| Battery omitted (Phase 5) | **PASS** | `batteryPercent` null; no fabricated % |
| Android version omitted (Phase 5) | **PASS** | `androidVersion` null; no fabricated version |
| Tray aggregate Connected | **PASS** | `health.tray = connected` |
| Tray tooltip | **PASS** | `Connected: SM_A065F (USB)` |
| USB disconnect / card removal | **not exercised / hermetically covered** | Software USB disable unsupported on this host; physical unplug unavailable. Hermetic: valid empty tracker list clears devices immediately (not the 2 s grace path). |
| USB reconnect / no duplicate card | **not exercised / hermetically covered** | Same constraint. Hermetic: complete-list replacement + identity id stability. |
| Unauthorized / offline / recovery / multi-device live | **not exercised / hermetically covered** | Hermetic + fixture coverage; no additional live hardware for these states in Phase 5. |
| Wireless pairing UI / metadata / notifications | **deferred** | Phases 6B–8 |

**Pipeline proven headed:**  
`AdbServerSupervisor` → `DeviceTrackerLifecycle` → `DeviceTracker` → `DeviceIdentity` → `StateEngine` → `StateReducer` → `AppSnapshot.Devices` → `BridgeCoordinator` → WebView → React `DeviceCard`.

## Phase 6A — mDNS discovery domain (2026-09-19)

Non-mutating probes only against platform-tools **37.0.1-15733141** and Samsung A06 (`R8VXA02SNKH`). No pairing, no connect, no wireless-debugging toggles.

| Check | Result | Notes |
|---|---|---|
| `host:track-mdns-services` | **PASS** | OKAY + complete-list binary `MdnsServices` (legacy `_adb._tcp`) |
| `adb mdns track-services --proto-text` | **PASS** | Same service; CLI binary mode unsafe on Windows (CRLF corruption) |
| `host:mdns:services` / `adb mdns services` | **PASS** | `adb-R8VXA02SNKH	_adb._tcp	192.168.18.248:5555` |
| `_adb-tls-pairing` / `_adb-tls-connect` live | **not observed** | Pairing screen not open; not fabricated |
| AppSnapshot / pairing UI wiring | **deferred** | Phase 6C |
