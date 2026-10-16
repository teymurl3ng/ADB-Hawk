# WinUI feasibility checkpoint — M02

Date: 2026-10-15. **M02 PASS for platform feasibility on this machine.** Final unchanged full gate: **1,016 succeeded, zero failed/skipped** (`m02-closeout-production-verify.log`); protected production hashes and R1d source/binary identity match. Production remains WPF; M03 has not started. Complete-app ceiling stays 200 MiB; latest isolated prototype peak is 194.0 MiB, with thin headroom. M09 integrated/hardware/lifetime qualification, M11 clean deployment and M12 device/soak remain unverified. They are not claimed by this prototype.

## What was implemented

The nonshipping project is `prototypes/WinUiShellProbe`, outside the release solution and installer. It has no reference to the production app/backend, no ADB access, production mutex, registry preference writes or real credentials. It uses .NET SDK 10.0.401, Windows App SDK 2.5.1 stable, the existing CsWin32 0.3.333 pin and a recorded dependency lock. No system-wide SDK/workload/runtime/service was installed.

Its compiled-XAML scene exercises 0/1/8 synthetic devices, list/details/pairing, immediate removal of missing details, rejection of older revisions, static progress under reduced motion, and content changes inside fixed native dimensions. The API driver verifies a real failure exit, bounded Acrylic creation and a separate experimental whole-surface interruption path. These are prototype assertions; they do not repair or replace the production snapshot pipeline.

## Results

### M02 closing observations — R2d / R3b / R2e, 2026-10-15

R1d application binary is unchanged. Only existing diagnostic observer/driver/analyzer and documents changed: QPC processing stages, baseline-indexed readiness, explicit owned-underlay warm-open mode, and static destination-text evidence. No new recorder, renderer, package, threshold, backend or production behavior. Final unchanged gate is reported in status.

| Current-machine measurement | Result / evidence |
| --- | --- |
| 100 list/details/pairing navigations | 100/100 changed-pixel windows, static destination-text matches and wrong-route rejections. Four recording integrity passes. Segment p95 <=8.06 ms / p99 <=9.68 ms / maximum <=15.66 ms intervals. Conservative near-settled text maximum 170.91 ms, separate from opening metric. `desktop-motion-44de04e18b4a42729e10c882f7ce7dd0.segment0`–`.segment3`. |
| 100 warm opens | 100/100 destination-text matches and hidden magenta baseline rejections, four integrity passes. Slowest segment p95 47.04 ms against <120 ms, all-open maximum 53.93 ms. `warm-open-7966325dcc014ed69eb92ff0f9b1bf75.segment0`–`.segment3`, `r2e-warm-open-findings.json`. |
| Isolated current resource / latest snapshot workload | 1,000 publications / 794 deliveries, revision 1022, 100 navigations; visible/motion enabled. Peak 203,452,416 bytes (194.0 MiB); last pre-teardown 195,645,440 working bytes (186.6 MiB) / 135,995,392 private bytes (129.7 MiB) / 2,742 handles. `workload-72dc574730da470b801a013dbe5e62a2.json`, samples/resources, `r3b-isolated-workload.log`. |

Desktop-image evidence uses OS presentation QPC and destination pixels, excluding busy indicators. Text proxy: >=95% of settled bright text pixels within 16/255 and mean error <=8/255 in the first-content band of this 476×606 fixture. Per-event strips support inspection. Navigation references are per-route settled frames; warm-open reference is the indexed fully warmed baseline before input. Initial warm-open analysis required an arbitrary fresh late desktop image while the page was static; corrected reference uses identical recording and retains initial output. No samples discarded or thresholds changed.

Accumulation maxima stay explicit: navigation 3/4/3/3; opens 3/2/3/3. No claim of every 144 Hz refresh, physical scanout or 60 Hz qualification. No concurrent capture/build/analysis during either recording or resource workload; other workstation activity uncontrolled. Earlier 865 ms observer delay remains unexplained and failed data preserved; current runs do not establish its cause. No optimization experiment needed. Discrete warm-open repaint intervals do not certify continuous animation; navigation supplies that observation.

M02 is platform feasibility; full integrated section 8 qualification belongs to M09, clean-machine/folder execution to M11, device/soak to M12. Physical contrast/Narrator/mixed-DPI, ten-minute integrated hidden idle, 500-cycle integrated lifetime and complete-app memory remain open. Thin headroom is a material carried risk. No release readiness follows from M02.

### Latest presentation finding — R2c, 2026-10-15

Read-only reanalysis closes the proposed readiness explanation without a code change or new capture. All four R2b compressed pixel streams have exact indexed lengths, valid gzip CRCs and no trailing data; every event's changing-frame count reproduces the saved analysis. The missing transitions are indices **20/21**, **6,319.00/6,633.18 ms** after the first input, rather than initial commands. Both full 190 ms windows lie between indexed frames 384/385, a **865.15 ms** gap. Frame 384's observation lag is **863.93 ms**; frame 385 reports **94 accumulated desktop updates**. Handling returns in **3.10/2.05 ms**, which says nothing about useful-content readiness.

This establishes observer loss during those windows and falsifies readiness-before-baseline as their explanation. It does not identify why the observer was delayed or whether app presentation also stalled. Accumulated desktop updates cannot be converted to dropped app frames. Preserve the 98/100 coverage result and failed cadence qualification. Evidence: `m02/r2c-retained-coverage.json`, derived from the unchanged `desktop-motion-802fd591b43a46a6986b3cc820135d27.segment0`–`.segment3` groups. The collector's early readiness signal is categorized as later test-equipment hygiene, not fixed in this item. Further processing-loss investigation, hardware and integrated-resource work remain separate; M02 is incomplete.

Checkpoint: full unchanged production gate **PASS**, 1,016 succeeded, zero failed/skipped (`r2c-production-verify.log`); all 27 protected production-source hashes and retained R1d prototype source/binary identity match. No new UI launch or shell behavior is claimed by this documentation/evidence-only item.

### Latest input correction — R1c / R1d, 2026-10-15

The stubborn bubble/flyout focus case now passes in all three retained clients, with the original assertions unchanged. This supersedes earlier focus-failure status below; historical failures are retained. M02 is still incomplete because presentation/resource/environment qualification is separate.

Two narrow changes in the existing prototype lifetime owner:

1. User-open activates the flyout and issues one checked `SetForegroundWindow` request if needed. Refusal is logged; no retry, lock-timeout change or forced transfer is added. Native CUIAutomation8 On/Off input tests failed without this request and pass with it under the existing owned-probe permission control.
2. A same-current-thread `WH_CBT` guard returns 1 only for `HCBT_ACTIVATE` targeting the existing bubble HWND. It forwards every other code/window, roots its delegate, reports native registration/removal failures, unhooks during owner disposal and retains the fail-closed exception boundary. It does not install a global hook, observe keyboard content, add a window/provider/renderer, disable the action or restore focus to another application after theft. The original managed helper still reaches Invoke and opens the flyout while bubble activation is rejected before it occurs.

| Client / unchanged assertions | Before correction | Corrected result |
| --- | --- | --- |
| Managed UIAutomationClient | Flyout opens, bubble briefly foreground | Full input test **PASS** with activation veto |
| Native CUIAutomation8, AutoSetFocus On, owned permission grant | Flyout fails foreground | Full input test **PASS**, bubble never foreground |
| Native CUIAutomation8, AutoSetFocus Off, owned permission grant | Flyout fails foreground | Full input test **PASS**, bubble never foreground |

All three corrected runs also pass actual hover/down/click, native pinned drag, pin persistence, outside dismissal, lookup-only control, MTA check, Escape/Back, pinned hiding and native menu cancellation. Trace shows both a successful native foreground request and a refused request logged without retry. The managed path's precise internal initiating function is still unknown; the guard is verified by observed pre-activation veto plus preserved action, not a claim about its private implementation. Physical Narrator/high-contrast/hardware qualification is not replaced by these clients.

Evidence: `r1c-findings.json` / `r1c-source-identity.json`, `r1c-native-off.log`, `r1c-native-on.log`, `r1c-managed-compatibility.log` (preserved partial-fix failure); `r1d-managed-input.log`, `r1d-native-on.log`, `r1d-native-off.log` (all exit 0). The existing seven API/negative cases pass after republish (`r1d-api-verification.log`), including callback failure. No assertion, dependency pin, timing budget or source-boundary rule changed. Existing application source remains protected and WPF is supported. No migration, new final styling, real-device operation or new memory/cadence result is claimed here.

Final gate **PASS**: 1,016 succeeded, zero failed/skipped; format, Release build, boundaries and publish smoke (`r1d-production-verify.log`). Repeated startup because bubble lifetime changed: ten fresh processes on this OS-warm developer machine pass, tray-ready p95 **464.22 ms**, primary flyout never shown, no guard cleanup failures. Evidence: `r1d-startup.log`, current `startup-summary.json` / `startup-observations.json`; original series preserved in `*-before-r1d.json`. Source/binary identity: `r1d-source-identity.json`; all three client results and zero cleanup failures: `r1d-findings.json`. All 27 protected production-source hashes still match. No owned probe remains. The next scoped item is presentation-observer readiness/coverage analysis using existing data and tools; no further focus/provider experiment is needed for these passing assertions.

### Latest bounded checkpoint — R1b / R2b, 2026-10-15

This supersedes the older next-item text below. Production remains WPF; M02 is still incomplete. This checkpoint changes test clients/drivers and documentation only. No product/prototype renderer, host/provider, backend, dependency or acceptance threshold changed.

**R1b closed with a negative result.** The same native COM CUIAutomation8 client was run with explicit AutoSetFocus On and Off, read back as 1 and 0 respectively. For each, the actual foreground test underlay granted permission only to the verified owned probe PID immediately before Invoke; grant success and QPC stage were recorded. Both reach the action, preserve bubble nonactivation, and fail to bring the flyout foreground. Neither full input run passes. This falsifies the proposed sufficient remedy of setting client focus policy and granting probe foreground permission in this test. It does not establish that the OS permission remains unchanged through every internal UIA call, explain the legacy managed path, or prove a WinUI framework defect. No third focus condition was attempted.

Paired evidence: `r1b-focus-on-just-in-time.log` / `r1b-focus-off-just-in-time.log`, cases `native-input-eb40fad4aeb343f590ff47e94c63e354` / `native-input-ebb832c85c93437ea78ddc4392c51e04` with `.lookup.json`, `.uia.json`, `.json.trace.txt` and `.input.json`. An initial On run granted before helper creation; the paired runs corrected that control's timing. The managed-client failure remains available and unchanged. The permission helper has no all-process grant or system setting change.

**R2b fixes the recording size failure, but does not pass cadence.** The existing observer ran four serial 25-navigation, ten-second segments against the same warmed, pinned visible prototype. All four exit successfully, stay under the existing 128 MiB per-file bound and pass unchanged indexed-frame integrity analysis. The driver records 100 commands. Native bounds remain unchanged through capture. No concurrent test/build/analysis was launched by this task. Other workstation activity was uncontrolled; this is a developer desktop, not an isolated benchmark machine.

| Segment | Desktop frames | p95 first changed pixel (ms) | p95 / p99 changing-frame interval (ms) | Maximum interval (ms) | Maximum observer accumulation | Transitions without an observed change in the analysis window |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 511 | 28.71 | 21.62 / 39.18 | 54.95 | 94 | 2 |
| 1 | 1,137 | 22.67 | 7.61 / 14.29 | 28.27 | 4 | 0 |
| 2 | 857 | 22.74 | 7.70 / 13.26 | 21.23 | 4 | 0 |
| 3 | 637 | 23.79 | 21.33 / 34.93 | 48.93 | 9 | 0 |

All segments are retained. Two exceed the reference cadence percentiles, one has a >50 ms observed gap, and observer misses prevent attributing those gaps to the app. Only 98/100 commands have changing pixels observed inside the current 190 ms analysis window; do not certify full transition coverage or omit missing samples to claim latency acceptance. Busy pixels can contribute changes, so these figures do not prove useful-content readiness. There is no pooled percentile across segment gaps, physical scanout claim, 60 Hz result or complete-app performance pass.

Evidence prefix: `desktop-motion-802fd591b43a46a6986b3cc820135d27` with `.segments.json`, `.summary.json`, `.json` and four `.segmentN` groups containing compressed pixels, frame CSV, events, metadata, analysis and error output. Compressed files are approximately 15.6/34.4/26.9/19.3 MiB. First-segment details phase PNG was inspected: incoming details are visible by the approximately 90–120 ms phase. Static appearance remains the rough synthetic fixture, Windows build 26100, 125% scale / inspected 144 Hz display. That spot inspection is not readiness proof for every navigation.

**Current decision:** M02 does not pass. Stop adding capture/automation infrastructure. The narrow next product question is whether the existing explicit user-open path must issue and check a native foreground request rather than rely only on `Window.Activate`; use existing test clients and keep both focus assertions. No host/provider rewrite, framework switch or contract change follows automatically. The previous 197.4 MiB prototype peak remains the latest memory observation; no new memory run or integrated-headroom claim is made here. Hardware/accessibility, cadence attribution, integrated resources, packaging and soak remain open.

### Latest bounded checkpoint — R1–R4, 2026-10-15

This checkpoint supersedes the older next-step statements below; the historical observations remain evidence, not current acceptance. M02 is **incomplete** and production remains WPF. No backend extraction, UI styling change, package change or new host/provider implementation was performed.

**R1 completed its two experiments and stopped with a finding.** Two matched managed-client trials establish that lookup and pattern acquisition preserve the owned underlay's foreground. Foreground activation occurs after BeforeInvoke and before the app's action entry. Caching properties before Invoke did not remove it. A direct native COM CUIAutomation client, using the same Windows PowerShell 5.1 windowless MTA host and named action, invokes the handler without bubble foreground activation, but the flyout does not become foreground. Thus both complete input runs fail, for different reasons. Neither is a passing product fix. This falsifies the assumption that invoking the WinUI action necessarily requires the bubble to become foreground; it does not identify the precise implementation responsible for the managed path.

Evidence: `r1-findings.json`, `r1-source-identity.json`, `r1-staged-input-1.log`, `r1-staged-input-2.log`, `r1-native-com-input-1.log` and their referenced `.lookup.json`, `.uia.json` and native QPC traces. Actual foreground markers, lookup controls, client apartment and existing whole-run assertions are retained. The native comparison is a single small diagnostic script; no third focus experiment or custom provider was added.

Microsoft's documented UIA AutoSetFocus policy and foreground privilege provide a concrete next question: compare explicit client focus policy with equivalent permission to foreground the flyout. These settings were not measured/normalized in R1, so they remain hypotheses rather than a demonstrated cause. See the dated research note. The current input failure is not proof of a WinUI framework defect.

**R2 failed and stopped after one capture.** The 100-command recording `desktop-motion-aea68c8691cc462596d01a068c78acd0` exceeded the existing 128 MiB diagnostic disk bound. Its analyzer then rejected unindexed frame data. `r2-clean-desktop-motion.log` and `r2-partial-analysis.log` preserve both failures. No cadence percentile from this run is accepted; the prior 30-transition trace remains provisional. The file bound and integrity checks were not raised or removed. A separate future driver-only scope can split the same instrument into bounded segments or reduce stored pixels.

**R3 completed one current-build resource workload.** `workload-416ef7d20eeb4d9a804e2c7c7fcf5db9.json` and its `.samples.json` / `.resources.json` record 1,000 publications, 640 visual deliveries, newest revision 1022, a 10,008.33 ms burst and 100 navigations. Motion and visibility are true. Sampled peak working set is **206,979,072 bytes (197.4 MiB)**; final sample at 66.28 seconds is **195,186,688 bytes (186.1 MiB)** working set, **133,959,680 bytes (127.8 MiB)** private, **2,720 handles**. This observed prototype run fits 200 MiB with only **2.6 MiB** sampled headroom before backend integration; it does not establish a plausible complete-app margin by itself. No forced GC or trimming. Partial R2 analysis overlapped part of this run, so mutation timings are not accepted as isolated latency evidence. All samples, including earlier over-budget runs, remain recorded.

| M02 criterion | Current evidence | Verdict / remaining gap |
| --- | --- | --- |
| Stable API/compiler, self-contained non-smoke XAML startup, failure exit | Published pinned prototype; seven existing API/negative cases; startup and callback exceptions owned | API checks pass; clean-machine runtime remains separate |
| Synthetic 0/1/8 data, navigation/removal/revision/reduced motion and fixed geometry | Existing assertions, current seven-case driver and 1,000-publication workload | Pass for prototype semantics; no backend proof |
| Transparent cross-process input / selected fallback | Both transparent variants failed; tightly bounded stationary fallback input passed | Fallback B retained; outer enter/exit disabled |
| Native tray/menu, drag, outside activation, Escape and bubble mouse input | Retained assertions pass in all three R1 extended runs | Actual Explorer restart is not established by owned-icon recreation |
| Accessible action and no bubble foreground theft | Managed client opens flyout but activates bubble; native client avoids bubble activation but fails flyout foreground | **FAIL for combined acceptance**; client policy/foreground distinction unresolved |
| Startup and switcher behavior | Ten fresh OS-warm processes, tray-ready p95 450 ms; primary flyout not shown; switcher API flags verified | No clean-machine or full visual Alt-Tab/taskbar audit claimed |
| Environment changes / placement | Fifteen synthetic placement cases and native display-message delivery | Physical mixed-DPI/taskbar/work-area hardware coverage incomplete |
| Actual presentation / no unexplained warm stall | Older 30-transition trace provisional; clean 100-command capture and integrity checks failed | **OPEN**; useful-content readiness and clean cadence unverified |
| Prototype resource path | Current sampled peak 197.4 MiB before backend; prior peak 201.5 MiB | Limited observation within ceiling; complete-app headroom and lifetime unproven |
| Existing production regression gate | Required full gate recorded separately for each checkpoint | A green regression gate cannot close input/presentation gaps |

**Decision:** do not start M03. The next focus item should be a bounded test-client focus-policy/foreground-permission comparison using the retained clients, not another host/provider rewrite. A separate bounded capture-driver item must repair recording coverage within the existing limit. Integrated memory, full accessibility/hardware matrix, packaging and release soak remain open under their existing phases. No requirement or budget was weakened.

| Check | Result | Evidence / limit |
| --- | --- | --- |
| Exact stable package restore, compiler and self-contained publish | PASS | `m02/publish.log`, dependency lock; zero warnings/errors on successful build |
| Compiled XAML from a non-smoke publish directory | PASS | `m02/api-verification.json`, case JSON and startup traces |
| Intentional failed assertion | PASS | `forced-failure` returns exit 1 and Passed=false; missing/crashed/timed-out results fail the driver |
| Unexpected managed verification failure | PASS | `--hold-ms=bad` exercises the UI exception owner and returns exit 1 with a structured failure result |
| Synthetic latest-revision, removal, reduced-motion and fixed-size checks | PASS for the prototype | Reported true in each successful case; not backend/unit-test replacement or proof of system-setting delivery on all hardware |
| Bounded Acrylic API and transparent brush setup | PASS for construction/lifetime | System dispatcher was absent and must explicitly be ensured; actual appearance/cadence remains separate |
| Interrupted surface driver at 25/50/90% | PASS for completion ownership | Latest generation 8 completes; no claim of measured presented-value continuity from these counters |
| Preferred transparent envelope + Acrylic | FAIL | Positive control succeeds; invisible margin intercepts actual input intended for another process; `m02/input-acrylic.log` |
| Fallback A: transparent envelope + opaque visual | FAIL | Same cross-process interception; `m02/input-opaque.log` |
| Fallback B: tightly bounded stationary opaque window | PASS for outside-click input | Positive-controlled click just outside the visible panel reaches the separate underlay; `m02/input-stationary.log` |
| Prototype resource ceiling | PASS for current prototype feasibility; integrated app unverified | Isolated R3b current binary peak 194.0 MiB, pre-teardown 186.6 MiB working / 129.7 MiB private. Only ~6 MiB sampled peak headroom; M09 complete-app requirement unchanged. |
| Actual desktop presentation | PASS for current-machine prototype feasibility | R2d 100/100 navigation and destination controls; four observed cadence statistics within reference limits. R2e 100/100 warm opens, slowest segment p95 47.04 ms. Physical/60 Hz/integrated qualification remains M09. |
| Native tray registration / version-4 callbacks / hidden receiver recreation | PASS for the probe | Seven-case API driver includes keyboard selection, ignored duplicate button-up, owned icon delete/re-add and a real callback-failure exit. Explorer itself was not restarted. |
| Bubble hover/mouse-down/click, pin/deactivation, Esc and native menu cancellation | PASS in current extended runs | All three retained client input runs pass with foreground assertions intact. Full product command/menu parity remains M08. |
| Native pin drag / external UIA Invoke / DPI and work-area matrix | Input PASS; hardware matrix OPEN | Actual drag and accessible opening without bubble activation pass in managed/native On/Off runs after the checked foreground request and own-thread activation veto. Fifteen synthetic geometry cases pass; physical mixed-DPI remains untested. |
| Fresh-process startup | PASS for current prototype on this machine | Ten repeated fresh processes, OS caches warm: tray-ready p95 464.22 ms; flyout never shown, bubble show does not take foreground. Not clean-machine or full-app evidence. |
| Synthetic 100-snapshot/s burst and 100 navigations | PASS for the probe | 1,000 accepted publications in about 10 seconds, highest revision 1022 delivered; source events remain separate from the visual mailbox |
| Clean-machine / ARM64 execution / release soak | NOT EXERCISED | Existing release rows remain open |

All `m02/` paths above are under `artifacts/winui-rebuild/`. The driver is reproducible with `prototypes/WinUiShellProbe/test-probe.ps1 -Publish`. The transparent experiment is retained for regression/evidence and is explicitly not the selected production surface. The candidate is the runbook's **fallback B**: instantaneous outer show/hide, with shared motion limited to internal navigation/feedback.

## Resource measurements and interpretation

The first five-second stationary scene sample recorded 178,511,872 bytes working set, 116,756,480 private bytes and 2,438 handles. A separate fresh-process run after exercising the synthetic scenes recorded:

| Seconds after ready | Working set (bytes) | Private bytes | Handles | CPU seconds during interval |
| ---: | ---: | ---: | ---: | ---: |
| 5.96 | 183,156,736 | 117,370,880 | 2,438 | 0.03125 over 5 s |
| 15.99 | 183,156,736 | 117,370,880 | 2,438 | 0 over 10 s |
| 31.02 | 183,107,584 | 117,256,192 | 2,434 | 0 over 15 s |

The last sample is about **174.6 MiB working set / 111.8 MiB private memory**. No forced GC, memory trimming or application minimization was used to obtain these numbers. This is a short visible scene test, not a ten-minute hidden-idle benchmark, leak test or framework-wide lower bound. Later intervals show no observed CPU growth; the first interval must not be extrapolated as persistent idle CPU.

The running WPF baseline was about 143.9 MiB working set / 78.3 MiB private memory in its separate observation, and also exceeded the historical working-set target. These workloads are not identical, so the difference does not establish a precise framework tax. The prototype failed the former 120 MB target; on 2026-10-15 the owner answered “Allow up to 200 MiB for WinUI”. The measured scene fits that revised ceiling, leaving about 25 MiB before integration. Removing unused published AI files is not a demonstrated working-set fix: the sampled native module list did not show AI/Search/WebView modules loaded.

Raw repeat data: `m02/settled-resource-13addbd97eb14b38bb3329595c8d8064.json.samples.json`; loaded native module inventory is beside it. The initial self-contained publish had **517 files / 238,441,940 bytes** before later prototype additions; its inventory is `m02/publish-inventory.csv`. That is not an installer/ZIP size, and this output is not packaged for users.

### Navigation workload follow-up

An initial version rebuilt XAML controls on every route change. After the 1,000-update burst and 100 navigations it retained 228,700,160 bytes working set (about 218.1 MiB), exceeding the approved ceiling. Its 60.44 ms maximum mutation call overlapped the production verification build, so that latency result is confounded rather than a proven intrinsic stall.

The revised probe keeps at most one list, details and pairing page, reuses controls, treats same-target navigation as a no-op, stops detached progress and clears synthetic code drafts on hide/leave. It does not keep an unbounded page history. A separate run without the concurrent build recorded:

- **1,000 publications / 649 visual deliveries**, newest revision **1022**, burst **10,002.94 ms**. Intermediate visual projections coalesce; domain events/actions are not passed through this mailbox.
- **100 navigations**, UI-thread mutation p95 **2.29 ms**, maximum **2.47 ms**. These are mutation-call durations, not input-to-frame or presentation latency.
- Maximum sampled working set **188,895,232 bytes (180.1 MiB)**; final sample at 68.06 s **182,292,480 bytes (173.8 MiB)** working set, **123,768,832 bytes (118.0 MiB)** private, **2,471 handles**.
- CPU seconds unchanged through the last settled intervals. No forced GC or trimming. This is not the 500-cycle lifetime or ten-minute hidden integrated-app test.

Evidence: `m02/workload-d8d50cb264764015ab13cc728cfd3313.json` and `.samples.json`; driver log `m02/reused-workload-test.log`. This establishes a plausible prototype path within 200 MiB; it does not reserve an additional 200 MiB for the backend or prove the complete app fits.

**Visibility-controlled repeat:** the synthetic panel is now pinned before the workload and result JSON records `AnimationsEnabled=true` / `VisibleAtCompletion=true`. `m02/workload-ff7f6dcf56204e2ebdc3b9ee8a95ef8b.json` delivered all 1,000 publications over 10,006.41 ms, 642 visual projections, revision 1022 and 100 navigations (mutation p95/max 2.52/2.64 ms). Final working set at 64.97 s was **207,114,240 bytes (197.5 MiB)**, private **136,200,192 bytes (129.9 MiB)**, **2,772 handles**; sampled peak **211,279,872 bytes (201.5 MiB)** exceeds 200 MiB. Earlier runs did not record visibility, so their 174 MiB result is not worst-case acceptance or an isolated proof that page reuse alone caused the entire reduction. Keep all measurements, not only the smallest.

The animation probe now explicitly removes scoped-batch completion handlers both on completion and cancellation before disposing them. Previously it disposed batches without removing those handlers. This is an ownership correction; it is not a proven explanation for the memory difference. A repeat is required. The workload driver now records a separate resource verdict and fails if the sampled ceiling is exceeded; semantic/API success alone cannot pass the resource gate.

**Repeat after handler ownership correction:** `m02/workload-0aa0a427edc845a7bc50d628e5f4ac3a.json` again records motion enabled and visible at completion, 1,000 publications / 643 visual deliveries, revision 1022, 10,011.15 ms burst and 100 navigations (mutation p95/max **2.83/3.39 ms**). At 64.94 s: **199,565,312 bytes (190.3 MiB)** working set, **135,864,320 bytes (129.6 MiB)** private, **2,777 handles**; sampled peak **203,329,536 bytes (193.9 MiB)**. This particular repeat fits the ceiling but leaves only about **6 MiB** below it at the sampled peak, before backend integration. It does not prove the handler correction caused all of the difference, a leak is absent, or that the earlier 201.5 MiB result cannot recur. A bounded-view/rendering and integrated resource qualification is still needed. The separate resource-verdict writer was added after this run; its retrospective verdict is derived from the saved samples, not a new execution.

### Input observations and interpretation

The initial guarded real-input run passed positive control, bubble hover and mouse-down foreground preservation, explicit flyout opening, pin persistence, unpinned outside dismissal, Escape from details to list and pinned hiding, native menu opening/cancellation, and zero bubble foreground activations. A normal Button with interaction focus disabled is retained; an experimental custom bubble peer was removed because there was no evidence it was needed.

One `WM_ACTIVATE` observation occurred while a different HWND remained foreground. Win32 thread activation is not the same as global foreground activation; counting that message alone initially produced a false focus-theft diagnosis. Both counters are retained, and actual foreground observations are the input acceptance evidence. Escape required a platform `KeyboardAccelerator`; the original page key handler did not receive it in this probe. No native message counter is treated as frame-presentation proof.

`test-native-input.ps1` now also attempts actual pinned drag and external UIA Invoke. Later attempts were **inconclusive**, because ShellExperienceHost covered the bubble target; the PID guard stopped before clicking it. Do not mark those extended assertions passed from the earlier, smaller test.

## Build findings that must carry forward

1. Keep `EnableMsixTooling=true` for compiled resource generation even with `WindowsPackageType=None`. The first false setting produced no application PRI and a native XAML crash; enabling it generated the application PRI and fixed the launch. The probe does not create/register an MSIX package.
2. The SDK's self-contained initializer needs a documented DLL-search policy under the current analyzer gate. The assembly-level SafeDirectories setting handles this without changing cached SDK code or disabling a diagnostic.
3. Let the compiler generate the complete Application type from App.xaml and use the small explicit startup method, rather than suppressing CA1515 on handwritten public application code. Failed accessibility/reflection experiments were removed.
4. Ensure the Windows.System dispatcher before creating a system Composition brush. The WinUI dispatcher alone was insufficient on this host.
5. Use the supported UIElement Opacity/Translation animation targets, not XAML-owned layout Offset; own batch/completion lifetime and invalidate stale generations. Counters still cannot prove smooth presentation.
6. Fail-closed verification is part of the prototype: structured results, unique output files, bounded process waits and nonzero exit. UI/domain unhandled-exception owners report failure; no new analyzer suppression remains in the prototype.

## Visual evidence and product status

Windows build **26100**, **125%** window scale on the inspected **144 Hz** display. The stationary technical prototype was captured using the existing native capture helper with its executable/title/output explicitly redirected to the probe. The original helper would target the live WPF app, so it was not invoked unchanged. The image was inspected: `artifacts/winui-rebuild/m02/native-winui-stationary-probe.png`. It is a functional fixture with rough default chrome, not an approved final visual or temporal evidence.

The native-lifetime header/bubble experiment also has a fresh inspected capture, `m02/native-winui-native-lifetime-probe.png`, Windows build 26100. `prototypes/WinUiShellProbe/capture-probe.ps1` reuses the C# capture helper from `scripts/capture-native.ps1` with an isolated title/process and explicit finally cleanup. The change is a nonshipping technical fixture: default chrome, large heading, synthetic row/pair action and pin are intentionally unfinished styling. It is not the replacement product layout or evidence of real ADB/device details. Failed earlier capture attempts were repaired by pinning the synthetic surface during capture and correcting UTF-8 handling; the one remaining owned probe process was closed before republishing. Final cleanup leaves no probe process or installed app change.

No production renderer/backend behavior changed in M02. The repository app was briefly restarted only after it locked the executable during the final required build; the same source executable was relaunched with `--startup`. Shared adb was not stopped or reconfigured. The installed copy was not replaced. See `m02/production-restart.json` and `m02/production-verify.log` for the final identities and gate result.

Final existing-product gate: **1,016 cases passed**, zero failed/skipped; formatting, Release warnings-as-errors build, boundaries and publish smoke green. All five standalone prototype verification cases and its format check passed. Existing production `src/` files were compared against the protected baseline and remain byte-for-byte identical. Final source app PID **26328**, shared adb PID **5888**. These are process observations at this checkpoint, not identifiers to assume remain valid later.

## Historical wrap-up checkpoint

Use the current result and next scope at the top of this document. The following preserves the earlier checkpoint and its then-open investigations; it is not the current focus-failure status.

M03 is **not authorized by the gate yet**. The owner requested wrap-up on 2026-10-15. The retained source uses the normal WinUI bubble Button; unsuccessful custom peer, native provider, alternate AppWindow/XAML-source host and stack-walker experiments were removed. MTA client threading, windowless helper creation and a native style refresh did not remove the transient foreground activation. Its native caller is not established. Current-source `checkpoint-native-input.log` exits **1**: all recorded observations pass except `BubbleNeverBecameForeground`. Keep this failure explicit; UIA opening itself succeeds.

Retained improvements include native caption-region pin dragging, Grid-based single-page replacement, reversed Back motion, bounded page reuse, synthetic placement checks and event-driven display recomputation. The earlier ContentControl recording retained the old page at roughly 100 ms after navigation; the Grid recording shows the incoming details by that point (inspected event0/phase3 PNG). This observation supports the change, but concurrent input-test startup confounds the second recording. It does not prove a documented ContentControl default delay.

Presentation artifacts: `desktop-motion-97ef7b9271824c44814c8d84b28f29fc` (ContentControl) and `desktop-motion-c3b365f40d89475490247bc3862deebf` (Grid), each with `.frames.csv`, compressed `.bgra.gz`, `.events.json`, `.ready.json`, `.analysis.json` and phase PNGs. The Grid trace has 1,381 desktop updates, 957 changing crop frames, 30 navigation events, 18 accumulated-frame observations and maximum accumulation 3. First-pixel-change p95 is 22.99 ms; interval p95/p99/max is 7.59/8.11/20.61 ms. These are desktop image observations, not physical scanout or useful-content-readiness proof; pairing progress contributes pixels. No clean 100-transition or 60 Hz acceptance is claimed. The longer capture driver was prepared but not executed at wrap-up.

Fresh-launch artifacts: `startup-observations.json`, `startup-summary.json`, `startup-test.log`. Ten fresh processes on this OS-warm developer machine recorded approximately 392–450 ms process-to-tray-ready, no initial flyout show and no bubble show-time foreground activation. There was no OS-cache flush, clean-machine execution or ADB attachment.

Final wrap-up verification: existing-product gate **PASS**, 1,016 cases, zero failed/skipped, format/build/boundaries/Release publish smoke (`checkpoint-production-verify.log`). Current published prototype's seven API/negative cases and standalone format check pass (`final-api-test.log`). The extended native-input test fails as described above. No new complete-app, packaging, real-device or soak acceptance is claimed. The production source was not migrated.

Next minimal experiment: compare a native COM UIA client with the existing managed helper against an isolated nonactivating control, recording activation ordering, before changing rendering or adding more automation plumbing. Then rerun the clean 100-transition presentation recording and the current visible resource workload. Preserve fallback B and the unchanged 200 MiB complete-app ceiling. Physical display/accessibility qualification remains open.

An actual ETW capture requires a Windows administrator token or existing Performance Log Users membership. A single scoped UAC launch of the checksum-verified, Intel-signed portable PresentMon was attempted after preparing the workload driver. Windows returned **operation canceled (1223)**. The probe's 60-second driver-start bound expired; no frame CSV was collected. This was a Windows consent result, not an automatic sandbox-review rejection. No group, account, service or installation change occurred. Do not retry elevation without a fresh go-ahead. The subsequent owner reply asked whether the sparse window was a demo; it did not approve another UAC attempt. The 200 MiB budget is already approved.

The capture driver targets only the owned prototype PID, uses a unique ETW session, disables input tracking, terminates after 40 seconds and never stops an existing session. It arms the workload after profiler startup so consent time is excluded. A future run pins the synthetic panel before the prompt so deactivation does not hide its workload. A nonempty CSV would still require attribution and Composition/presentation analysis; collecting it alone is not a PASS.
