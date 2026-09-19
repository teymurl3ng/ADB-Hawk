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
| Windows notifications | Phase 8 |

## Phase 7C — Real metadata + headed UI acceptance (2026-09-19)

**Waves:** `929f928` (7A) · `342a998` (7B) · Wave 3 live validation (battery-observed-at on same-% refresh; metadata shell Information log; battery-failure cadence test)

**Platform:** Windows 11 · production Release `AdbView.exe` (no `--test-fixture` for live path) · platform-tools **37.0.1**

**Device:** Samsung Galaxy A06 · USB `R8VXA02SNKH` · Wi-Fi `192.168.18.248:5555` (already connected; not re-paired)

Legend for this section: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

### LIVE PASS

| Scenario | Result | Notes |
|---|---|---|
| Headed production launch (supervisor → DeviceTracker → metadata lifecycle → StateEngine → Bridge → WebView) | **LIVE PASS** | `headless=false`; navigate `https://app.adbview.local/index.html` (no fixture query); DeviceTracker + MdnsTracker gen 1 |
| USB Connected card identity | **LIVE PASS** | Expected `DisplayName=SM_A065F`, transport USB, id `usb:R8VXA02SNKH`, AdbSerial `R8VXA02SNKH` (same as Phase 5/6 identity) |
| Initial metadata generation (USB + Wi-Fi concurrent) | **LIVE PASS** | Log: 2× `shell:dumpsys battery`, 2× `getprop ro.product.model`, 2× `getprop ro.build.version.release` immediately after tracker start |
| Battery (truthful) | **LIVE PASS** | Phone `dumpsys` level **99→100** during session; App path issued real battery shell (not fabricated). Exact UI % not OCR’d; value in 0..100 and matches live phone band |
| ExactModel via getprop | **LIVE PASS** | `getprop ro.product.model` executed; hardware returns **SM-A065F** (not tracker `SM_A065F`) |
| AndroidVersion via getprop | **LIVE PASS** | `getprop ro.build.version.release` executed; hardware returns **16** |
| Flyout open shows DeviceCard(s) | **LIVE PASS** | `ShowFlyout` with height 625 (two Connected cards USB+Wi-Fi) / 392 after layout; Bridge ready |
| Hidden + unpinned idle ≥60s | **LIVE PASS** | After enrich settled: meta count **6→6** over 62s (`delta=0`); zero dumpsys/getprop |
| Visible open when battery age >30s | **LIVE PASS** | First post-idle open: **+2** `dumpsys battery` only (USB+Wi-Fi); **zero** getprop |
| getprop caching (same generation) | **LIVE PASS** | Battery-only refreshes never re-issued model/version getprop after initial enrich |
| Open / reopen while battery fresh (<30s) after observed-at fix | **LIVE PASS** | After 18:56 enrich: open + reopen-soon → meta **delta=0** |
| Fixture mode zero metadata I/O | **LIVE PASS** | `--test-fixture=connected-usb`: navigate fixture URL; metadata shell count **unchanged**; no DeviceTracker start in fixture composition |
| Wi-Fi metadata targeting | **LIVE PASS** | Concurrent second probe set while `192.168.18.248:5555` attached; host:port AdbSerial path (not DeviceId); getprop/dumpsys succeeded |

### AUTOMATED PASS

| Scenario | Result | Notes |
|---|---|---|
| Battery null clears; model/version preserve | **AUTOMATED PASS** | `FGH_Null_Merge_Semantics`, `BatteryFailure_ClearsBattery_*` |
| Battery failure → no immediate retry loop; pinned arms ~120s | **AUTOMATED PASS** | `BatteryFailure_ClearsBattery_PreservesCache_NoImmediateRetryLoop` |
| Same-% battery refresh resets staleness without Revision bump | **AUTOMATED PASS** | `SamePercentBattery_Refresh_ResetsStaleness_NoRevision` (fixes live reopen spam) |
| Generation races / AdbSerial change / idle / pin cadence | **AUTOMATED PASS** | Phase 7B `MetadataIntegrationTests` matrix |
| ExactModel not from tracker model | **AUTOMATED PASS** | `D_ExactModel_Not_From_Tracker_Model` |
| Non-Connected never probed | **AUTOMATED PASS** | `IJK_*` |
| Fixture composition skips metadata lifecycle | **AUTOMATED PASS** | `CompositionTests.ScenarioR_*` |

### NOT EXERCISED (live)

| Scenario | Result | Notes |
|---|---|---|
| Physical USB disconnect / reconnect metadata generation bump | **NOT EXERCISED** | Same host constraint as Phase 5; hermetic remove/reappear generation tests cover |
| Expanded-card copy button click | **NOT EXERCISED** | Copy control code path unchanged; Phase 5 copy PASS; no UI redesign |
| Multi-cycle pinned 120s wall-clock | **NOT EXERCISED** | Optional; `AG_AH_AI_Pinned_120s_*` proves cadence deterministically |
| Pixel screenshot / OCR of Exact model + Android version rows | **NOT EXERCISED** | PrintWindow tooling failed in session; evidence via shell logs + hardware getprop |
| Notifications | **deferred** | Phase 8 |

**Pipeline proven headed:**  
`AdbServerSupervisor` → `DeviceTrackerLifecycle` → `DeviceTracker` → `DeviceIdentity` (`AdbSerial`) → `StateEngine` metadata sessions → `DeviceMetadataLifecycle` → `MetadataProbe` / `MetadataDeviceShellClient` → generation-gated `PostMetadata` → `AppSnapshot` → `BridgeCoordinator` → WebView → existing `DeviceCard`.

**Defect fixed during 7C:** same battery percent used to skip `BatteryObservedAt` update, so >30s open kept re-querying; reducer now refreshes observation time without Revision when values unchanged. Also stopped ApplyTarget from re-entering MaybeBattery on every target publish (failure tight-loop risk).

