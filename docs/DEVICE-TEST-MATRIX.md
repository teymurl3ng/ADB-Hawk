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

## Phase 6C / 6D — Discovery + pairing into state + live acceptance (2026-09-19)

**Waves:** `eeed0ad` (6A) · `52a912b` (6B) · `4177b79` (6C) · closure commit `a57e0b8` / tag `phase-6-complete`

**Platform:** Windows 11 · production Release `AdbView.exe` (no `--test-fixture`) · platform-tools **37.0.1-15733141**

**Device:** Samsung Galaxy A06 · serial `R8VXA02SNKH` · model `SM_A065F`

### LIVE PASS

| Scenario | Result | Notes |
|---|---|---|
| Headed production path (supervisor → MdnsTrackerLifecycle → StateEngine → Bridge → WebView) | **PASS** | MdnsTracker started; Bridge ready; no fixture URL |
| `adb pair` / equivalent `host:pair` with six-digit code on stdin (leading zeros) | **PASS** | Multiple successful pairs; codes **absent** from Adb View logs |
| Code remains string / leading-zero capable | **PASS** | e.g. code with leading `0` accepted by pair |
| No synthetic DeviceView from pair alone | **PASS** | Pairing success did not fabricate a card; card only after tracker/`adb connect` |
| Real Wi-Fi DeviceTracker → AppSnapshot | **PASS** | After `adb connect 192.168.18.248:5555`: `SM_A065F` · Connected · Wi-Fi · id `wifi:192.168.18.248:5555` (no mDNS enrichment → host:port id) · tray `Connected: SM_A065F (Wi-Fi)` |
| Secret redaction | **PASS** | Pairing codes not present in `%LOCALAPPDATA%\ADBView\logs` |

### AUTOMATED PASS (hermetic — not claimed as live hardware)

Covered by `Phase6CIntegrationTests`, PairingCoordinator/HostPairClient tests, MdnsTracker fakes, Composition/Boundary tests: offer map/withdraw; legacy/connect ≠ offer; paired ≠ connected; USB/unrelated Wi-Fi do not clear waiting; matching Wi-Fi clears waiting; 60 s quiet clear; cancel drops late success; fixture isolation; Vocabulary pairing/warning copy.

### NOT EXERCISED (live) — host mDNS browse empty

Host `adb mdns services` / `host:track-mdns-services` returned **empty complete lists** while pairing ports were TCP-reachable and `adb pair` succeeded. Audited in `research/2026-09-19-host-mdns-empty-while-tcp-works.md` (not an Adb View PairingOffer bug). Google empty-track guidance: treat as network/host mDNS unsupported from the workstation.

| Scenario | Result | Notes |
|---|---|---|
| Live `_adb-tls-pairing._tcp` discovery | **not exercised** | Host browse empty with pairing screen open |
| Live PairingOffer in flyout | **not exercised** | Requires non-empty pairing mDNS |
| Inline Pair → code entry → `PairedWaitingForDevice` via app UI | **not exercised** | Blocked on missing offer |
| ADB auto-connect after pair (no manual connect) | **not exercised** | Auto-connect did not attach; manual `adb connect` used for Wi-Fi observation |
| USB + Wi-Fi coexistence cards | **not exercised** | USB dropped mid-session; only Wi-Fi observed later |
| Offer withdrawal when pairing screen closes | **not exercised** | No live offer |
| Wrong-code failure in flyout | **NOT EXERCISED** | Optional; not run |

**Pipeline proven headed (partial):**  
`AdbServerSupervisor` → `DeviceTrackerLifecycle` → `DeviceTracker` → `StateEngine` → Wi-Fi `DeviceView` → Bridge → React.  
`MdnsTrackerLifecycle` runs, but live pairing-offer UI path awaits host mDNS browse recovery.

### Deferred

| Item | Notes |
|---|---|
| Battery / Android version metadata | Phase 7 |
| Windows notifications | Phase 8 |
