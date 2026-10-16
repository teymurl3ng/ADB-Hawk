# Device test matrix

## Multicast-host discovery (2026-10-12, decision 0026)

| Scenario | Result | Notes |
|---|---|---|
| Harness A: default-route ephemeral query, Wireless debugging on | **LIVE PASS (connect replies)** | Owner report `artifacts/probe-mdns-20261012-113318-807.md`: 4 queries, 2 relevant datagrams, two connect instances on Wi-Fi, no errors; no pairing replies |
| Harness B: per-interface ephemeral query | **LIVE PASS (connect replies)** | Same report: 8 queries across Hyper-V/Wi-Fi addresses, 2 relevant datagrams on Wi-Fi, the same two connect instances, no errors; necessity of explicit egress is not established by this run |
| Harness C: shared 5353 passive listener | **EXERCISED — 0 ADB replies** | Same report: 15 s, 8 datagrams, 0 relevant, no errors; no queries sent; does not distinguish blocked announcements from absence of announcements during this window |
| QR scanned 10–40 s after start, including passive bind unavailable | **NOT EXERCISED** | Observe fallback pairing offer and successful handoff on owner host |
| QR cancel/expiry/handoff stops 2 s queries; background floor stays 60 s | **NOT EXERCISED** | Owner: inspect Debug query evidence and test a fresh attempt after expiry |
| QR server-generation change ends fast window | **NOT EXERCISED** | Observe an externally caused server change; this task never restarts it |
| Per-interface egress with VPN/Hyper-V and contended 5353 | **NOT EXERCISED** | Information summaries record changed interface sets/send counts; success is not inferred from sends |
| Roam/VPN toggle/address change and resume replace passive memberships | **NOT EXERCISED** | Owner: compare join/query evidence after 1 s event debounce; no adapter changes by the app |
| Valid compressed PTR/SRV replies on the owner's phone produce fallback endpoints | **NOT EXERCISED** | Separately authorized parser fix; synthetic full-message compression is covered by regression tests, not live evidence |
| No A/B/C replies, or A/B empty and C relevant | **CONDITION NOT MET** | Owner report: A and B both received connect replies; C received no ADB replies. Task 4 remains unimplemented |


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

## Native shell theme, pin round-trip, and QR discovery burst (2026-10-03)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Dark mode: state pill text light-on-dark; theme dictionaries swap with log proof | **LIVE PASS** | Window capture at 125% DPI; logs `Window theme dictionary swapped to "Dark"`, `App theme dictionary swapped`, `Native shell theme applied` |
| Light mode: readable tokens, corrected semantic alphas (8 fixes vs app.css:161-177), themed Button/ScrollBar chrome | **LIVE PASS** | Window capture; no Aero2 light-gradient highlights remain |
| Startup identity log (version + resolved theme) — stale-binary detector | **LIVE PASS** | `AppHost identity: version=0.1.0.0 native=true theme="Dark" transparency=false` |
| Theme applied before first show (dictionary re-sync in OnSourceInitialized) | **LIVE PASS** | `Window theme dictionary swapped` precedes the open slide in the log |
| Pin click round-trips through the reducer (icon and window agree) | **AUTOMATED PASS** | `SnapshotPinStateUpdatesTheViewModel`; live click requires the new binary (prior sessions ran pre-fix builds) |
| QR session start triggers an immediate fallback query burst | **AUTOMATED PASS** | `MdnsFallbackTriggerTests` — frozen clock proves the burst comes from the trigger, not the 60 s requery floor |
| Fallback rows fill only the discovery keys adb does not report (decision 0016) | **AUTOMATED PASS** | `MdnsEffectiveSetTests`; `DiscoveryHealthReducerTests` per-key win + no-duplicate cases |
| Native offer-phase pairing card renders and raises pairStart (web parity) | **AUTOMATED PASS** | `NativeShellViewModelTests` offer cases |
| Live: QR pair on a network where adb mDNS works | **NOT EXERCISED** | Blocked by environment 2026-10-03: `adb mdns services` empty; Chrome ×2 + svchost hold UDP 5353; Tailscale up. In-app health card reports the same. |
| Live: synthetic click automation against the flyout | **NOT EXERCISED (harness)** | The unpinned flyout is non-topmost and light-dismisses on focus loss, so synthetic input is unreliable; owner manual test required. Root cause of the earlier misleading test: PowerShell reports virtualized coordinates — 1151 × 1.25 = 1439 matches the log's physical X. |

## Pairing slot release + QR presentation (2026-10-03, decision 0017)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Parked pairing slot released once the pairing wait completes (device appeared / wait expired) | **AUTOMATED PASS** | `FulfilledWaitIsReleased_SoTheNextPairIsAccepted` — the live leak |
| A genuine in-flight operation is undisturbed by reconciliation | **AUTOMATED PASS** | `ReconcileLeavesAGenuineInFlightOperationAlone` |
| A parked Failed operation for another offer does not block a new pair | **AUTOMATED PASS** | `NewPairStartPreemptsAFailedOperationForAnotherOffer` (QR mints a fresh instance each time) |
| Cancel clears a parked operation even when the clicked offer id differs | **AUTOMATED PASS** | `CancelClearsAParkedOperationEvenWhenTheClickedIdDiffers` |
| Refused QR handoff keeps the QR session, submits nothing, allows a later rescan | **AUTOMATED PASS** | `RejectedHandoffKeepsTheQrSessionForARescan` |
| QR-origin card hides the typed-code slots/title/Pair and offers "Scan again" on failure; typed cards keep Pair/Retry | **AUTOMATED PASS** | 4 cases in `NativeShellViewModelTests` |
| Live: code-pair, then QR-pair in the SAME session without restarting | **NOT EXERCISED** | The exact failure mode (log 2026-10-03 15:13:55 → 15:14:16); requires the owner's phone on a network where mDNS works |

## Native shell chrome + notification timing (2026-10-03, decisions 0018/0019)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Hover on the "Pair a device over Wi-Fi" row fills the whole card with no interior hairline | **NOT EXERCISED** | Needs the owner's eyes; the cause was the inset hover fill inside the card (not a border) |
| Repair chips ("Restart ADB", "Stop Tailscale service", "Check again") use the themed button template | **NOT EXERCISED** | They previously fell back to raw Aero2 chrome; visual check pending |
| Header inset 16 px, pin centred in the 40 px header, pin hit area 36×36 | **NOT EXERCISED** | Visual check pending |
| Connection toast appears ≈0.5 s after the transition (was 2.5 s) | **AUTOMATED PASS** | Timing tests retargeted to `NotificationPolicy.HoldConnected`; `C_D_*`, `E_*` (flap before the hold), `F_PostBaseline_*` |
| Tracker-failure grace still holds 2 s (server restart must not flash cards) | **AUTOMATED PASS** | `X_Y_Z_AA_GraceRetentionAndStaleExpiry`, `Row5_Tracker_Grace_Retains_Then_Clears` |
| No toast while the flyout is visible | **AUTOMATED PASS** | `N_SuppressedWhileTheFlyoutIsVisible` |
| A suppressed notification consumes no episode latch or burst slot (the next transition still notifies) | **AUTOMATED PASS** | `O_SuppressionConsumesNoBookkeepingSoTheNextTransitionStillEmits` |
| Live: unplug the phone while the flyout is open → no toast; close it and unplug → toast in ~0.5 s | **NOT EXERCISED** | Owner's device |

## Status bubble (2026-10-04, decision 0020 — phases N1/N2)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Contract amendment applied (DESIGN §4/§30/§33 + regenerated authority hash) | **AUTOMATED PASS** | `DesignAuthorityHashMatches`; menu arrays updated in three places |
| `StatusBubbleEnabled` preference defaults OFF and round-trips | **AUTOMATED PASS** | `StatusBubble_Defaults_Off_And_RoundTrips` |
| Tray menu exposes the checkable "Status bubble" item | **AUTOMATED PASS** | `TrayContextMenuTests`, `BalloonNotificationSinkTests` |
| Aggregate → colour/lifetime mapping (green/amber/blue permanent; red/blue transient; idle hidden) | **AUTOMATED PASS** | `StatusBubblePresenterTests` (11 cases) |
| Transient states arm ONE 60 s expiry, retarget on a new state, are cancelled by a permanent one | **AUTOMATED PASS** | Same suite — includes a retargeting case and a "superseded wait must not hide" case |
| "Just disconnected" episode shows red using the last connected detail | **AUTOMATED PASS** | The aggregate collapses to idle, so this state exists only in the presenter |
| Window is created only when the preference is on; disposed on toggle-off and shutdown | **AUTOMATED PARTIAL** | Live: created with the preference on; the off path is code-verified only |
| Window never activates: `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST` all present | **LIVE PASS** | Probed via `GetWindowLong(GWL_EXSTYLE)` on the running bubble |
| Tiny size: exactly 22x22 DIP (frameless; a captioned window floors at 133x38) | **LIVE PASS** | Measured `GetWindowRect`; see research note addendum |
| Position: bottom-right of the work area, 12 DIP margin, above the taskbar | **LIVE PASS** | Measured against the taskbar screen's working area at 125 % DPI |
| Bubble colour follows the aggregate (green while connected) | **NOT EXERCISED** | Needs a visual look; the window is up but its fill was not eyeballed |
| DWM corner rounding on the frameless bubble | **NOT EXERCISED** | "If appropriate" per docs; frameless may skip it — `SetWindowRgn` is the fallback; needs a look |
| Hover shows device detail; click opens the flyout (phase N3) | **NOT IMPLEMENTED** | Next phase |
| Bubble hides over a full-screen app / lock screen (phase N3) | **NOT IMPLEMENTED** | Next phase |
| Live: unplug the phone → red for 60 s → bubble disappears | **NOT EXERCISED** | Owner's device; the presenter behaviour is unit-tested |

## Status bubble — hover/click phase (2026-10-04, decision 0020 phase N3)

| Scenario | Result | Notes |
|---|---|---|
| Rounded corners confirmed by the owner (DWM rounding works on the frameless bubble) | **LIVE PASS** | Owner: "it's a rounded square" — `SetWindowRgn` fallback not needed |
| Bubble colour follows the aggregate | **LIVE PASS** | Window capture pixel-sampled `#4ADE80` — the dark-theme Green token, while a phone was connected |
| Bubble hides while the flyout is visible | **IMPLEMENTED** | Found live: the bubble sits INSIDE the flyout's rectangle, so the open flyout intercepted the mouse. Hiding it while the flyout shows is the same reasoning as decision 0019's toast suppression |
| Hover reveals the status text (window grows left, bottom-right corner anchored) | **IMPLEMENTED, UNVERIFIED** | No mouse event reached the window in testing, even with the flyout dismissed — see the note below |
| Click opens the flyout | **IMPLEMENTED, UNVERIFIED** | Wired to the same path as the tray's "Open ADB View" |
| Hide over a full-screen app / lock screen | **NOT IMPLEMENTED** | Planned via `SHQueryUserNotificationState` |
| Live: unplug the phone → red for 60 s → bubble disappears | **NOT EXERCISED** | Presenter behaviour is unit-tested; the physical unplug is the owner's |

**Open question (hover):** a synthetic `SetCursorPos` over the bubble produced no `MouseEnter` (confirmed
by the temporary `StatusBubble: SetHovered(...)` log line, which stayed empty) even after dismissing the
flyout. Synthetic hover demonstrably works on the flyout itself (verified earlier by a pixel diff), so the
difference is the bubble's non-activating window. The next step is to read that log line after a REAL
mouse hover, which distinguishes "synthetic input does not reach a `WS_EX_NOACTIVATE` window" from "the
window never receives mouse input at all"; if it is the latter, hover moves to the HWND (`WM_MOUSEMOVE` +
`TrackMouseEvent`) instead of WPF's input pipeline.

## Motion system + native self-sizing (2026-10-04, decision 0021)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Native flyout self-sizes with content (376×500 → 376×306 with 2 device cards), bottom edge frozen at 804 DIP | **LIVE PASS** | Measured via GetWindowRect at 125% DPI; first implementation no-opped (dedup against the Height property instead of ActualHeight) |
| Self-sizing math: clamp [160,560], frozen bottom, work-area handling, step threshold | **AUTOMATED PASS** | `ShellSizingTests` (11) |
| Reduced-motion gate follows the OS client-area-animation setting | **AUTOMATED PASS** | `ShellMotionGateTests` (8); the audit found the gate was inert (hardcoded event value) |
| Reduced motion zeroes the XAML motion tokens live (hover/chevron/fades go instant) | **NOT EXERCISED** | The override is wired (`ApplyMotionTokenGate`); needs the OS setting toggled both ways with the owner's eyes |
| Hover/press 83 ms opacity layers (buttons + QR row) | **NOT EXERCISED** | Owner visual check |
| Chevron rotates 167 ms; details fade in 167 ms; height stays instant | **NOT EXERCISED** | Owner visual check; height-instant is by contract (0012/0013) |
| Pairing success/progress fade-in; Copied badge fades in 83 ms | **NOT EXERCISED** | Owner visual check |
| Copied badge auto-clears after 1200 ms; a second copy supersedes; clipboard failure shows no badge | **AUTOMATED PASS** | 3 badge tests |
| Status bubble content fades in 83 ms on appear; hover growth (content-sized window) | **NOT EXERCISED** | Hover input proven working by the owner (SetHovered log); growth awaits re-test |
| Dead code removed (TickMs, SlideTarget intoTray variant, subscriber-less event) | **AUTOMATED PASS** | Build + gate |
| Live: device connect/disconnect resize with the OS animation setting OFF (everything instant) | **NOT EXERCISED** | Owner |

## WebView removal follow-up: first-show placement + discovery evidence threading (2026-10-05)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| First show opens at the tray, sliding in from the taskbar side (never from the OS cascade position) | **LIVE PASS** | Defect: the open-origin `SetPhysicalBounds` ran before `Show()`, where the HWND does not exist yet, so it silently no-opped and WPF placed the window at the WM cascade position (physical X=32–192) — the reveal slid the flyout across the whole screen ("old UI appears from the dead on the left", owner report). Log before: `bounds animation started {X=64,Y=375,...}->{X=1439,...}`; after the fix (placement applied after `Show`, using the self-sized height): `{X=1920,Y=691,...}->{X=1439,Y=691,...}` |
| The 167 ms open slide completes in ~167–400 ms on first show | **LIVE PASS** | Defect: `DiscoveryDiagnostics.Collect` (GetExtendedUdpTable sweeps + process-name resolution) ran on the UI thread via `RunDiscoveryCheckAsync`'s synchronous prefix, starving the animator's dispatcher ticks — the reveal took 3.1 s. The sweep now hops to the worker pool inside the check, covering all three callers (flyout open, health-card check-again, repair flow). Log after: started 22:53:08.349, completed 22:53:08.713 (364 ms, includes two concurrent self-size steps) |
| Open-slide origin/final heights use the measured content height (no post-open seed snap) | **LIVE PASS** | Before: origin/target carried the 500 DIP seed and self-sizing snapped 500→251 after the slide; now both are 251 DIP from the first layout pass |
| Metadata probes never run on the UI thread | **AUTOMATED PASS** | `DeviceMetadataLifecycle` probes via `Task.Run` (existing tests); the 2 s `marketname` stall seen in the log is a device-side shell latency on a worker thread, correctly off-screen |
| Owner re-check: open feels instant, no stray window at the left edge | **NOT EXERCISED** | Owner |

## Animation jitter fixes: single-writer bounds, composition-clock slide, frozen curve in XAML (2026-10-08, decision 0023)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| The reveal is a position-only translation: width/height identical in every applied frame | **AUTOMATED PASS** | `SlideIsPositionOnly_SizeNeverChangesMidFlight`, `SlideEndpointsDifferingInSizeAdoptTheTargetSize` (stale-origin size mismatch is forced to the target size and logged) |
| A frame identical to the last applied one is not re-issued (the composition callback can fire more than once per composed frame) | **AUTOMATED PASS** | `IdenticalFramesAreNotReapplied` — includes the start itself: the progress-0 frame is skipped because the caller has already placed the window on `from` |
| Every XAML storyboard animation carries the frozen curve `cubic-bezier(0.55,0.55,0,1)` | **AUTOMATED PASS** | `MotionCurveTests` — Controls.xaml and DeviceCardView.xaml: every `DoubleAnimation` declares an easing function; PairingCardView.xaml allows exactly one (the documented spinner); no `CubicEase`/`QuadraticEase`/`KeySpline` anywhere |
| XAML curve equals the code animator curve | **AUTOMATED PASS** | `BezierEaseTests` — `BezierEase.Ease(t)` ≡ `Easing.FluentPointToPoint(t)` over a 41-point grid (the composed `EasingMode.EaseIn` path, not just `EaseInCore`) |
| Reduced-motion twin: key parity, no animations, instant setters for hover/press | **AUTOMATED PASS** | `ReducedMotionDictionaryTests` (3) — the empty triggers were replaced with real instant setters (an empty trigger is no behaviour, not instant behaviour) |
| Self-sizing does not step while the reveal owns the window rect | **LIVE PASS** | Launch run 2026-10-08: the self-size step (500→251 DIP) lands *before* `bounds animation started`, and no `SelfSizedHeight: stepping` line appears between any slide's `started` and `completed` (three slides observed) |
| Open/close slides and expand/collapse read smooth at 100 % and 200 % scaling | **NOT EXERCISED** | Owner's eyes; the pre-fix complaint ("jittery slide, jittery details") is the acceptance |
| No black flash at the exposed strip during the slide or a height step | **NOT EXERCISED** | Background now seeds from the themed opaque brush (was `Brushes.Black`); needs the owner's eyes on a slow frame |
| Tick-quality evidence: the flight completes on time with frame accounting in the log | **LIVE PASS** | Same machine, ~3 min apart — OLD binary: 167 ms open flight took **2035 ms** wall (started 14:49:16.298, completed 18.333). NEW binary: open **183 ms** (8 frames), close **102 ms** for the 83 ms flight (4 frames), toggle-reopen **180 ms** (7 frames); worst tick gap 31–47 ms at 125 % DPI. Endpoints differ in X only (470×314 constant) — position-only holds live |
| The pairing spinner still runs linear 850 ms and ignores reduced motion | **FIXED (decision 0024)** | Now gated by the snapshot's reduced-motion flag via the view model (`SpinnerEnabled`); the 850 ms period stays the documented non-token exception — see the 2026-10-10 section below |

## Animation follow-up: reveal retargeting, position-before-show, appear-only fades, spinner gate (2026-10-10, decision 0024)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Open-reveal plan: visible window retargets from current bounds; hidden window reveals from the off-tray origin; reduced motion places at final with no slide | **AUTOMATED PASS** | `OpenRevealPlanTests` (4) — the visible/hidden × animated/reduced matrix |
| Rapid toggle (open → close → open within the 83 ms close slide) shows no teleport off-screen | **NOT EXERCISED** | Owner: toggle fast from the tray; the window must slide back out from where it was, never vanish behind the tray first |
| Display/DPI change while the flyout is open re-places it without vanishing | **NOT EXERCISED** | Owner: change resolution/scale with the flyout open; retarget slide or instant snap, no disappear/reappear |
| First show never appears at the OS cascade position (pre-seeded DIP geometry) | **NOT EXERCISED** | Owner: fresh launch; the open slide starts at the tray edge, not the top-left |
| Bubble fade is appear-only: a colour/detail change while visible does not blink | **AUTOMATED PASS** | `StatusBubblePolicyTests` (3) — `ShouldFadeIn(wasVisible, animationsEnabled)`; the window branch is thin over it |
| Bubble fade carries the frozen curve and honours the reduced-motion gate | **NOT EXERCISED** | `BezierEase` on the code-behind `DoubleAnimation`; the gate reaches the bubble as `AnimationsEnabled` — needs the OS setting toggled both ways with the owner's eyes |
| Pairing spinner is static under OS reduced motion; runs (linear 850 ms) otherwise | **AUTOMATED PASS** | `NativeShellViewModelTests` (4): gate off at snapshot, offer card, flag follows the gate across snapshots on the same card instance |
| Pairing spinner static under reduced motion — live | **NOT EXERCISED** | Owner: start a pairing with the OS animation setting OFF; the dashed ring must not rotate |
| `RowCardButton` hover overlay starts at Opacity 0 in both dictionary twins; reduced twin raises it instantly | **AUTOMATED PASS** | `ReducedMotionDictionaryTests.RowCardOverlayStartsHiddenInBothVariants` |
| First hover on the QR entry row fades in (no pop) | **NOT EXERCISED** | Owner visual check; subsequent hovers already faded |
| Reopen after a reduced-motion hide shows no flash at the previous on-screen position | **NOT EXERCISED** | Owner: OS animations OFF, open, close, reopen — the window must appear at the tray, not at its old spot first |

## Slide clock + build identity (2026-10-10, decision 0025)

Legend: **LIVE PASS** / **AUTOMATED PASS** / **NOT EXERCISED**

| Scenario | Result | Notes |
|---|---|---|
| Owner report "not fixed" traced to a stale binary: the 15:02 instance ran the installed exe last written 2026-10-06 10:11 (pre-0023/0024); its log session shows pre-0023 slide lines (no tick accounting) | **LIVE PASS** | Process path + file timestamp vs commit c433be7 (2026-10-10 12:30); the identity log line could not have distinguished builds (constant version) — see the next row |
| Startup identity line carries the executable path and build (last-write) timestamp | **AUTOMATED PASS** | `AppHostIdentityTests` (2): `AppHost.BuildIdentity()` carries `version=`/`path=`/`built=`, stamp is a parseable `yyyy-MM-dd HH:mm` or explicit `unknown` |
| Slide frame pacing on the composition clock is irregular (the measured defect) | **LIVE PASS** | 2026-10-10 log, build c433be7: open 6 frames/47 ms worst gap (first show), open 7/32 ms (quiet), open 10/16 ms, close 5/16 ms, close 4/32 ms — 4-10 frames per flight depending on incidental renders |
| Slide frame pacing on the dispatcher clock is even, including quiet open slides | **LIVE PASS** | Re-measured after the clock switch, 2026-10-10 16:47 session (log `adbview-20261010.log`) |
| A 167 ms open flight applies 10 frames with a 16 ms worst tick gap, on every flight | **LIVE PASS** | 16:47 session: 10/16 ms (first show), 10/16 ms, 10/16 ms — quiet opens previously starved at 6-7 frames / 32-47 ms gaps |
| An 83 ms close flight applies 4-5 frames with a 16-31 ms worst tick gap | **LIVE PASS** | 16:47 session: 4/31 ms, 5/16 ms, 5/16 ms — closes run while visible, with metadata-driven UI work ahead of the tick at Render priority |
| Timer interval quantization measured: 16 ms interval locks into a 31 ms period; 15 ms slips to 16-32 ms; 10 ms (below the 15.6 ms system tick) fires every tick | **LIVE PASS** | 16:40 / 16:43 / 16:47 sessions — the dispatcher's wait is tick-grid quantized; the interval must sit below the tick |
| Rapid toggle (open during a close slide) retargets from the mid-flight position — no snap to the off-tray origin | **NOT EXERCISED** | Owner: toggle fast from the tray; the log's `bounds animation started {From}` must show a mid-flight From, not the origin X=1920 |
| Owner runs a current build: installed copy at `AppData\Local\Programs\ADBView` repackaged and reinstalled (it was from 2026-10-06) | **LIVE PASS** | 2026-10-10: `scripts/package.ps1 -Rid win-x64` (verify gate green inside the packaging; self-contained single-file + ZIP + Inno installer + manifest + SHA256SUMS in `artifacts\release\0.1.0`), installer run `/VERYSILENT` over the 06/10 install (same frozen AppId, per-user `PrivilegesRequired=lowest`). Installed exe last-write 2026-10-10 19:16; identity log line verified live: `path=...AppData\Local\Programs\ADBView\AdbView.exe built=2026-10-10 19:16`; slide pacing verified on the installed binary (opens 7-8 frames / 31-32 ms under first-paint + metadata load, closes 5 frames / 16 ms) |
| Installer artifacts integrity | **AUTOMATED PASS** | `ADBView-0.1.0-SHA256SUMS.txt` + `ADBView-0.1.0-manifest.json` (gitCommit, builtAtUtc, per-artifact SHA-256) in `artifacts\release\0.1.0`; sums: zip `78380a6e…`, setup `5cd852eb…`, manifest `a3332931…` |
| If the evenly-paced slide still reads jerky, the remaining cause is the shadowed-HWND slide (Microsoft's AnimateWindow warning) — rollback is instant open/close (decision 0013), an owner-facing DESIGN §24 decision | **NOT EXERCISED** | Owner perception call after testing a current build |
# 2026-10-15 connection reliability repair acceptance

The following rows supplement existing acceptance; fake-server results do not establish live-device behavior.

| Scenario | Status | Evidence / remaining work |
|---|---|---|
| Mixed ADB/fallback discovery with spare builder capacity, then a device disconnect | AUTOMATED PASS | MdnsEffectiveSetTests and StateEngineTests; state loop continues and removes the device |
| Server unavailable before tracker failure; repeated failures do not prolong grace; stale outage expiry cannot remove a new session | AUTOMATED PASS | ServerLossArmsGraceOnceAndNewOutageGetsFreshToken |
| Device event after UTC rollback and a stale sequence afterward | AUTOMATED PASS | ProducerSequenceAcceptsDisconnectAfterClockRollbackAndRejectsStaleSequence |
| Offline duplicate followed by working transport; fallback hardware identity used by metadata | AUTOMATED PASS | FallbackIdentityAndWorkingTransportAreSharedWithMetadataTargets |
| Late connect-service advertisement received through FakeAdbServer without a visible snapshot change | AUTOMATED PASS | FakeDiscoveryStreamTriggersConnectionAfterPairingWithoutVisibleDelta; no fabricated connected device |
| Multiple endpoint attempts use their actual ports and advance after failure | AUTOMATED PASS | TriesActualUnattemptedPortsAndAdvancesAfterFailure |
| Pairing client reports cancellation when the coordinator deadline expires; unexpected launch/client failure then retry | AUTOMATED PASS | PairingCoordinatorTests; timeout failure and immediate retry |
| Empty CLI discovery document while its pipe remains open; bounded diagnostic retention drains all bytes | AUTOMATED PASS | MdnsTrackerTests |
| Shared-host and reversed/split DNS records, independent TTL expiry, missing-record resolution, short-TTL refresh, address withdrawal/cache flush | AUTOMATED PASS | MdnsFallbackBrowserTests and MdnsRecordCache |
| Network rejoin clears endpoints from the old network | AUTOMATED PASS | NetworkRejoinInvalidatesPreviousNetworkEndpoints |
| Recovery executable launch failure; explicit wake interrupts a long supervisor delay; status refresh at unchanged protocol version | AUTOMATED PASS | AdbServerSupervisorTests; no kill command |
| Failed repair exit or exception publishes a terminal failure; successful reversible repair retains undo after card clearing | AUTOMATED PASS | RepairCoordinatorTests; fake commands only |
| Native unavailable/restoring and wireless warnings; current operation retry target; same-operation code preservation | AUTOMATED PASS | NativeShellViewModelTests; four health fixtures added |
| Live: turn wireless debugging off while connected, then on; cards follow actual ADB truth and pairing recovers | NOT EXERCISED | Requires controlled phone interaction |
| Live: phone changes Wi-Fi network or connection port; shared-host discovery and connection recover | NOT EXERCISED | Requires real network/phone observation |
| Live: Windows sleep/resume and lock/unlock with a connected phone and an ongoing outage | NOT EXERCISED | Fake supervisor wake is covered; physical cycle remains pending |
| Live: IPv6-only multicast network and scoped link-local discovery | NOT EXERCISED / LIMITED | Current browser multicast transport remains IPv4; IPv6 records and ADB-provided endpoints are covered |
| Live: explicit repair and subsequent undo after discovery recovers | NOT EXERCISED | No machine service, adapter, or real ADB restart was performed as a test |
| 24-hour pinned/hidden reliability and resource soak | NOT EXERCISED | Phase 11 acceptance remains open |

## 2026-10-16 M06 shell action migration

| Scenario | Status | Evidence / remaining work |
| --- | --- | --- |
| WinUI manual pairing, retry/cancel, Back/hide, offer replacement and waiting without inferred connection | AUTOMATED FIXTURE PASS | M06 actual-XAML pairing driver and BackendShellActionsTests; existing backend/WPF sources unchanged. Live candidate composition remains M08. |
| WinUI QR decode/invalidation, QR-to-manual overlap and stale decode completion | AUTOMATED FIXTURE PASS | Pinned QRCoder output for a fixed noncredential payload; local native SVG decoder. This is not a phone scanning result. |
| WinUI diagnosis, explicit repair and undo command identities | AUTOMATED FIXTURE PASS | Synthetic actions only; no machine service, adapter or ADB repair executed. |
| Real phone code/QR pairing through the integrated WinUI shell, then disconnect while waiting and cancel after Back/hide | NOT EXERCISED | M08 integration prerequisite; M12 real-device regression. Existing real-device/soak acceptance stays open. |
| Real discovery repair/undo and notification opening through integrated WinUI native delivery | NOT EXERCISED | M08 native integration and M12 controlled acceptance; fixture command routing does not pass these rows. |

## 2026-10-16 M07 presentation lifecycle

| Scenario | Status | Evidence / remaining work |
| --- | --- | --- |
| Actual WinUI navigation interruption, snapshot churn, motion disable/re-enable, hide/reopen and suspend boundaries | AUTOMATED FIXTURE PASS | M07 motion-scenarios; real native objects, immediate truth/input, no hidden presentation clocks; no physical suspend or Windows preference was changed. |
| 500 animated navigation cycles with matched native-control warmup, handler disposal and owned animation/subscription cleanup | AUTOMATED PASS | Final M07 gate: process handles 2513→2519; private bytes 141262848→144609280; no forced GC. This is a candidate-only workload, not integrated resource/soak qualification. |
| Real system motion-toggle, physical sleep/resume and hardware presentation qualification in the integrated shell | NOT EXERCISED | Actual UISettings subscription is implemented; injected event-source and lifecycle tests do not pass physical M08/M09/M12 acceptance. |
