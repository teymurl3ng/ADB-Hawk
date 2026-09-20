# Pre-Phase-10 runtime diagnosis (facts only)

- **Date:** 2026-09-20
- **Author:** agent (diagnosis-only; no product fix)
- **Phase:** post-9 / pre-10
- **Status:** current — measurements recorded; no production behaviour changed

## Working tree baseline

| Field | Value |
|---|---|
| `HEAD` | `703b4f38111f1859939d7cca172fb2c0be8767ec` |
| Branch | `main` (ahead 1 of `origin/main`) |
| Dirty | Yes — large uncommitted pre-Phase-10 acceptance / FLIP / Pair / DESIGN motion work |
| `git diff --stat` | 35 files, ~979 insertions / 394 deletions (at diagnosis start) |

**Do not reset. Do not commit.**

## Diagnosis method

| Area | Method |
|---|---|
| Pair-code geometry | Temporary Playwright probes (`ui/tests/diagnosis/*`, `playwright.diagnosis.config.ts`) — **not** in `verify`/`visual` |
| Pair hit-test | Same diagnosis Playwright project |
| Pair submit / WD / offers | Static trace of `PairingSession` / `PairingCoordinator` / `StateReducer` / `NotificationPolicy` against ENGINEERING contracts |
| Acrylic | Temporary `scripts/diag-acrylic-native-only.ps1` (outside `src/AdbView`); environment registry query |
| Motion A/B/C | Code-level proof for production path A; B/C **not built** (would require product diagnostic modes) |
| Live `host:pair` | **Not executed** — `adb mdns services` empty during session (no pairing advertisement) |

Artifacts:

- `artifacts/diagnosis/pair-code-geometry.json`
- `artifacts/diagnosis/pair-code-geometry-near-max.json`
- `artifacts/diagnosis/pair-hit-test.json`
- `artifacts/diagnosis/acrylic-native-only.json`

---

## 1. Pair-code geometry

### OBSERVED USER SYMPTOM

Typing pairing digits changes flyout geometry; scrollbar appears/disappears though “no structural content” was added.

### MEASURED TIMELINE (fixture `pairing-code`, viewport 376×600)

| event | dig | pairH | contentH | btnW | btnDisabled | cursor ring | RO fires | scrollbar |
|---|---:|---:|---:|---:|---|---|---:|---|
| before | 0 | 176.25 | 176 | 46.45 | true | yes | 1 | no |
| after-1 … after-12345 | 1–5 | 176.25 | 176 | 46.45 | true | yes | 0 | no |
| **after-123456** | **6** | **174.25** | **174** | **48.45** | **false** | **no** | **1** | no |
| backspace-to-5 | 5 | 176.25 | 176 | 46.45 | true | yes | 1 | no |
| retype-6 | 6 | 174.25 | 174 | 48.45 | false | no | 1 | no |
| hover / pointerdown / up | 6 | 174.25 | 174 | 48.45 | false | no | 0 | no |

**Deltas:** `max(pairH)-min(pairH) = 2.00`; `max(contentH)-min(contentH) = 2`.

Near-overflow probe (filler + pairing-code, scroll already overflowing):

| event | contentH | scrollScrollH | pairH | btnH | scrollbar |
|---|---:|---:|---:|---:|---|
| d1–d5 | 528 | 536 | 176.25 | 28 | true |
| **d6** | **526** | **534** | **174.25** | **26** | true |
| back5 | 528 | 536 | 176.25 | 28 | true |

### Answers A–J

| Q | Answer |
|---|---|
| A. DOM natural height change? | **Yes** — exactly at digit count 5→6 and 6→5 (±2 px pair card / content). Digits 0–5: stable. |
| B. scrollHeight change? | **Yes** — same events (±2). |
| C. Scrollbar width↔wrap loop? | **Not required for the ±2 px card change.** Card height changes from CSS state swap (below). Near-threshold layouts can still flip overflow when combined with live HWND resize. |
| D. Native HWND resize with stable DOM? | **Not observed in browser probe.** In production, **live** `reportContentSize` follows content ResizeObserver (`useContentSize.ts`); a ±2 content change **will** call `ResizeFlyout(Live)` → `ApplyImmediateResize` (not StructuralSizeAnimator). |
| E. WebView child size? | Production: WebView `Dock = Fill` (`WebViewHost.cs`). Any HWND size change resizes the child. |
| F. ResizeObserver fire? | **Yes** on 5↔6 transitions (measured `roFires: 1`). |
| G. reportContentSize fire? | Probe patched `postMessage` saw empty arrays (fixture harness / timing); **code path** is unambiguous: RO → `scheduleMeasure('live')` → `reportContentSize` when not structural. |
| H. StructuralSizeAnimator? | **No** — typing is not FLIP; phase is live. |
| I. Font/focus/border geometry? | **Yes.** Cursor slot uses `ring-2 ring-win-accent/25` + `text-[14px]`; filled slots `text-[15px] font-semibold`. At 6 digits cursor removed. |
| J. Pair enabled state geometry? | **Yes.** Disabled Pair: `px-3 py-1` + border, ~28px tall / ~46px wide. Enabled Pair: `px-3.5 py-1` fluent primary, ~26px tall / ~48px wide. Different React branch swaps the button. |

### First incorrect event

**Digit count reaches 6 (or drops from 6):** React swaps Pair button + removes cursor ring → pairing-card height changes by **2 CSS px** → content ResizeObserver → **live** HWND resize.

### Exact geometry root cause

Presentation state in `PairingCard.tsx` ties **layout-affecting CSS** to digit count / enabled Pair:

1. Cursor `ring-2` present iff `code.length < 6`
2. Entire Pair control replaced when `isReady` (`code.length === 6`)

This is **not** structural disclosure content, but it **is** a real box-geometry change, and the sizing pipeline treats it as live content height.

---

## 2. Pair offer button (enter AwaitingCode)

### OBSERVED

Offer Pair sometimes felt dead (prior acceptance); now wired.

### Trace (code + hit-test probe)

| Step | Status |
|---|---|
| pointer → Pair host | **PASS** — `elementFromPoint` = `pairing-pair-button` (offer + code fixtures) |
| React `onClick` → `pairStart(offerId)` | **PASS** (wired in `App.tsx` / `PairingCard`) |
| Bridge `pairStart` | **PASS** (`BridgeCoordinator`) |
| `PairingSession.Start` | Can **silently no-op** if binding missing, bad endpoint, or `TryStart`/`TryRetry` both false (`pairStart ignored: …` logs) |
| Snapshot AwaitingCode | Published when coordinator emits |
| UI phase | `derivePairingState` maps `awaitingCode` → `pairing-code` |

### Hit-test result

Stable fixtures: **hit is the Pair button** (`pair-hit-test.json`). `pointer-events: auto`. No drag region on Pair (`app-region-no-drag`).

**Unresolved headed case:** hit-test **during DeviceCard FLIP** with a live pairing offer was not measured in a combined fixture (no dual fixture). Code: FLIP only transforms `[data-device-id]`; PairingCard has no `data-device-id` — interception by FLIP transforms is **unlikely**; residual risk is scroll/clip during HWND animation.

### First broken step (when offer Pair fails)

**`PairingSession.Start` silent ignore** after a valid click — especially `unknown or stale offer` or `pairing operation already active` — with **no user-visible error**.

---

## 3. Pair submit (six-digit → host:pair)

### OBSERVED

Submit unreliable / UI stale while pairing may progress.

### Trace

| Step | Status |
|---|---|
| Click enabled Pair | Hit-test PASS when enabled |
| `onPairSubmit(offerId, code)` | Only if `code.length === 6` |
| Bridge `pairSubmit` | Dispatched |
| `PairingSession.Submit` | **Fire-and-forget** (`_ = SubmitCoreAsync`) |
| `PairingCoordinator.SubmitAsync` | **Silent `return` (no Emit)** if offerId ≠ active, or phase not AwaitingCode/Failed, or duplicate while Pairing |
| Invalid code | Emits Failed |
| `HostPairClient.PairAsync` | Runs when phase advanced to Pairing |
| Snapshot Pairing / PairedWaitingForDevice / Failed | Only if Emit runs |
| React | Renders mapped phase |
| Device appearance | **Never fabricated** — waits for DeviceTracker / connect mDNS |

### Live host:pair

**Not run** — no pairing mDNS service advertised (`adb mdns services` empty).

### First broken step (dominant, from code)

1. **Silent no-op in `SubmitAsync`** when offerId/phase gates fail → UI stays on code entry with **zero feedback**.
2. **By design:** success → `PairedWaitingForDevice` only; Wi-Fi device appears only if ADB later connects. User can read this as “Pair did nothing” even when `host:pair` succeeded.
3. Secondary: height thrash at enable (above) can make the click feel unstable.

### Auto-connect

Product does **not** call `host:connect`. Auto-connect is **ADB/OS behaviour**, not AppSnapshot logic. No evidence of app-initiated connect.

---

## 4. WD off/on + stale coexistence

### Code facts

| Event | Effect |
|---|---|
| mDNS complete list **without** active offer Id | `ReduceMdnsServices` **clears** `Pairing` operation (`StateReducer`) |
| `MdnsTrackerFailed` | Sets wireless timers only — **does not clear** `PairingOffers` / `Pairing` |
| USB in `track-devices` | Remains independent of mDNS |
| Offer returns with **same** `DiscoveryKey` | Treated as new vs previous empty set → `DetectPairingOffer` re-arms PairingAvailable (10 min cooldown applies to notifications only) |

### Distinction

- **USB card remaining after WD off:** expected if USB still in DeviceTracker.
- **Pairing offer / operation remaining after WD off:** incorrect if mDNS failed **without** an empty complete list; incorrect if UI shows AwaitingCode after offers cleared (would be a snapshot/UI bug — not reproduced headed here).
- **Offer “resurrection”:** same service re-advertised → same Id reappears in offers → **correct reducer behaviour**, can feel like stale spam.

### Exact root cause (policy/state)

Stale pairing UI across WD cycles is explained by:

1. **Asymmetric clear:** empty mDNS list clears pairing; **tracker failure does not**.
2. **Re-offer on same DiscoveryKey** after gap → new PairingAvailable / offer card.
3. USB persistence is **not** a bug.

---

## 5. Offer resurrection

Identity: `DiscoveryKey = serviceType + \u001f + instanceName` → `PairingOffer.Id`.

If the phone re-advertises the same instance, Id is stable. After a period with Id absent, reappearance is a **new** offer edge for notifications (`!prevIds.Contains`). Not a random new GUID unless the instance name changed.

Consumed/active filtering: operation clears when offer missing from bindings; offers themselves are whatever the latest complete mDNS list contains — **no “consumed” flag** suppressing re-display.

---

## 6. Acrylic delay (“delayed camera”)

### Environment

| Item | Value |
|---|---|
| Windows | 10.0.26100 (build **26100.9457**) |
| WebView2 Runtime | **153.0.4234.48** |
| WebView2 SDK | **1.0.4191.47** (`Directory.Packages.props`) |
| GPU | AMD Radeon 780M + NVIDIA RTX 4050 Laptop |
| Backdrop API used | `DWMSBT_TRANSIENTWINDOW` + `DwmExtendFrameIntoClientArea(-1)` (`FlyoutWindow.ApplyBackdrop`) |

### Probe A (native-only Temporary Window Acrylic)

`scripts/diag-acrylic-native-only.ps1` showed a topmost Transient Acrylic form over a color-cycling behind form. **No BitBlt sampler** — DWM exposes no backdrop-ready event. Automated latency distribution **not quantified**.

### B / C

**Not measured** (would need temporary WebView host / full app instrumentation).

### Lifecycle correlation (code)

Backdrop applied on handle create / theme-transparency changes — **not** re-applied on every `Bounds` animation frame. Stale samples during/after resize are consistent with **DWM Transient backdrop sampling**, not with React content.

### Root layer classification

| Case | Verdict |
|---|---|
| A native-only stale | **Unresolved quantitatively** (human 2–3 s reports match Transient DWM behaviour; probe lacked pixel timing) |
| B WebView integration | Unmeasured |
| C app-only | Unmeasured |
| D | Partial — product uses Transient Window; headed user symptom exists; automated CASE lock pending pixel sampler |

**Working conclusion (not a fix):** dominant suspicion is **CASE A (DWM Transient Acrylic lifecycle)**, possibly worsened when HWND/WebView resize every frame (motion path). Not proven to CASE lock without A/B/C pixel timings.

---

## 7. Motion A/B/C

### Production = A (measured in code)

- FLIP: one `reportContentSize(structural)` → `BeginStructuralResize` → `StructuralSizeAnimator` sets `_flyoutWindow.Bounds` **every tick (~16 ms)**.
- WebView2 control: **`DockStyle.Fill`** → child client size tracks HWND every frame.
- Live typing path: immediate HWND resize (see §1).

### B (final child size once; clip HWND)

**Not implemented** (diagnosis-only; avoided product change).

### C (fixed HWND; FE-only)

**Not implemented.**

### Human A/B/C

**Not collected** — modes B/C were not launched.

### Motion root-cause answers

| # | Answer |
|---|---|
| 1. Chromium relayout every native frame? | **Yes, expected** under A — WebView bounds change every animator tick. |
| 2. Child resize ↔ jank? | **Strongly implicated** by architecture; not isolated vs FLIP/easing without B. |
| 3. Fixed child bounds eliminate scrollbar flash? | **Unknown** — needs experiment B. |
| 4. Parent clipping correct? | **Unknown** — needs B. |
| 5. FLIP unstable? | Geometry probes for FLIP pass automated; human still jerky → **jank source likely compositor/WebView resize**, not only easing. |
| 6. Unrelated transforms? | FLIP only `[data-device-id]` with nonzero dy; PairingCard not transformed. |
| 7. Easing still meaningful after geometry jank removed? | **Unresolved** until B/C remove resize jank. |
| 8. Irregular native cadence? | Timer 16 ms; no stutter measurement this session. |
| 9. UI-thread stalls? | Not profiled. |
| 10. Acrylic correlated with resize path? | **Plausible** — Transient backdrop + per-frame HWND resize; not proven. |

### Dominant / secondary

- **Dominant (architecture evidence):** per-frame **HWND + Dock.Fill WebView resize** during structural motion (and live ±2 px during code entry).
- **Secondary:** FLIP transforms / 333 ms easing may still affect *feel* after geometry jank removed — **unproven**.
- **Easing contribution alone:** **insufficient** to explain human fail given automated trajectory pass.

### Acrylic ↔ motion correlation

**Suspected yes, not proven.** Same HWND is both Acrylic host and animated surface.

---

## Temporary diagnostic files / changes

| Path | Role |
|---|---|
| `ui/tests/diagnosis/*.spec.ts` | TEMP Playwright probes |
| `ui/playwright.diagnosis.config.ts` | TEMP config (**not** used by `visual.ps1`) |
| `ui/vitest.config.ts` | Exclude `tests/diagnosis/**` only |
| `scripts/diag-acrylic-native-only.ps1` | TEMP native Acrylic probe |
| `artifacts/diagnosis/*.json` | Measurement output |
| `research/2026-09-20-prephase10-runtime-diagnosis.md` | This note |

**Not left in shipping path:** no `--diagnose` CLI, no permanent product overlays.

**Revert note:** remove diagnosis specs/config/script when correction pass begins; keep this research note.

---

## Root causes ranked by severity

1. **Structural motion path A:** WebView `Dock.Fill` resized every animator frame → Chromium relayout/jank (human motion fail despite trajectory tests).
2. **Pair-code live geometry:** digit 6 enable / cursor-ring CSS changes card height ±2 px → live HWND resize / scrollbar threshold fights.
3. **Pair submit silent no-ops** in `PairingCoordinator.SubmitAsync` / `PairingSession.Start` with no UI failure state.
4. **Success ≠ device:** no `host:connect`; `PairedWaitingForDevice` can look “stuck”.
5. **WD/mDNS asymmetry:** tracker failure does not clear offers; re-advertisement resurrects offers/notifications.
6. **Acrylic Transient Window:** likely DWM sampling lag (2–3 s reports); CASE not fully locked.

---

## Recommended correction architecture (HIGH LEVEL ONLY)

1. **Motion:** evaluate diagnostic mode B (final WebView size once + HWND clip/reveal) before another easing tweak; keep FLIP for cards if child resize is removed.
2. **Pair-code:** freeze PairingCard box geometry across digit/enabled states (reserve ring/button space) **or** suppress live `reportContentSize` for non-structural pairing chrome churn.
3. **Pair submit:** never silent-fail user submit — always emit Failed/progress or ignore only with visible busy state; surface ignore reasons.
4. **WD:** clear or mark offers when wireless tracker fails; clarify USB vs pairing in UI (presentation only).
5. **Acrylic:** isolate with A/B/C pixel sampler before changing backdrop type; consider lifecycle refresh experiments only after CASE lock.

---

## Open questions requiring user decision

1. Approve building **temporary headed modes B/C** for motion (still non-shipping) to finish A/B/C human scoring?
2. For Acrylic, approve a **BitBlt/pixel sampler** diagnostic to lock CASE A vs B vs C?
3. When live pairing is available, run one **instrumented host:pair** capture (logs only) to close submit E2E?
4. Is ±2 px live resize during code entry acceptable if scrollbar never flashes, or must height be frozen?

## Git status (end of diagnosis)

Uncommitted acceptance work preserved. Diagnosis artifacts/scripts added as untracked/temporary. **No commit. No Phase 10.**

---

## FINAL TARGETED DIAGNOSIS (2026-09-20 afternoon)

### Pair-code geometry contract (frozen for future implementation)

Once `AwaitingCode` is visible, PairingCard outer geometry is fixed for that structural state.

MUST NOT change outer card/window geometry: typing 1–6, backspace, caret, focus, Pair enabled/disabled, hover, pressed, keyboard focus ring.

Tolerance: PairingCard outer height **0 CSS px**; native HWND **0 physical px**; no scrollbar-state change.

The measured 176.25 → 174.25 px change is a **bug**. Not fixed in this diagnosis task.

### Motion A/B/C measured (auto structural ±120 px, fixture multi-device)

Artifacts: `artifacts/diagnosis/motion-{A,B,C}.jsonl`

| Mode | hwndApplies (expand/collapse) | childHeightChangeCount | childResizeCount | elapsed |
|---|---|---:|---:|---|
| A production Dock.Fill | 16 / 15 | **12 / 11** | 12 / 11 | ~344 ms |
| B final child once | 15 / 14 | **0 / 0** | 0 / 0 | ~360 ms |
| C fixed HWND | 14 / 14 | **0 / 0** | 0 / 0 | ~344 ms |

**B removes viewport height churn present in A.** (Objective.)

Human A/B/C launch (do not rank in UI):

```powershell
powershell -File scripts/diag-motion-launch.ps1 -Mode A
powershell -File scripts/diag-motion-launch.ps1 -Mode B
powershell -File scripts/diag-motion-launch.ps1 -Mode C
# optional auto exercise:
powershell -File scripts/diag-motion-launch.ps1 -Mode A -Auto
```

Env: `ADBVIEW_DIAG_MOTION=A|B|C`, `ADBVIEW_DIAG_MOTION_AUTO=1`, `ADBVIEW_DIAG_OUT=<repo>/artifacts/diagnosis`

Gated temporary code in `DiagMotionMode.cs` / `DiagMotionProbe.cs` / `AppHost` / `WebViewHost`. Default unset = production A.

### Acrylic pixel sampler

Probe: `artifacts/diagnosis/acrylic-probe/` (temporary, outside product). Capture: desktop `GetDC`+`GetPixel` (composed). PrintWindow of Transient Acrylic is unreliable (often static/black).

| Mode | first-delta min/median/p95/max | multiSec stale | notes |
|---|---|---:|---|
| A native Acrylic only | **36 / 37 / 194 / 194** ms (n=12) | **0** | Tinted samples track behind colors |
| B Acrylic + transparent WebView2 | **no detect** (n=0) | **12/12** | Stuck gray RGB(84,84,84) ≥3.5 s each |
| C normal AdbView (empty fixture) | **no detect** (n=0) | **12/12** | Sample hit opaque UI blue RGB(96,165,250); no acrylic-only gap |

**Acrylic CASE: B**

Native-only Transient Acrylic is live. Adding WebView2 (even transparent minimal DOM) makes the composed flyout sample stop tracking the behind window for the full 3.5 s wait. Normal app has no reliable acrylic-only pixel under Dock.Fill WebView; sampled content stays frozen vs desktop changes — consistent with WebView2 composition interaction, not DWM-alone.

Lifecycle correlation: B freeze appears as soon as transparent WebView is present (initial show of probe B). Not limited to pin/unpin in this sampler. Human 2–3 s reports remain compatible with WebView compositor lag after show/resize; A disproves “DWM Transient is always multi-second stale.”

### Silent return audit

| Location | Condition | Caller indication | Snapshot change | Log | Class |
|---|---|---|---|---|---|
| `PairingSession.Start` disposed | disposed | none | no | DIAG only if env | USER-ACTION SILENT FAILURE |
| `PairingSession.Start` unknown offer | resolve null | none | no | Information | USER-ACTION SILENT FAILURE |
| `PairingSession.Start` invalid endpoint | TryCreate fail | none | no | Information | USER-ACTION SILENT FAILURE |
| `PairingSession.Start` already active | TryStart/Retry false | none | no | Information | USER-ACTION SILENT FAILURE |
| `PairingSession.Submit` disposed | disposed | none | no | DIAG | USER-ACTION SILENT FAILURE |
| `PairingCoordinator.SubmitAsync` wrong offer / not AwaitingCode / wrong phase | early return | none | no | none | USER-ACTION SILENT FAILURE |
| `PairingCoordinator.SubmitAsync` phase already Pairing | duplicate | none | no | none | VALID IDEMPOTENT NO-OP |
| `PairingCoordinator.SubmitAsync` invalid code while AwaitingCode | Emit Failed | yes | yes | via progressed | OK |
| `PairingCoordinator.Cancel` wrong offer / Cleared | return | none | no | none | VALID IDEMPOTENT NO-OP |
| `PairingCoordinator.TryStart` active op | return false | Session logs | no | Session Information | USER-ACTION SILENT FAILURE at Session layer |

### Live host:pair

**Not executed.** `adb mdns services` showed only `_adb-tls-connect` (no `_adb-tls-pairing`) during session. USB `R8VXA02SNKH` + Wi-Fi device present. DIAG-PAIR logging is gated (`ADBVIEW_DIAG_PAIR=1`) and ready for a later live run.

Auto-connect: **D insufficient evidence** (no successful host:pair this session). Product never issues `host:connect`.

### WD failure retention (ENGINEERING)

`MdnsTrackerFailed` → `ReduceMdnsFailed`: sets `WirelessFailed`, schedules Restoring (5 s) / Unavailable (60 s) warnings; **does not clear `PairingOffers`**. Offers clear only on `MdnsServicesReceived` mapping (empty list or offer withdrawn). **Retain-on-transient-failure is current frozen behaviour.** Policy change deferred.

### Offer resurrection

Same `DiscoveryKey` absent → present again remaps to a PairingOffer and can re-arm PairingAvailable notification. Technically correct discovery; UX may feel like stale resurrection. Not a StateEngine overwrite-by-old-generation defect when `LatestAppliedMdnsAt` gating holds.

---

## Update 2026-09-20 afternoon — Pairing transition + drag region (diagnosis only)

**HEAD:** `703b4f38111f1859939d7cca172fb2c0be8767ec` (unchanged; dirty tree preserved; no commit)

### Pair offer → AwaitingCode (live log + code)

| Question | Evidence |
|---|---|
| WebView navigation/reload? | **No** during pairStart (NavigationStarting only at process show `13:52:45`) |
| React root / App remount? | **No** production path (StrictMode mount once; no navigate) |
| PairingCard fiber? | **Reused** (no `key`); early-return branches **replace entire DOM subtree** (offer vs code) |
| DeviceCard remount? | **No** from pairing alone (stable `key={d.id}`); keys change only when device set changes |
| Structural FLIP? | **Not used** for pairing phase changes — FLIP only in `handleToggleExpand` |
| Native resize? | **Live `ApplyImmediateResize`** on layoutKey change (`420→501` at pairStart) — **instant snap**, not `StructuralSizeAnimator` |
| First discontinuity | Instant HWND height jump + PairingCard inner-tree replacement → feels like reload |

### Pair success (live + reducer)

| Item | Evidence |
|---|---|
| host:pair / PairedWaiting | Notification `PairingResult` / `Paired with Android device` at `13:54:13.564` |
| Classification | **CASE C** (backend generates; frontend may paint ≤1 frame) — matching Wi-Fi clears Pairing in **same** `DeviceListReceived` Reduce (see `paired-waiting-same-reduce.json`; hermetic `backendOnlyDurationMs: 5`) |
| Live UI window | `13:54:13.564` → height churn `489/560/525` by `13:54:13.684` (~120 ms) |
| Device arrival jolt | Same snapshot can clear PairingCard **and** insert Wi-Fi DeviceCard → one Live content-size delta (remove+add) |

### Header drag

- DESIGN §19: pinned header is draggable region; pin control excluded; native host owns drag (WebView2 `IsNonClientRegionSupportEnabled` + `-webkit-app-region`).
- Chromium CSS heatmap (`artifacts/diagnosis/header-drag/`): ~95% of header mapped `drag` except pin (`B`); **upper≈lower** in Chromium.
- Human WebView2 “only lower half”: **not explained by missing CSS on title/icon in Chromium** → suspect **WebView2 non-client region / acrylic transparency mapping**, not DESIGN “lower-half strip”.
- `FlyoutWindow` `WM_NCHITTEST` only collapses resize grips to `HTBORDER`; does not invent caption — HTCAPTION comes from WebView2.

### Acrylic

Prior CASE B still authoritative; not re-run pixel sampler this pass. Human reports unchanged. No new workaround.

### Artifacts

- `artifacts/diagnosis/pair-success/events.json`
- `artifacts/diagnosis/pair-success/paired-waiting-same-reduce.json`
- `artifacts/diagnosis/pair-offer-code/fixture-url-nav-baseline.json`
- `artifacts/diagnosis/header-drag/css-heatmap.json` / `.txt`
- Diagnosis tests: `ui/tests/diagnosis/*`, `ui/src/diagnosis/*`, `tests/AdbView.Tests/Diagnosis/*`, `ui/playwright.diagnosis.config.ts`

**DO NOT FIX / DO NOT COMMIT / DO NOT BEGIN PHASE 10.**

