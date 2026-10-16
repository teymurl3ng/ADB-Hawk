# ADB View shell rebuild: execution runbook

Date: 2026-10-15. Status: **ready as an implementation plan; no migration step executed**.

**Execution checkpoint (2026-10-17):** M00–M07 passed; M08 is in progress; the final native-owner full gate passes after the owner-click recheck (see status). Its isolated native-owner slice is verified: full gate 1,067 cases, 29 headed fixtures / 833 assertions, 172 native assertions, 11 guarded real-input observations and two inspected final-binary captures. All 86 prior C# test files, prior backend/WPF/prototype sources and 824 mapped methods retained. Live composition, startup/instance/persisted preferences, actual environment sources and physical checks remain M08; no M09 advance. Production remains WPF. See [status](WINUI-REBUILD-STATUS.md) for the next exact work and limits.

This is the current plan for the user's unconstrained redesign request. It replaces the implementation direction in `MOTION-SYSTEM-PLAN.md` and decision 0028. Those files remain useful audit evidence. The existing WPF application, reliability repairs, design authority and package pins have not been changed by writing this plan.

## 1. Objective and execution rules

**Research/scope baseline, 2026-10-15:** use [WINUI-RESEARCH-BASELINE.md](WINUI-RESEARCH-BASELINE.md) before resuming. It defines the frozen starting evidence, one-question work items, named file scope, at most two experiments and a 60-minute investigation limit. It narrows execution without replacing any M00–M12 acceptance requirement.

Build a quiet Windows 11 tray utility whose state is trustworthy, whose layout stays still, and whose short transitions remain responsive during interruptions. Replace the shell with WinUI 3; retain and regression-test the existing ADB, discovery, pairing and notification behavior. This is a shell migration, not an Android toolbox rewrite.

The intended executor is a model such as GPT-6.1 Sol. Follow the numbered work packages and their acceptance criteria. Do not infer missing API names, start later packages early, or treat a plausible explanation as a verified result.

1. Read this runbook, [research](../research/2026-10-15-winui-shell-rebuild.md), [decision 0029](decisions/0029-stable-winui-shell-proposal.md), `AGENTS.md`, `DESIGN.md`, `ENGINEERING.md`, `PHASES.md`, and `RELIABILITY-REPAIR-RESULTS.md`.
2. Complete M00, then M01, then the M02 feasibility gate. **Do not migrate production code until M02 passes.**
3. Work in small checkpoints. Each checkpoint names its permitted files, tests and evidence. Within an implementation request, continue through passed gates without repeatedly asking permission for already specified work.
4. Use the current working tree, including uncommitted reliability repairs. Never reset to HEAD to obtain a supposedly clean baseline. Do not modify unrelated `.zcode/` files.
5. Record every result in `docs/WINUI-REBUILD-STATUS.md` using the template at the end. PASS requires an artifact or reproducible observation; unavailable hardware is NOT EXERCISED.
6. The user allowed changing the old design/architecture for this direction. M01 makes the exact changes explicit; this is not permission to remove security, reliability or verification rules. This planning task does not itself start implementation.
7. Do not create another chat, switch models, spawn agents, publish a release, or replace the installed app solely because this document names an executor.

## 2. Decisions the executor must not reopen casually

These are product/engineering decisions, not Microsoft requirements. Source IDs refer to the research note.

| ID | Decision | Why / verification |
| --- | --- | --- |
| D01 | WinUI 3 with Windows App SDK **2.5.1 stable** and the existing .NET SDK **10.0.401** | S01–S03; conditional on M02, not a claim of measured superiority |
| D02 | Fixed flyout panel, initially **376 × 480 DIP**; no resizing from snapshots, device count, navigation or pairing phases | Removes recurring outer geometry changes |
| D03 | Device list → device details; list → pairing, within the same panel | Replaces expanding cards and nested height animation |
| D04 | Motion is a small shared policy over platform animations, with a single owner for each animated property | No home-grown spring, frame scheduler or generic layout-animation framework |
| D05 | Optional bubble stays **32 × 32 DIP**, with a normal tooltip; no hover expansion | Stable location and low background cost; existing semantic lifetime retained |
| D06 | Preserve backend behavior and vocabulary; semantic changes appear immediately | Animation must not present stale connectivity or valid-looking expired QR data |
| D07 | Native tray adapter using existing CsWin32, one WinUI UI thread/message pump | Retires WinForms UI plumbing; requires explicit tray parity tests |
| D08 | Three permanent projects: UI-independent `AdbView.Core`, WinUI executable `AdbView`, existing `AdbView.Tests` | Keeps backend tests independent of XAML startup; S14 |
| D09 | Unpackaged, self-contained **folder** distribution, portable ZIP and existing per-user installer | S10; no true single-file EXE promise, MSIX migration or updater |
| D10 | Native controls and theme resources; bounded Desktop Acrylic if proven, intentional opaque fallback | S07–S09; transparency is optional, correct input and readable text are mandatory |
| D11 | No changes to ADB recovery policy, polling, server ownership, phone operations or notifications during extraction | Protects the preceding reliability work |

Do not add MVVM/DI/message-bus/tray/animation packages. Keep existing Serilog, QRCoder, CsWin32 and xUnit pins. The App SDK metapackage has a broad dependency graph: measure the output, keep a dependency lock, and do not manually delete native files to meet a size target. Dependency reduction is a separately researched change if the prototype cannot meet the budget.

## 3. Interaction contract

### Panel geometry and content

- Select the panel size before showing it: width = min(376 DIP, work-area width minus 16 DIP); height = min(480 DIP, work-area height minus 16 DIP). Work in physical pixels for native bounds, convert using the target monitor's DPI, and round once. Clamp the entire visible panel to that work area with an 8 DIP edge margin where space permits. Include any required shadow/envelope allowance in the placement calculation.
- Keep these dimensions for that open session. DPI, work-area change or monitor removal may cause one immediate recomputation. Settle active motion before that recomputation. User dragging changes position, not size. Do not respond to child LayoutUpdated/SizeChanged by resizing the HWND.
- Preserve the existing visual identity: typography, semantic colors, icon assets, corner character and restrained spacing. WinUI theme resources own interactive control chrome. Initial content padding is 16 DIP, section gap 12 DIP, header minimum height 48 DIP, controls minimum height 32 DIP. Text scaling may increase content height; these are not clipping limits.
- Header: existing app identity and pin; Back appears on a subpage. Body owns vertical scrolling. Pairing's action area stays in place across phases; error text scrolls inside the content area. At extreme text scaling/small work areas, allow the full content region to scroll so actions remain reachable.
- Keep a stable trailing scrollbar allowance in the content container; toggling overflow must not change text wrapping width. Reuse keyed rows and controls. Do not rebuild the whole ItemsSource on every snapshot.
- Device row: existing display name, status/transport, status marker, and available battery data. It is a native focusable button/list item. It opens a detail page; no expanded-card state or row-height animation remains.
- Details: current snapshot fields, current guidance and copy actions. On Back, restore the first visible list-item ID plus its offset and focus; if removed, select the nearest surviving neighbor. Long serial/address text must be readable/copyable without widening the window.
- Device removed or authoritative health lost while details are open: remove stale data immediately. Navigate to the current list/health content without an outgoing-device animation. A detached/offline record still present in the authoritative snapshot can remain, using its actual current label.
- Global health, wireless warnings and existing diagnosis/undo actions retain their semantics. Never collapse a local discovery failure into a global ADB failure. Do not omit existing user actions in the redesign.

### Pairing, dismissal and focus

- Pairing uses the same content region for code, QR, working, waiting-for-device and error states. State wording and button enablement come from the current snapshot. Preserve explicit submit, cancel and retry semantics.
- Back and light dismiss change presentation; they do not implicitly submit, retry or cancel an in-flight pairing operation. An existing operation remains discoverable through its current status entry. Explicit Cancel uses the existing command. Clear an unsubmitted code when leaving its page or hiding the flyout; never persist, capture or log it.
- If a QR becomes invalid or its session disappears, remove its image, hit targets and accessible content in that dispatcher application. Do not retain it in a fading page or screenshot. Use fixture-only QR data for captures.
- Opening by tray, bubble or keyboard activates the flyout and puts focus in its content. Background snapshots never activate it. On pointer-open prefer the content root; on keyboard-open choose the first meaningful control.
- Unpinned: genuine activation outside the app dismisses the flyout. Own menus/popups do not. Do not suppress dismissal for an entire entrance animation. If an activation event races opening, identify whether the current foreground HWND belongs to this flyout or its owned popup before deciding.
- Pinned: external activation does not dismiss; the header provides native drag. Pin value remains snapshot-owned. Esc first closes an owned popup, otherwise returns from a subpage, otherwise hides the flyout, including when pinned. Tray toggle hides/shows; the tray menu's Open always shows/activates. Hiding does not silently unpin; process restart retains the existing reset-to-unpinned behavior.
- Bubble: existing aggregate status, suppression over full-screen applications, preference and transient lifetime rules remain. Show without activation and prevent activation on interaction; its click explicitly opens/activates the flyout. Do not assume Show(false) alone covers mouse activation. Tooltip does not resize the bubble. Hide it while the flyout is visible.

## 4. Universal motion contract

### Named recipes

The durations below are fixed initial decisions grounded in S04, not measurements of this app. Define them once. Framework-owned button, focus and scroll behavior can keep platform defaults; app-authored animation cannot compete for the same properties.

| Recipe | Properties | Duration / curve | Trigger |
| --- | --- | --- | --- |
| SurfaceEnter | Entire visible panel opacity 0→1, translation 12→0 DIP along the tray-facing axis | 167 ms, cubic (0,0,0,1) | Hidden→shown only |
| SurfaceExit | Entire panel opacity current→0, translation current→12 DIP toward tray | 167 ms, cubic (1,0,1,1) | Explicit dismissal / toggle |
| ContentNavigate | Incoming page opacity 0→1, horizontal translation 12→0 DIP; reverse direction for Back | 167 ms, cubic (0,0,0,1) | User navigation only |
| Feedback | Nonsemantic highlight/opacity | 83 ms, linear | Copy acknowledgment or app-specific highlight |
| SemanticUpdate | Current text, presence, QR validity, actions and accessibility tree | 0 ms | Every accepted snapshot |
| EnvironmentChange | DPI, forced colors, reduced motion, work-area constraints | 0 ms | Change event |

No scale on text, QR, rows or window; no bounce, elastic overshoot, staggered lists or animation of Width/Height/Margin. No automatic motion on every property-changed event. A status change can be visually immediate and still feel polished because the surrounding layout stays stable.

On navigation, detach outgoing content and remove its focus/hit targets immediately. Animate only incoming content over a persistent panel background. There is never a second, stale live device page. A new navigation replaces an in-flight incoming page without queuing previous destinations.

### Ownership and interruption

Implement only these small pieces under `NativeUi/Motion/`:

- `MotionPolicy`: enabled/disabled state and named recipe parameters; reads the current environment policy.
- `SurfaceTransitionController`: Hidden / Opening / Open / Closing / Disposed state, generation and surface animation ownership.
- `ContentTransitionController`: a separate inner container for page navigation; no window geometry writes.
- Platform animation handles and scoped completion subscriptions. No application per-frame callback in production.

All window visibility commands execute on the UI thread. Each replacement transition increments a generation. A completion can hide/settle a surface only when its generation, target and lifetime still match. Repeating the same target is a no-op. Opening during Closing reverses from the **currently presented** opacity/position; it must not restart at an endpoint. Position continuity is required; do not promise velocity continuity from a cubic keyframe implementation.

M02 must prove the actual stable API mechanism for replacing a Composition animation from its current displayed value. Reading a normal XAML/base property during animation may return the destination, not that value. No guessed sampling API, StopAnimation-then-snap workaround, or timer estimator. Exercise reversals at 25%, 50% and 90% of a transition. If the mechanism cannot be proven, stop the gate and document the failure.

Each retargeted segment has a maximum 167 ms duration. No queue delays input. Close immediately makes the content noninteractive; hide the native window only on the current exit completion. A reopen restores interaction and invalidates the old completion. Reduced motion, suspend and shutdown invalidate callbacks and settle/hide synchronously.

### Reduced motion and inactivity

- Read the system preference before first show; subscribe to `UISettings.AnimationsEnabledChanged`, marshal to the UI thread and unsubscribe on disposal. Effective disabled state wins immediately, including before a delayed snapshot round trip. Publish the appearance change through the existing state path for consistency.
- Disabling motion stops active surface/navigation/feedback effects, applies their current semantic destination and stops busy rotation. Re-enabling permits future effects; it does not replay old transitions.
- Native progress indicators are active only while operation-busy **and** their page is visible **and** motion is allowed. With reduced motion, show static progress/status text. Unloaded or hidden content has no active progress clock.
- The one-second age ticker runs only when its age text is visible. No composition rendering subscription, animation timer, whole-app refresh timer or idle decorative loop. Existing backend deadlines/backoffs keep their independent ownership.

## 5. Rendering feasibility decision

An opaque native window does not become a moving/fading outer panel when only its children animate. Prove the whole visible surface, including outline and shadow, in this order:

1. **Preferred:** bounded Desktop Acrylic using `SystemBackdropElement` inside a supported transparent WinUI window. The native envelope stays fixed during animation. Its margins must be bounded, clipped appropriately and not steal clicks from another process.
2. **Fallback A:** same transparent-envelope approach with an opaque panel visual, if only Acrylic fails quality/resource tests.
3. **Fallback B:** ordinary opaque WinUI window with native outline/shadow; outer window appears/hides immediately, and only internal navigation uses motion. This deliberately disables SurfaceEnter/SurfaceExit for the entire shell, avoiding a decorative panel moving inside a stationary visible rectangle. Record the selected variant explicitly.

Try each fallback only for an identified failed criterion. Do not add layered-window hacks, per-frame SetWindowPos, a third rendering stack or a preview dependency. Failure of all variants means M02 fails: retain the current app and report evidence. Stable-size WPF remains a credible separate alternative; do not implement two replacement systems in this migration.

Transparent margins and rounded corners need **real cross-process hit-testing checks**; returning a nominal transparent hit-test code is not proof. Test clicking a separate test window underneath, at rest and during entry/exit. Size the bounded envelope from actual shadow and travel extents; do not reserve a screen-sized transparent window.

## 6. Target ownership and migration map

| Location | Owns | Must not own |
| --- | --- | --- |
| `src/AdbView.Core/Adb/`, `State/`, `Notifications/`, `Diagnostics/` | Existing protocol, reducers, snapshots, vocabulary, policies and logging | WinUI/WPF/WinForms types, rendering |
| `src/AdbView.Core/Host/` | Existing UI-independent lifecycle/coordinator logic extracted from Host; injected visibility and clock inputs | Native window handles, OS calls, DispatcherQueue |
| `src/AdbView.Core/Presentation/` | Pure latest-revision acceptance and navigation policy, stable IDs, focus/scroll restoration data | Composition objects, another copy of device truth |
| `src/AdbView/Host/` | Win32 tray, window placement, environment signals, preferences, startup, single instance, notification delivery | Device semantics or repair policy |
| `src/AdbView/NativeUi/` | WinUI windows, pages, view adapters, named motion, native controls | ADB execution/parsing, recovery, semantic wording |
| `tests/AdbView.Tests/` | Existing xUnit/MTP runner, backend fakes and pure policy tests; references Core | Constructing XAML objects without a XAML application |
| `src/AdbView/Verification/` plus scripts | Fixture-only assertions on the real WinUI UI thread and structured test results | Live ADB, private QR/code data, silent PASS on unavailable desktop |

Core targets `net10.0-windows` initially, with no UseWPF/UseWindowsForms/UseWinUI and no App SDK package. Keeping a Windows TFM avoids an unrelated portability rewrite. App targets `net10.0-windows10.0.26100.0`, minimum OS `10.0.22000.0`. Core can retain current namespaces and internal types with explicit friend assemblies for app/tests. Move behavior intact before renaming or simplifying it. Do not mechanically move UI-coupled AppHost into Core: extract its backend composition separately and leave native startup/shutdown orchestration in the executable.

Retain existing clocks, operation generations, snapshot revisions, cancellation and backend event queues. Snapshot coalescing applies **only to the visual projection**, never notification intents, domain events, discovery reconciliation or user commands.

The UI mailbox holds one newest pending immutable snapshot and schedules at most one dispatcher drain. It rejects revisions <= last applied. Its replace/drain/reset operation must be race-safe: recheck after clearing the scheduled flag so a publication during draining cannot be stranded. Test both race orders. Diff keyed view adapters and notify only changed properties; a no-op snapshot causes no layout/motion restart. Hidden UI may retain the newest snapshot reference without materializing its tree. On show, apply latest before revealing. Navigation/scroll/focus are presentation state; connectivity, pin and operation status remain snapshot-owned.

### Retire only after replacements pass

| Existing mechanism | Replacement |
| --- | --- |
| `Host/FlyoutRevealAnimator.cs` | SurfaceTransitionController / selected M02 surface variant |
| `NativeFlyoutWindow.ApplySelfSizedHeight`, LayoutUpdated resizing | Pre-show stable geometry and event-driven environment reposition |
| `CompositionTickSource`, `DispatcherTimerTickSource`, local BezierEase | Platform animation handles; no production application frame tick |
| `Themes/ControlsReduced.xaml`, local card storyboards | One live MotionPolicy and platform controls |
| Expanding `DeviceCardView` | DeviceRow + DeviceDetailsPage |
| Expanding status bubble | Fixed BubbleWindow + tooltip |
| UI dispatches per snapshot and unconditional updates | Newest-only mailbox and keyed property diff |
| WinForms Application/NotifyIcon/ContextMenuStrip | WinUI Application + narrow Win32 tray/menu adapter |
| Single-file packaging assumptions | Validated self-contained directory inventory |

## 7. Ordered work packages

### M00 — Record and protect the baseline

**Allowed:** read-only inspection; evidence under `artifacts/winui-rebuild/baseline/`; create the status document.

1. Save HEAD, full git status and a diff inventory; include untracked reliability files. Identify repository versus installed executable and preserve the current working tree. Do not commit unrelated files or stop shared adb.
2. Run `scripts/verify.ps1`. Earlier work recorded 1,010 passing tests; use the actual new result, not that historical number, as the baseline.
3. Record existing shell screenshots/recordings, toolchain/OS/display/GPU, working set, private bytes, handles and CPU. Measure cold process launch separately from first/warm flyout open. Do not call the existing animation callback counters presented-frame timing.
4. Inventory every backend, Host, NativeUi and boundary test. Create a migration table: test/scenario → retained Core test / replacement pure test / WinUI fixture assertion / manual acceptance row. Record every existing fixture and tray command.

**Pass:** current full gate green, dirty baseline protected, test map exists. Unavailable performance tools/hardware are named and block the relevant later gate, not silently marked passed.

### M01 — Reconcile the product and engineering contracts

**Allowed:** AGENTS, DESIGN, ENGINEERING, PHASES, decision 0029, authority hash and narrowly affected boundary tests. No renderer or backend behavior edits.

1. Adopt sections 2–6 of this runbook as the shell contract: fixed geometry, list/details/pairing, pin/Back/Esc, bubble, motion, fallback rules, three-project architecture and folder deployment.
2. Update the specific old rules: AGENTS R01/R02/R04/R09/R16 as necessary; DESIGN shell identity/structure, disclosure, height, motion, bubble and acceptance sections; ENGINEERING project ownership, host/UI, dependencies, reliability rows 29/30 and packaging. Preserve R06 immediate truth, R08 failures, R13 secrets and R14 ADB citizenship. Avoid blanket exemptions.
3. Add this ordered migration track to PHASES without marking earlier release/soak work complete. Mark WPF-only decisions' affected mechanics superseded, with links. Update DESIGN hash only alongside the deliberate contract diff.
4. Change obsolete source tests to test the new written boundary. For example, qualify WinForms type/namespace checks; do not simply allow all Button/Control usage everywhere. Keep negative tests proving forbidden references still fail. During migration, explicitly allow the existing WPF shell until M10 and a temporary prototype; no permanent dual shell.

**Pass:** contract changes are reviewable and internally consistent; existing security/reliability assertions remain; full gate green. No extra approval loop is needed when executing an explicit user-authorized implementation request for this runbook.

### M02 — Prove the platform before migration

**Allowed:** source-controlled `prototypes/WinUiShellProbe/`, a standalone prototype project/test driver not included in release packaging; new research/evidence and exact dependency pins. Production app remains WPF.

1. Start from an official unpackaged WinUI template compatible with .NET 10 and App SDK 2.5.1. Record template source/commit, installed build prerequisites, any additional exact build-tools pin, restore lock and complete build command. Run a compiler probe for each selected API. Do not copy UWP namespaces or assume a similarly named API exists.
2. Display fake 0/1/8-device data, details and pairing in the 376×480 panel. Add preferred rendering variant, opaque fallback, native tray/menu, pin/drag, activation, Esc, bubble and a small UI-thread verification entry point. No real ADB or real credentials.
3. Exercise whole-surface reveal/dismiss, interruptions, negative monitor coordinates, DPI/work-area changes, keyboard, owned menus and cross-process transparent-edge clicks. Verify no taskbar/Alt-Tab entry, no startup flash and no bubble focus theft. Cover actual mouse-down activation, not only Show(false).
4. Publish self-contained x64 to a non-smoke directory and run it without relying on a development-only launch profile. Confirm the UI verification entry point returns a bounded JSON result and a nonzero exit on a forced assertion failure. Record runtime dependencies; a run on this SDK-equipped machine is not clean-machine proof.
5. Measure the prototype using section 8; include a defined 100-snapshots/second fake-feed burst, highest-revision delivery and visible busy indicator. Establish how to obtain a presented-frame trace on this host. Record the selected rendering variant and the current-value retarget API in a short executable proof.

**Pass:** build/launch/test harness/tray/window/input gates pass; no unexplained >50 ms warm interaction stall; resource results show a plausible path to section 8 budgets. For an animated outer surface, current-value reversals are verified; for fallback B, prove immediate show/hide and that no outer-surface animation exists. Run the unchanged production gate too. If presentation measurement is unavailable, record the evidence gap and do not claim M02 fully passed.

**Fail behavior:** retain production; record the exact failing criterion and minimal next experiment. Use section 5's prescribed fallback order. Do not install previews, raise budgets, fake benchmark results or proceed with mass migration.

### M03 — Extract the backend with no behavior changes

**Allowed:** Core project, solution/project references, moved backend/pure policies, AppHost backend composition seam, corresponding tests and boundary paths.

1. Extract Adb, State, Notifications, Diagnostics, clock abstraction and UI-independent Host lifecycles into Core. Keep assembly resources that belong to UI in the app. Review typeof/assembly-resource lookups, internals visibility and static ownership after moves.
2. Separate backend startup/disposal from WPF construction. Expose the existing snapshot stream and explicit command/visibility inputs; use small existing-style interfaces, not a bus or DI framework.
3. Move pure view projection/navigation policies into Core. Keep native Windows API calls out of State and Core; existing BCL process/socket/file operations in the backend retain their current owners. Split mixed tests according to M00; retain their assertions. Core tests must run without initializing WPF/WinUI. During M03 the existing test project may temporarily reference both Core and the WPF executable so old shell/Host assertions still execute; remove that temporary executable reference only after equivalent checks move to the M04+ harness. Do not force old XAML tests into Core to satisfy the final dependency shape prematurely.
4. Keep the WPF product working against Core for this checkpoint. Compare fixture snapshots and backend command/event sequences before/after; preserve notification reconciliation paths that do not publish a new visual snapshot.

**Pass:** every backend regression test and full gate green; existing Release shell launches; fixture semantics unchanged. No ADB policy change hidden in the extraction diff.

### M04 — Establish the production-shaped WinUI shell and test path

**Allowed:** temporary `src/AdbView.WinUiMigration/` using the proven prototype foundation; Core reference; fixture verification code/scripts; build/test integration. This temporary executable is not packaged for users.

1. Use a temporary sibling UI project so WPF and WinUI XAML build tasks are not mixed in one project. Reference Core, not the WPF executable. Use a distinct development mutex/tray identity so tests cannot hijack the installed instance.
2. Add one WinUI Application and one UI dispatcher; all backend work stays asynchronous. Add fixture mode with unique instance suffix and no backend startup, registry writes or real credentials.
3. Add `scripts/test-native-ui.ps1`: accept the candidate executable path explicitly until M10, launch the actual fixture executable, apply scenarios on its XAML UI thread, collect JSON assertions and exit code with a timeout, then dispose only that process. Parse verification/fixture options before normal single-instance forwarding or backend startup. A crash, timeout, missing result or unavailable interactive desktop is a failure/unexercised row, never success.
4. Move XAML-dependent assertions from M00 into this harness. Keep backend/pure policy checks in xUnit. Gate runs both explicitly. Test the harness by intentionally failing a temporary assertion, confirm gate failure, then remove the deliberate failure.

**Pass:** empty/list/details/pairing fixtures launch, focus works, test results are reliable, Core tests and both shell checkpoints still build. Do not bundle this temporary executable into installer/ZIP.

### M05 — Build the stable list and details

**Allowed:** Core presentation policies, WinUI list/detail components, snapshot mailbox/view adapters, fixture/tests.

1. Implement section 3 geometry and scroll policy; make a single window controller the only native bounds writer. Add diagnostic counts for native bounds writes in verification builds.
2. Implement revision rejection, race-safe one-slot dispatch and stable keyed rows. Keep action dispatch independent and lossless. Bind existing vocabulary; no semantic enum-to-text switch in XAML code-behind.
3. Implement list/detail route and keyed scroll/focus restoration. Preserve copy values and device identity across transport changes. Incoming state changes must remain visible while navigation is active.
4. Add scenarios: revisions 20→19, duplicate 20, 21 published during drain/reset, removal while details open, health loss, battery-only update, same-value revision, 0/1/2/8/50 devices, long names, unknown/missing metadata, overflow and 200% text size.

**Pass:** zero native size writes after show for content-only changes; no stale device controls after removal; no lost latest snapshot or command; focus/scroll restoration passes. Do not add motion yet.

### M06 — Port pairing and all existing action paths

**Allowed:** WinUI pairing pages, QR presentation, existing command wiring and fixture/tests. Backend changes only to interfaces required by extraction.

1. Port code entry, QR, submit/cancel/retry, waiting-for-device, error and offer withdrawal. Reuse QRCoder; render a crisp local image/geometry without a browser or new SVG package. Never scale a QR during an animation.
2. Keep stable action locations and code textbox identity while valid; never recreate it on metadata updates. Clear drafts on leave/hide. Disable unavailable actions from current truth and revalidate in backend.
3. Carry existing discovery diagnosis, repair/undo and notification links forward. No new automatic repair. Show an active operation's snapshot wording in the list when returning from pairing.
4. Verify offer replacement, QR→manual operation identity, cancellation completion after navigation, disconnect while waiting, global outage and hidden/reopened active operation. Use fake endpoints and nonsecret fixture QR data.

**Pass:** all mapped pairing/reliability assertions survive, no credentials in logs/artifacts, no false connected state inferred from pairing success, no window size change across phases.

### M07 — Add the shared motion policy

**Allowed:** named controllers/recipes, integration at window/page boundaries, environment subscriptions, verification tests.

1. Implement the exact M02-proven animation/retarget mechanism and selected surface variant. Do not let a theme transition and custom animation write the same property. Root surface motion and inner page motion use distinct containers.
2. Add generation-checked completion and no-op same-target behavior. Test Open→Close→Open, repeated Open, dispose during transition, stale completion after reopen, input during navigation and simultaneous snapshot churn.
3. Implement live reduced motion, static busy status, visible-only age ticks and explicit subscription disposal. Prefer platform focus/hover/press behavior; add Feedback only where needed.
4. Test cancellation at start/middle/end; toggle motion off mid-reveal, navigation and busy; hide/show and suspend/resume; repeat 500 cycles and inspect active handles/clock diagnostics.

**Pass:** section 4 contracts pass on actual WinUI objects; semantic updates never await a transition; hidden presentation has no active clocks. Callback counts alone do not pass the visual gate.

### M08 — Complete native tray, bubble and environment integration

**Allowed:** Host adapters, fixed BubbleWindow, placement/environment integration and related fixtures/manual checks.

1. Replace NotifyIcon with a small CsWin32 adapter: add/modify/delete, version selection, tooltip, mouse and keyboard activation, native menu with existing entries/check states, existing balloon delivery/click behavior, and TaskbarCreated re-registration. Use a hidden top-level notification receiver for required broadcasts; do not assume a message-only HWND receives them.
2. Integrate single instance, startup-silent behavior, preferences, theme, power/session and full-screen suppression. Reuse policies and existing identities where possible; fixture mode uses isolated identities.
3. Verify bubble nonactivation and tooltip, click-to-open, 60-second transient lifetime, permanent states, idle hidden, full-screen suppression and flyout-visible suppression. One-shot lifetime deadlines are not presentation animation clocks.
4. Verify pinned native dragging, mixed-DPI placement, monitor unplug and work-area/taskbar changes. No animation or per-frame window positioning during native drag.

**Pass:** tray/context-menu parity, Explorer recreation, no focus theft/startup flash, keyboard and DPI checks pass; app exits fully with icon removed and backend owners disposed. Explorer restart is disruptive: use a controlled test session and existing authorization, or leave that row unexercised until available.

### M09 — Qualify visual quality, accessibility and performance

**Allowed:** profiler/capture/test tools, fixture improvements and fixes strictly attributable to failed criteria.

Run all of section 8 on the integrated candidate. Diagnose a failed trace before changing durations. Use the simplest owner-correct fix; do not add exceptional animation code to individual controls. Record static captures and motion recordings, the exact executable, OS/display settings and the selected fallback variant. Close every regression in the M00 test map or mark the migration incomplete.

**Pass:** complete required matrix and budgets with evidence. Passing unit tests or a pleasant single recording is insufficient. Hardware rows absent on this machine stay open.

### M10 — Cut over and remove the old shell

**Allowed:** primary app project/bootstrap, move validated WinUI sources into `src/AdbView`, removal of superseded WPF/WinForms shell code, test/solution/build script paths, docs.

1. Switch the main executable to the validated WinUI shell and production single-instance identity. Remove WPF and WinForms references and the temporary migration project. Keep exactly Core, app and tests as permanent projects.
2. Remove the retired mechanisms in section 6. Replace each obsolete WPF-mechanism assertion using its M00 mapping. Do not delete a behavior test because the old class disappeared.
3. Update source-boundary scans to cover both Core and app, not just the old root. Assert no WPF/WinForms dependency, browser surface, application frame timer, forbidden ADB operation or semantic wording ownership drift.
4. Keep the tiny prototype only as an explicitly nonshipping experiment or remove it after its proof is captured; no release build includes it. Re-run full verification and launch the actual Release exe from a non-smoke directory.

**Pass:** one production shell, no legacy geometry writers or dead animation tokens, all old behaviors mapped, full gate green, actual production-identity UI launched successfully.

### M11 — Package and test deployment

**Allowed:** package/test/install/capture scripts, installer, CI, release documentation and exact build/dependency manifests.

1. Publish with `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `.NET SelfContained=true`, explicit RID and `PublishSingleFile=false`. Use the tested stable template's initialization; do not combine incompatible framework-bootstrap and self-contained initialization by guesswork. Disable trimming/AOT unless separately proven; do not add them during this migration.
2. Update `scripts/package.ps1`, `test-package.ps1`, `test-install-acceptance.ps1`, `installer/AdbView.iss`, CI and capture paths for the new TFM and full dependency tree. Package native files, resource indexes and assets from verified output; installer/ZIP must not assume one EXE.
3. Produce x64 and ARM64 artifacts; inspect architecture and missing dependencies. Run x64 on a clean supported Windows 11 machine without developer tooling or preinstalled App SDK. Test portable launch, install, upgrade from the existing WPF build, single instance, Run/startup-silent, uninstall and stale-file cleanup using exact install-directory bounds.
4. A headless publish smoke proves only startup. Add a separately reported headed launch/fixture gate; update the old smoke log assertion so this run's process/identity and successful exit are proven rather than matching an old log line. Keep headless CI and headed acceptance results distinct.

**Pass:** full gate, package inventory and real x64 deployment checks pass. ARM64 build success is not runtime proof; leave ARM64 execution open if hardware is unavailable. Do not replace the user's installation or publish artifacts as a release without the corresponding request.

### M12 — Reliability regression and handoff

**Allowed:** existing device/soak procedures, final results/status/docs; fixes only for demonstrated migration regressions.

1. Run the entire fake-server/reliability suite after UI cutover. Exercise real disconnect/reconnect, wireless changes, pairing, sleep/wake and pinned visibility against `DEVICE-TEST-MATRIX.md` on available hardware. Never kill or reconfigure shared ADB as a convenience test.
2. Run the existing release soak requirement (24-hour pinned run) and a hidden idle interval, recording memory/handles/CPU and notification correctness. Preserve rows already unverified before migration.
3. Final report: exact new behavior, chosen rendering variant, version pins, tests and traces, package status, remaining unexercised rows and executable location. Do not declare public-release readiness while mandatory hardware/soak/deployment rows remain open.

**Pass:** all required rows evidenced; otherwise report implementation status and named acceptance gaps accurately.

## 8. Measurement and acceptance matrix

These thresholds are proposed engineering acceptance criteria. They are not established performance facts or universal OS guarantees. Use Release builds without a debugger, the same machine/power settings for comparisons, and a repeatable fixture workload. Record OS build, GPU/driver, display refresh, DPI, text scale, remote/local session, material variant and foreground/background load.

| Area | Procedure and required result |
| --- | --- |
| Fixed geometry | Record native client/outer bounds during 100 list/detail/pairing/state transitions. No content-driven size or anchor change; settled anchor error <=1 physical pixel. Environment changes are identified separately. |
| Warm responsiveness | 100 interactions per scenario. p95 first useful visible frame <120 ms after open input; meaningful snapshot state visible <250 ms from publication. Focus/hit targets ready without waiting for animation completion. |
| Motion at 60 Hz | Use ETW/WPR/WPA, PresentMon where applicable, or another validated presentation instrument plus recording. On active transition intervals: p95 presented-frame interval <=20 ms, p99 <=33.4 ms, no unexplained >50 ms stall. Count actual presented changes; idle periods are excluded, not classified as dropped frames. |
| Faster displays | Test available 120/144 Hz display; report the refresh-specific interval and missed refreshes. Do not claim refresh matching from a 60 Hz trace. No invented PASS if unavailable. |
| Cold startup | At least 10 fresh process launches; identify OS-cache conditions. Report process→tray-ready and process→first useful flyout separately. Proposed p95 tray-ready <=1 second; no empty flash. Warming the process then hiding it is not a cold-launch test. |
| Interruption | 25/50/90% reversals, repeated toggles and 100-snapshot/s bursts for 10 seconds. No endpoint jump, stale completion, old snapshot, queued navigation backlog or lost user command. Latest snapshot drains after the burst. |
| Hidden idle | Ten-minute stable-device run after settling. Mean CPU <0.1% of one logical core (process CPU seconds / wall seconds ×100); no device-directed metadata requests while hidden, no age/progress/presentation clocks. Discovery keepalive/backoff work retains its documented backend policy. |
| Memory/lifetime | Owner-approved 2026-10-15: complete WinUI app working set ≤200 MiB (209,715,200 bytes), including the backend; record private bytes too. Compare settled memory/handles before and after 500 cycles: investigate >10 MB private-byte growth or >10 handles, and any continuing upward trend. Never force GC only to produce a passing report. Full integrated app must pass; an empty prototype cannot prove this. |
| Material/input | Light/dark, transparency off, contrast themes, desktop/background variations, rounded corners/shadow, cross-process edge clicks. No opaque host rectangle behind a moving panel or text/QR blur introduced by scaling. |
| Layout/accessibility | 100/125/150/175/200% DPI; 100/150/200% text size; available mixed monitors; small work area; 0/1/2/8/50 devices; long names; all health/pairing fixtures. Tab/Shift-Tab/Enter/Space/Esc/Back, focus restoration, Narrator/UIA labels and high contrast pass. |
| Lifetime | Preference changes at startup and in flight, hide/reopen, suspend/resume, monitor removal, owned menus, Explorer recreation, shutdown during pairing/motion. No hidden clock, orphan callback, zombie icon or stolen focus. |

Capture static screenshots with the updated `scripts/capture-native.ps1 -Fixture <actual-fixture-name>` and record Windows build. Static captures verify layout and appearance; recordings and presentation traces verify motion. If a profiler cannot attribute WinUI compositor presentation on this host, explain that limitation and use a validated alternative; UI timer timestamps do not replace presentation evidence.

Do not silently lower thresholds. If WinUI cannot meet the product's memory/startup budget, stop before committing to the migration and report the measured tradeoff with the stable-size WPF alternative.

## 9. Commands, checkpoints and completion

Run from the repository root using PowerShell. The existing gate locates the pinned SDK itself:

```powershell
& .\scripts\verify.ps1
git diff --check
```

M02 records the prototype's proven build/publish commands. M04 adds `scripts/test-native-ui.ps1`; M11 updates package commands and their parameter documentation. Do not run nonexistent scripts or paste an invented WinUI initialization recipe from this plan. Preserve native exit codes when redirecting output. For background helpers use hidden windows; show product windows intentionally for headed acceptance.

Update `docs/WINUI-REBUILD-STATUS.md` after each checkpoint:

```text
Current package: Mxx
Status: NOT STARTED / IN PROGRESS / PASS / FAIL / NOT EXERCISED
Baseline identity and working-tree changes preserved:
Selected surface variant and exact package/toolchain pins:
Files changed for this checkpoint:
Assertions retained/replaced (test-map IDs):
Commands, exit codes, test counts:
Artifacts / captures / traces / machine conditions:
Observed failures and fixes:
Remaining required checks and reason unavailable:
Next exact step:
```

Before ending a work session, leave this checkpoint sufficient for another model to resume without rereading the entire conversation. Never label a work package PASS because the next package has started. Separate implementation complete, verified on this machine, clean-machine validated, real-device validated and release-ready.

## 10. Independent judgment review of this plan

- The evidence establishes defects in this implementation, not an inability of WPF to animate well. Stable geometry is the highest-confidence improvement; changing toolkit is a conditional choice.
- A fresh WinUI shell creates migration, native tray and deployment risk. M02 tests those assumptions before production moves; M03 isolates backend behavior from presentation work.
- One universal policy does not mean forcing every platform control through custom animation. The design removes most structural transitions and centralizes only the remaining app-owned ones.
- The exact product dimensions, navigation model and thresholds are explicit proposals. Official docs support the mechanisms, not these product-specific numbers.
- No callable “judgement hook” was exposed by the available tools or found in the inspected repository instructions. This review is an evidence-based self-review, not a claimed hook execution or external approval.



