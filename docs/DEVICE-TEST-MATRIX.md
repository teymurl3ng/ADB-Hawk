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

**Waves:** `929f928` (7A) · `342a998` (7B) · `44a3ff5` (7C live validation) · 7D closure (this docs wave)

**Idle-cost acceptance (retained):** connected device + initial enrichment complete + flyout hidden and unpinned ≥60s → **ZERO** metadata shell requests (live-proven ≥62s, meta count 6→6).

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

## Phase 8 — Windows notifications (2026-09-19)

**Waves:** `ccfba10` (8A) · `9ab81a5` (quiet-time) · `2e25be4` (8B) · `7b8b232` (8C) · 8D closure (this docs wave)

**Platform:** Windows 11 · production Release `AdbView.exe` (no `--test-fixture`, no headless, real `BalloonNotificationSink` + `TrayIcon`) · platform-tools **37.0.1**

**Device:** Samsung Galaxy A06 · USB `R8VXA02SNKH` · Wi-Fi `192.168.18.248:5555`

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

### LIVE PASS

| Scenario | Result | Notes |
|---|---|---|
| Startup with USB+Wi-Fi already attached → zero notifications | **LIVE PASS** | After baseline + 5s suppress: no `emitting` / `balloon delivered` before operator action |
| Real Wi-Fi disconnect → DeviceDisconnected balloon | **LIVE PASS** | External `adb disconnect 192.168.18.248:5555`; path Offline→absent cancelled DeviceProblem then emitted DeviceDisconnected; `host.tray balloon delivered (Info)` at 22:42:28; title `SM_A065F disconnected`; body title-only (empty → delivery space); no DeviceId/IP/pairing code in title |
| Real Wi-Fi reconnect → DeviceConnected balloon | **LIVE PASS** | External `adb connect 192.168.18.248:5555`; one DeviceConnected; title `SM_A065F connected`; body Vocabulary `Connected over Wi-Fi`; severity Info; `balloon delivered` at 22:43:10 |
| Stable no-spam after transitions | **LIVE PASS** | ≥60s idle after reconnect: no duplicate Connected/Disconnected; metadata refresh silent |
| Fixture / headless externally silent | **LIVE PASS** | Production headed only constructs real sink; fixture/headless `NullNotificationSink` (composition + live fixture path) |

### AUTOMATED PASS

| Scenario | Result | Notes |
|---|---|---|
| Startup baseline / 5s suppress / holds / flap / disconnect / grace / Unauthorized / Offline / AdbUnavailable / silent restore / pairing cooldown / PairingResult / metadata silence / wireless-warning silence / burst / resume+8s / shutdown / sink failure isolation / Explorer recreation / click show-only / fixture silence / balloon severity+privacy | **AUTOMATED PASS** | `NotificationTimelineTests`, `NotificationIntegrationTests` (incl. Mut1–8), `BalloonNotificationSinkTests`, Composition fixture/headless |
| Offline→absent → DeviceDisconnected (not stuck Problem) | **AUTOMATED PASS** | `L2_Offline_Then_Absent_Disconnects` |
| Connected hold survives metadata-style snapshot | **AUTOMATED PASS** | `L3_Connected_Hold_Survives_Metadata_Snapshot` (live defect: bare `else` cancelled Connected on still-connected snapshots) |

### NOT EXERCISED (live)

| Scenario | Result | Notes |
|---|---|---|
| OS-visible clickable banner + BalloonTipClicked → flyout | **NOT EXERCISED** | Sink delivery proven (`ShowBalloonTip` + `balloon delivered`); UI Automation found no banner; **OS presentation NOT OBSERVED** — do not claim live click PASS. Hermetic click → `ShowFlyoutOnly` remains covered |
| Physical USB unplug/replug balloons | **NOT EXERCISED** | Wi-Fi path preferred and exercised |
| AuthorizationRequired / PairingAvailable / AdbUnavailable live balloons | **NOT EXERCISED** | Hermetic coverage; not deliberately provoked |
| Focus Assist / DND claims | **NOT EXERCISED** | Not asserted |

**Closure defects fixed in 8D:** (1) WinForms rejects empty disconnect tip text — `TrayIcon.ShowBalloon` substitutes a single space; (2) Offline→absent arms DeviceDisconnected; (3) Connected hold no longer cancelled by still-connected follow-up snapshots (metadata).

**Pipeline proven headed:**  
`StateEngine.SnapshotPublished` → `NotificationCoordinator.AcceptSnapshot` → hold → `BalloonNotificationSink` → `TrayIcon.ShowBalloonTip`.

---

## Phase 9 — Reliability / chaos (2026-09-19)

**Platform:** Windows 11 · Release tests + `scripts/soak-phase9.ps1` · platform-tools **37.0.1**

**Device:** Samsung Galaxy A06 · USB `R8VXA02SNKH` · Wi-Fi `192.168.18.248:5555`

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

### LIVE PASS

| Scenario | Result | Notes |
|---|---|---|
| Wi-Fi `adb disconnect` / `adb connect` churn | **LIVE PASS** | External disconnect removed `192.168.18.248:5555`; reconnect restored device (transport_id 10). Safe Phase 9 churn only — no kill-server / revoke-auth / recovery modes |
| USB remains while Wi-Fi flaps | **LIVE PASS** | USB `R8VXA02SNKH` stayed `device` across Wi-Fi disconnect/reconnect |

### AUTOMATED PASS

| Scenario | Result | Notes |
|---|---|---|
| §11 rows 1, 3, 5, 8, 11, 26 (hermetic Chaos*) | **AUTOMATED PASS** | `tests/.../Integration/Chaos/ChaosHermeticTests` + FakeAdbServer; no kill-server |
| §11 rows 16, 17 (WebView backoff / tray-only) | **AUTOMATED PASS** | `ChaosWebViewRecoveryTests` — recreate 1/5/15/60 + 3-in-5-min tray-only; renderer path |
| §11 row 24 (second instance mutex) | **AUTOMATED PASS** | `ChaosWindowsLifecycleTests.Row24_*` |
| §11 row 19 TaskbarCreated path | **AUTOMATED PASS** | Hermetic HandleTaskbarCreated + existing BalloonNotificationSink Explorer recreate |
| Channel backlog warning (row 26) | **AUTOMATED PASS** | Threshold 256, 30 s rate-limit; `StateEngine.PendingCount` |
| Hermetic churn soak (2000 list cycles) | **AUTOMATED PASS** | `Hermetic_Churn_Soak_TwoThousand_List_Cycles` |
| Audit: no bare `catch {}` / no `lock` in State / no Adb·State Delay loops | **AUTOMATED PASS** | `ChaosAuditTests` |
| Soak smoke (budget gates) | **AUTOMATED PASS** | `scripts/soak-phase9.ps1 -Minutes 3 -SampleSeconds 20` → `SOAK_OK`; WS/handles Δ gates; log `%LOCALAPPDATA%\ADBView\logs\soak-phase9.log` |
| Idle hidden metadata (re-check) | **AUTOMATED PASS** | Phase 7 hidden+unpinned zero metadata remains; no new Adb/State timers introduced in Phase 9 |

### NOT EXERCISED (live)

| Scenario | Result | Notes |
|---|---|---|
| Real Explorer process kill/restart with live tray | **NOT EXERCISED** | Gated: `ADBVIEW_CHAOS_EXPLORER=1` + `Row19_Real_Explorer_Restart_Gated`; hermetic TaskbarCreated covered |
| Live Chromium ProcessFailed (renderer/browser) | **NOT EXERCISED** | Test-hook SimulateProcessFailed; no forced live WebView kill |
| Full 12-hour overnight soak | **NOT EXERCISED** | Same script default `-Hours 12`; smoke gates proven. Run overnight before Phase 11 release hardening |
| Lock/unlock / suspend/resume live | **NOT EXERCISED** | Covered hermetically via notification resume rebaseline + PowerAndSession seams from prior phases |
| Forced recovery / bootloader / revoke-auth | **NOT EXERCISED** | Explicitly out of Phase 9 safe LIVE scope |

**Production gaps closed:** WebView recreate backoff aligned to ENGINEERING §9.7; StateEngine backlog warning; DeviceTracker/MdnsTracker degraded/malformed Serilog wiring; `State/` `lock` removed from DeviceViewMapper once-set.

**Deferred visuals (parked):** `docs/decisions/0002-park-phase9-deferred-acrylic-typography.md` → Phase 11.

## Phase 10C — Package lifecycle acceptance (2026-09-20)

**Closure:** **COMPLETE** (human-approved scope decision 2026-09-20).

Clean-machine x64, ARM64 runtime, WebView2-missing runtime, real reboot auto-start, and SmartScreen/MOTW validation are deferred to Phase 11 / pre-public-v1 release hardening and remain **NOT EXERCISED**. This is intentional — not a claim that those tests passed.

**Evidence base:** packaging fixes (Run clear, `{app}\wwwroot` InstallDelete, absolute ISCC paths); `scripts/test-install-acceptance.ps1`; host Windows 11 LTSC Eval Build 26100 x64; WebView2 Evergreen **153.0.4234.48**.

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED** — do not collapse these levels.

### Accepted Phase 10C evidence

| Scenario | Result | Notes |
|---|---|---|
| x64 host installer (per-user, no elevation) | **LIVE PASS** | `%LOCALAPPDATA%\Programs\ADBView`; Start Menu `ADB View`; no desktop shortcut |
| x64 portable ZIP | **LIVE PASS** | extract + run; LocalAppData logs/WV profile; no uninstall/Run registration |
| First launch (tray + flyout + opaque surface) | **LIVE PASS** | one process; WebView loads; no native caption; dark opaque |
| no-ADB (platform-tools removed) | **LIVE PASS** | no DeviceTracker/metadata; process stable; truthful unavailable path |
| platform-tools recovery | **LIVE PASS** | LocalAppData Android SDK discovery; supervisor recovery; A06 USB observed |
| single-instance | **LIVE PASS** | mutex/pipe; count stays 1 across second launch / portable |
| optional Run registration | **LIVE PASS** / **AUTOMATED PASS** | `--startup` tray-only; reinstall without task clears Run |
| upgrade N→N+1 (stopped) | **LIVE PASS** / **AUTOMATED PASS** | same AppId; one uninstall entry; version metadata updated |
| running-app upgrade protection | **LIVE PASS** | `AppMutex`; silent N+1 exit 1; app remains; no force-kill |
| stale Vite wwwroot purge | **LIVE PASS** / **AUTOMATED PASS** | `[InstallDelete] {app}\wwwroot`; sentinel removed |
| uninstall cleanup | **LIVE PASS** / **AUTOMATED PASS** | program dir, shortcut, Run, WV profile, logs removed; SDK + Evergreen preserved |
| WebView2-present | **LIVE PASS** | runtime detected; install continues without needless bootstrap |
| package structure / checksums / denylist | **AUTOMATED PASS** | `scripts/test-package.ps1` + SHA-256 sums |
| ARM64 package construction | **AUTOMATED PASS** | `win-arm64` zip + setup produced; staging architecture validated — **not** runtime |

### Phase 11 / pre-v1 deferred validation (NOT EXERCISED)

| Scenario | Result | Notes |
|---|---|---|
| A. Clean x64 pristine VM | **NOT EXERCISED** — deferred Phase 11 | no SDK/repo/dev tools; preferably no .NET 10 Desktop; installer + portable + first launch + upgrade + uninstall |
| B. ARM64 Windows runtime | **NOT EXERCISED** — deferred Phase 11 | install/launch/WebView2/tray/flyout/single-instance/uninstall — or remove ARM64 from public v1 support |
| C. WebView2-missing bootstrap live | **NOT EXERCISED** — deferred Phase 11 | genuine Evergreen absence only |
| D. Reboot / sign-in auto-start | **NOT EXERCISED** — deferred Phase 11 | `--startup` → tray only, no flyout |
| E. SmartScreen / Mark-of-the-Web | **NOT EXERCISED** — deferred Phase 11 | public-download path |
| Light OS theme headed (installed) | **NOT EXERCISED** — deferred Phase 11 | dark host proven in 10C |

### Packaging defects fixed in 10C

1. Orphan HKCU Run on modify without startup — `deletevalue` then task-gated write
2. Stale Vite assets across upgrade — `[InstallDelete] {app}\wwwroot`
3. Relative `-OutputDirectory` broke ISCC — absolutize paths in `package.ps1`


## Phase 12.1 — Discovery health diagnostics (2026-10-01, decision 0008)

**Platform:** Windows 11 `10.0.26100` · platform-tools 37.0.1 · the host with documented broken
ADB mDNS browse (`research/2026-09-19-host-mdns-empty-while-tcp-works.md`)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Trigger fires: Wi-Fi device connected + zero services + healthy server | **AUTOMATED PASS** | `DiscoveryHealthReducerTests` truth table (incl. USB-only, offline, mdns-disabled, tracker-failed, pre-first-frame negatives) |
| Evidence rendering: probe measured / nothing / unavailable; port holders singular+plural; virtual adapters 1–2 named | **AUTOMATED PASS** | `DiscoveryHealthReducerTests`; Vocabulary order probe → port → adapters |
| Card clears on any service return / device loss / mdns disable | **AUTOMATED PASS** | Momentary evidence dropped with the card (10 s legacy-response TTL) |
| `discovery-health` fixture parse + rendering | **AUTOMATED PASS** | `fixtureParity.test.ts`, Vitest, Playwright visual captures (dark + light) |
| 5353 binder list matches `netstat -ano` | **NOT EXERCISED** | Live comparison pending on the affected host |
| Adapter virtual flags match `Get-NetAdapter` | **NOT EXERCISED** | Live comparison pending |
| Probe measures announcements while `adb mdns services` is empty (the acceptance) | **NOT EXERCISED** | Requires the affected host's Wi-Fi environment with a phone advertising; next live session |
| `Check again` headed flow | **NOT EXERCISED** | Headed run pending |

## Phase 12.2 — Discovery repair actions (2026-10-01, decision 0008 amendment)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Restart ADB: kill spawn → supervisor recovery → trackers reconnect → post-restart measure | **AUTOMATED PASS** | `RepairCoordinatorTests` (fixed argv `kill-server`, one-shot measure on first frame after new-generation Up; survives card-clear churn); supervisor/lifecycle recovery pre-proven (ScenarioG, Mut1) |
| Stop Tailscale: fixed `sc.exe stop Tailscale` via UAC runas; undo unlocks; re-measure | **AUTOMATED PASS** | `RepairCoordinatorTests.StopServiceBuildsFixedScCommandAndUnlocksUndo` |
| Disable adapter: fixed `netsh interface set interface name=<validated> admin=disable`; undo entry kept while adapter absent from Up-enumeration | **AUTOMATED PASS** | `RepairCoordinatorTests.DisableAdapterBuildsFixedNetshCommand`; charset rejection (`ValidTargetAcceptsDocumentedNamesAndRejectsInjection`) |
| UAC decline → logged no-op, "not run" line, no undo/no re-measure | **AUTOMATED PASS** | `DeclinedLaunchReportsDeclinedWithoutUndoOrMeasure`; reducer `DeclinedRepairShowsMomentaryLineWithoutMeasurement` |
| Result line momentary (cleared by next measurement); card clear drops repair state | **AUTOMATED PASS** | `DiscoveryHealthReducerTests` repair block |
| Episode balloon: once per episode, cancel-on-clear during hold, latch reset on card clear, card-identical copy, master switch | **AUTOMATED PASS** | `DiscoveryBrokenNotificationTests` |
| Boundary carve-out: `kill-server` only in AdbProcessRunner.cs | **AUTOMATED PASS** | BoundaryTests updated per the amendment; FakeAdbServer socket-kill assertion untouched and still strict |
| Real `Restart ADB` on this host (headed) | **NOT EXERCISED** | Next live session; Phase 6D evidence says restart alone did NOT restore browse — expect "Still not working", which is honest truth, not a defect |
| Real UAC stop-Tailscale + re-enable (headed) | **NOT EXERCISED** | Next live session (needs elevation) |
| Real adapter disable/enable (Hyper-V Default Switch) | **NOT EXERCISED** | The one documented lever never tried on this host — now testable in-product |

## Phase 12.3 — First-party DNS-SD fallback browser (2026-10-01, decision 0008 follow-through)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Codec parses PTR/SRV/TXT/A with compression, TTL, and TXT first-key-wins | **AUTOMATED PASS** | `MdnsFallbackBrowserTests` + `DnsTestMessages` fixtures |
| PTR→instance, SRV→port+hostname, A→address via hostname, TXT→`name`/`api` | **AUTOMATED PASS** | `AbsorbBuildsInstanceFromPtrSrvTxtA` (AOSP mdns.cpp TXT schema) |
| TTL-0 goodbye evicts; re-announcement recreates; TTL sweep drops stale | **AUTOMATED PASS** | `GoodbyeWithZeroTtlEvictsInstance`, `ExpirySweepDropsStaleInstances` |
| Merge: adb non-empty wins entirely; adb empty + fallback rows → offers | **AUTOMATED PASS** | `DiscoveryHealthReducerTests` merge truth table (offer ids are DiscoveryKeys) |
| Fallback rows never satisfy the broken-discovery trigger nor clear tracker warnings | **AUTOMATED PASS** | `FallbackRowsDoNotSatisfyBrokenDiscoveryTrigger`, `FallbackDoesNotClearTrackerFailureWarnings` |
| Shared 5353 bind → passive announcements; bind failure → active-only mode | **NOT EXERCISED** | Headed run on the affected host pending; active-only path is the firewall-safe default |
| **Acceptance: browser sees ADB services while `adb mdns services` is empty** | **NOT EXERCISED** | The Step 3 acceptance on this host — next live session with the phone advertising |
| Windows Firewall first-listen prompt for UDP 5353 | **NOT EXERCISED** | One-time; declining = active-only (no rule needed) |

## Phase 12.4 — Post-pair active connect (2026-10-01, decision 0009)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| `host:connect` framing: OKAY + hex-length + body on success AND failure; body-prefix classification (`connected to` / `already connected`) | **AUTOMATED PASS** | `HostConnectClientTests` (AOSP `connect_service` always `SendProtocolString` — no FAIL branch) |
| Unknown host service → Unsupported (wait continues, no retry) | **AUTOMATED PASS** | `UnknownHostServiceClassifiesUnsupported` |
| Coordinator: fires once when the paired device's Connect row matches the pairing host; continuous row = no retry; vanished→re-presented row = one re-attempt; phase-gated; fallback rows used when adb source empty | **AUTOMATED PASS** | `PostPairConnectCoordinatorTests` |
| No state fabrication: connect success never creates a DeviceView; tracker arrival / 60 s quiet clear unchanged | **AUTOMATED PASS** | Reducer untouched; coordinator posts nothing into State |
| Real pair → active connect → `connected to` logged → tracker arrival < 60 s | **NOT EXERCISED** | Next live pairing on the affected host; supersedes the "ADB auto-connect after pair (no manual connect)" gap |

## Phase 12 — Double-transport collapse (2026-10-01, decision 0011)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Two adb transports (`ip:portA`+`ip:portB`), fallback rows carry the connect serial → ONE card `wifi:<serial>` | **AUTOMATED PASS** | `DoubleTransportIdentityTests` (broken-browse variant + adb-source variant) |
| No discovery rows → two cards remain (honest per-port degradation, pre-0011 behavior) | **AUTOMATED PASS** | `WithoutAnyDiscoveryRowsTwoTransportsRemainTwoCards` |
| USB + Wi-Fi same serial → still two cards (pinned §6.7) | **AUTOMATED PASS** | `UsbAndWifiSameSerialRemainTwoCards` |
| Post-pair connect skipped when the phone is already tracked over Wi-Fi (any port) | **AUTOMATED PASS** | `PostPairConnectCoordinatorTests` guard cases (other host / non-Wi-Fi unaffected) |
| Live: real stale+fresh transport pair collapses to one card after update | **NOT EXERCISED** | Next live session: `adb devices -l` shows the two transports; UI shows one; stale entry dies without UI change |

### Double-transport collapse amendment (2026-10-02, decision 0011 amendment)

| Scenario | Result | Notes |
|---|---|---|
| Serial extracted from AOSP instance name (`adb-<serial>-<6 alphanum>`, legacy `adb-<serial>`) | **AUTOMATED PASS** | `MdnsFallbackMapperTests` + `DeviceIdentity.ExtractSerialFromInstanceName` |
| Stale port (no advertisement) collapses via host-only match on the current advertisement | **AUTOMATED PASS** | `StaleTransportCollapsesViaCurrentAdvertisementOnly` — the live-defect shape |
| Two distinct phones (distinct IPs) remain two cards | **AUTOMATED PASS** | Over-merge guard |
| Live: real stale+fresh transport pair collapses to one card after update | **NOT EXERCISED** | Live session; requires app restart to pick up |

## Flyout presentation animation (2026-10-02, decision 0013)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Animator: easing frames monotone to target, retarget composes completions (light-dismiss arming never lost), cancel stops, complete-snaps fires once | **AUTOMATED PASS** | `FlyoutRevealAnimatorTests` (fake tick source + fake clock) |
| Easing = Fluent Point-to-Point endpoints/monotonicity | **AUTOMATED PASS** | `EasingTests` |
| Live: growth animates when devices connect / card expands; open slides out; close slides into tray on light-dismiss/Esc/toggle | **NOT EXERCISED** | Owner feel-validation; rollback = revert to 0012 instant steps |
| Reduced motion (OS client-area animation off) reduces all animation to instant | **NOT EXERCISED** | `SystemTheme.ReadClientAreaAnimation` wired through ShellAppearance |
| WM_DPICHANGED mid-animation | **NOT EXERCISED** | Cancels animation, adopts OS bounds |

## Pairing wait survival (2026-10-02, decision 0014)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Withdrawal while PairedWaitingForDevice → card/host/wait preserved | **AUTOMATED PASS** | `PairingWithdrawalTests` |
| Withdrawal during in-flight Pairing → card/host preserved (attempt-3 shape) | **AUTOMATED PASS** | In-flight result lands; coordinator survives; self-cleans after result |
| Withdrawal while AwaitingCode → still cancels | **AUTOMATED PASS** | Pre-success behavior unchanged |
| Withdrawal → connect row appears → ActivePairingHost + Connect row valid for post-pair connect | **AUTOMATED PASS** | `WithdrawalWhileWaiting_ThenConnectRowAppears…` |
| Live: QR pair on the A06 → `Post-pair connect: connecting to 192.168.0.53:45533` → card appears | **NOT EXERCISED** | The fix's live acceptance; the phone is still advertising this endpoint |
