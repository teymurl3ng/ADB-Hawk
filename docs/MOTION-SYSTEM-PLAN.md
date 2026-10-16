# Unified motion system: audit and replacement plan

> **Superseded implementation direction (2026-10-15):** follow [WINUI-REBUILD-RUNBOOK.md](WINUI-REBUILD-RUNBOOK.md) and decision 0029 for the owner's subsequent unconstrained redesign request. The current-shell audit and probe evidence below remain valid; do not implement this earlier WPF replacement proposal alongside the new plan.

Date: 2026-10-15. Status: analysis and implementation plan; replacement not implemented.

## Recommendation

Replace the independent native-window slide, self-sizing callback, local XAML storyboards, and bubble resizing with one presentation system. Share policy, interruption rules, layout transactions, visibility lifecycle, and reduced-motion behavior across the two existing windows. Keep WPF, the canonical cards, backend snapshots, and the current product scope.

The principal problem is not an incorrect easing function. A timer moves an HWND, WPF independently animates details and controls, and layout changes instantly resize the HWND. These mechanisms have different scheduling, cancellation, and size rules. The current contract deliberately mandates some of the abruptness.

The replacement should animate the visible surface and surviving elements inside a stable native host. Do not animate HWND dimensions on every frame, scale text, add springs, or hide stale data behind outgoing animations. A rendering prototype must first prove that the surface's outside edge, shadow, hit testing, and text remain correct. An ordinary opaque WPF window cannot achieve this merely by translating its children.

## Evidence and confidence

Reviewed the current working tree, including the preceding reliability repairs, rather than assuming previous audit conclusions are correct. Relevant implementation is in `Host/AppHost.cs`, `Host/FlyoutRevealAnimator.cs`, `Host/ShellSizing.cs`, and `NativeUi/`. The app running during this audit was the repository Release executable, PID 23260, with the startup identity `built=2026-10-15 01:27`. Windows is build 26100; SDK is 10.0.401 and Windows Desktop runtime is 10.0.12.

Existing logs from that session show:

| Local time, 2026-10-15 | Requested transition | Applied frames | Worst callback gap |
| --- | --- | ---: | ---: |
| 09:15:46 | Open, 167 ms | 3 | 141 ms |
| 09:15:59 | Open, 167 ms | 2 | 328 ms |
| 09:16:03 | Open, 167 ms | 10 | 16 ms |
| 09:15:53 | Close, 83 ms | 5 | 16 ms |
| 09:16:07 | Close, 83 ms | 4 | 31 ms |

The logs also record height steps from 275 to 488 DIP and back. These are real geometry discontinuities. Callback gaps corroborate scheduling stalls, but are not measurements of monitor-presented frames. They do not identify whether a particular stall came from binding/layout, GC, cold initialization, OS scheduling, or another dispatcher task. No claim is made that every action stutters or that all stalls share one cause.

An isolated, non-visible WPF harness exercised the actual Release controls, with no AppHost, device access, or tray instance. It verified:

- Existing device-card timelines still contain 167/83 ms durations after the production motion gate swaps the resource token to zero.
- After `SetSpinnerEnabled(false)`, the actual spinner remains animated; its angle advances from 0 to approximately 46.7 degrees across a 100 ms dispatcher-pump interval.
- Applying revision 20 empty followed by revision 19 connected restores a device in the view model. There is no presentation revision guard. This is a missing guard, not proof that the production backend currently delivers revisions out of order.
- A constructed, never-shown shell view model has its age timer enabled.

Reproduction: `artifacts/motion-audit-2026-10-15/run-probe.ps1`, `MotionProbe.cs`, and `probe-results.txt`. The harness redirects WPF resource lookup to the application assembly because it runs in a separate executable; production code is unmodified. Timing excerpt: `current-build-motion.log`. Artifacts are local and ignored by Git.

## Findings

| ID | Finding and effect | Evidence | Confidence |
| --- | --- | --- | --- |
| M01 | The slide has genuinely uneven callback delivery. Its 10 ms dispatcher interval is not a frame clock or a scheduling guarantee. Late callbacks jump forward because progress uses elapsed time. | `NativeUi/DispatcherTimerTickSource.cs:32`; `Host/FlyoutRevealAnimator.cs:163`; current-session log above. | Observed stalls; exact blocking work needs profiling. |
| M02 | The entire window travels its full height plus the tray gap in 167/83 ms. A taller flyout moves much farther in the same duration, amplifying the distance skipped during a missed callback. | `Host/AppHost.cs:876`, `ShowNativeFlyout`, `HideNativeFlyout`. | Confirmed implementation; perceived speed is a design judgment. |
| M03 | Expansion/collapse is a hard height step followed by a detail fade/chevron rotation. The shell edge and neighboring content jump before the feedback settles. | `NativeUi/Cards/DeviceCardView.xaml:120`; `NativeFlyoutWindow.xaml.cs:208`; `DESIGN.md` §§24–25. | Confirmed, currently required by design. |
| M04 | Content sizing is suppressed during a reveal, then remeasured and stepped at completion. A size-changing update during opening therefore creates a second motion: slide, then snap. Startup also begins with a 500 DIP placeholder. | `NativeFlyoutWindow.xaml.cs:213`; `Host/AppHost.cs:623`, `767`. | Confirmed conditional path; not every show takes it. |
| M05 | `LayoutUpdated` performs an unconstrained measure of the live shell; resizing then calls `SetWindowPos` and sets WPF Height/Top. Deduplication avoids many redundant native writes, but not the remeasurement or constraint changes. | `NativeFlyoutWindow.xaml.cs:87`, `231`, `274`, `289`. | Confirmed extra work/ownership complexity; sustained oscillation not established. |
| M06 | Geometry ownership is only partially centralized. The reveal, self-size callback, initial placement, and DPI handler can write bounds. DPI adopts a new rectangle without invalidating the old slide endpoints. | `NativeFlyoutWindow.xaml.cs:146`, `274`, `469`; `Host/AppHost.cs:744`. | Confirmed competing paths; mixed-DPI artifact requires headed reproduction. |
| M07 | Presentation snapshots are queued individually through multiple dispatcher calls; there is no latest-only mailbox or UI revision guard. Every device emits its full property notification set on every snapshot. | `Host/AppHost.cs:536`, `980`, `1001`; `NativeShellViewModel.cs:188`, `414`, `570`, `805`; revision probe. | Confirmed work amplification; contribution to a given stall unmeasured. |
| M08 | Reduced motion does not reach already-created local card timelines. Swapping Controls dictionaries changes implicit control styles but not durations captured by card-local `StaticResource` animations. | `NativeFlyoutWindow.xaml.cs:351`; `DeviceCardView.xaml:65`, `131`, `177`; control probe. | Reproduced. |
| M09 | The spinner has an enter-only forever storyboard. Setting its enable flag false does not remove the clock. Its flag also expresses motion preference rather than progress AND visibility. | `Shell/PairingCardView.xaml:146`; `Shell/CardViewModels.cs:81`; control probe. | Reproduced cancellation defect; hidden work must be measured separately. |
| M10 | Changing reduced motion does not finish an already-running native reveal. `CompleteImmediately` exists but has no production caller. | `Host/AppHost.cs:1223`; `Host/FlyoutRevealAnimator.cs:137`. | Confirmed missing connection. |
| M11 | The one-second age timer starts in the view-model constructor and is stopped only by disposal. Hiding the flyout does not stop it. It can update bindings while invisible. | `NativeShellViewModel.cs:69`, `474`, `Dispose`. | Reproduced enabled timer; CPU impact not quantified. |
| M12 | Several fades explicitly restart at opacity zero; collapse removes details immediately. Rapid reversals therefore do not use a consistent current-value policy. Open/close restarts a full-duration curve from the current integer rectangle, preserving position but not velocity. | `DeviceCardView.xaml:131`; `PairingCardView.xaml:89`, `124`; `FlyoutRevealAnimator.cs:89`; `AppHost.cs:659`, `817`. | Confirmed interruption semantics. |
| M13 | The bubble has a separate instant `SizeToContent` resize followed by reanchoring in `SizeChanged`. Its background window remains opaque while only its child content fades. | `StatusBubbleWindow.xaml.cs:81`, `172`, `212`, `228`; `StatusBubbleWindow.xaml`. | Confirmed mismatch; no new bubble frame capture in this audit. |
| M14 | Auto scrollbar visibility has no reserved gutter in the shell. The custom scrollbar's 8 DIP width does not reserve space while it is collapsed. Crossing the height limit can therefore change content width and text wrapping. | `Shell/ShellView.xaml:44`; `Themes/Controls.xaml:90`. | Confirmed configuration; wrapping impact depends on content. |
| M15 | Self-sizing always freezes the bottom edge and uses the tray monitor's work-area top. That does not express top-edge anchoring or a pinned window dragged to another monitor. The minimum height can also exceed available space when less than 160 DIP remains. | `Host/ShellSizing.cs:35`; `NativeFlyoutWindow.xaml.cs:240`. | Confirmed math; extended taskbar/mixed-monitor scenarios not exercised here. |
| M16 | Existing tests mainly assert view-model flags, dictionary text, or final fake-tick positions. They miss actual clock cancellation, rendered pacing, and combined open/resize/state sequences. | `ReducedMotionDictionaryTests`, `FlyoutRevealAnimatorTests`, `NativeShellViewModelTests`; probe findings despite green gate. | Confirmed test coverage gap. |

Additional retirement concern: the old animator's equal-endpoint branch does not cancel an already-running flight, completion delegates can accumulate, and `CompleteImmediately` does not synchronize `LastApplied`. These are real helper lifecycle holes, but normal AppHost open/close cancels before starting a new flight. Do not present them as the proven cause of the user's current jitter. Replace those semantics with a generation-owned completion model instead of preserving the old helper API.

## What should remain

The current easing is shared through `BezierEase` and `Easing`; it is not independently drifting between XAML and C#. Device card instances are generally retained by identity. Diagnostic collection already runs off the UI thread. A good opening has been logged at 10 frames/16 ms. These facts argue against blaming all animation code, all WPF rendering, or ADB itself.

Previous notes use Microsoft's warning about shadowed `AnimateWindow` calls as an explanation. This app uses `SetWindowPos`, not `AnimateWindow`. That warning is useful background, not proof that DWM shadows caused these stalls. Likewise, the old composition-clock experiment is historical evidence, not a guarantee that another clock swap will solve the present problem.

## Intended feel

- Respond immediately to input, then settle quietly. No bounce, overshoot, pulsing, or stretched text.
- Open with a short tray-directed displacement and a surface fade; dismiss by reversing that gesture. Travel should not increase with device count.
- Expansion, sibling movement, the shell edge, detail reveal, and chevron should read as one event.
- New interaction takes over immediately at the current visible position. Repeated identical targets do not restart a transition.
- A device becoming unavailable changes its text/actions immediately. Motion must never preserve a false Connected card or a stale QR code.
- Reduced motion applies to every running and future channel in both windows, without rebuilding templates.

Proposed initial policy, to be evaluated in the prototype: 0 ms semantic updates; 83 ms feedback/dismiss; 167 ms reveal/reflow/disclosure; leave 333 ms reserved. Use a shared 12 DIP reveal-distance token instead of full-window travel. Preserve the existing nominal point-to-point curve. An interruption may use the remaining portion of a token's timeline; avoid a fresh full-duration tail for a nearly-complete transition. Preserve position and opacity exactly at takeover; preserve velocity only when compatible with the new direction and bounded motion. Reversal must follow the new intent without overshooting. Do not promise perfect velocity continuity and immediate reversal simultaneously.

## One system, bounded responsibilities

Proposed components stay within the existing application project:

1. **`NativeUi/Motion/MotionPolicy`** owns durations, travel distance, easing, interruption rules, and effective reduced-motion/visibility policy. Controls request semantic effects, not arbitrary durations.
2. **`NativeUi/Motion/MotionRuntime`** owns active WPF clocks and their lifetime, target identity, generation, replacement, cancellation, and cleanup. One logical transaction starts related channels together. It uses WPF's timing/rendering facilities; no independent dispatcher timer for HWND movement. A rendering callback, if required for measurement, exists only while useful and does not become an idle loop.
3. **`NativeUi/Motion/MotionLayout`** owns measurement transactions and maps stable element identities from current displayed rectangles to target rectangles. A snapshot/input batch is applied, target geometry is measured at the stable width, and surviving content moves using render translations and clips. Text and QR pixels are never scaled. Per-frame tree remeasurement is excluded.
4. **`NativeUi/Motion/MotionSurface`** owns the visible outline, background, clipping, and shadow inside a native host. Its flyout and bubble instances use the same lifecycle engine. A small native adapter alone commits host bounds/monitor changes; components never call `SetWindowPos`.

This is a small application-specific system, not a generic animation library, new UI project, framework migration, or message bus. Existing card templates remain the visual authority. No ADB knowledge enters these classes.

Flow: latest UI snapshot/input → apply current semantic state → one layout transaction → one motion transaction → settle and release clocks. Intermediate presentation coordinates are temporary rendering state, not a second copy of device truth.

## The surface decision must be proved first

Preferred prototype: one transparent WPF host per existing window with an opaque, clipped visual surface inside it. Its native bounds cover only the bounded motion envelope, including the union of current/target geometry, reveal travel, and shadow. Bounds can change at transaction boundaries; they stay fixed during a flight. No full-screen overlay and no third window.

The surface outline changes through geometry/clip animation. Target content is measured once; stable elements receive render translations from their current displayed rectangles. The same timeline drives the outline and the content. On growth, reserve the required envelope before rendering; on shrink, release excess space after completion. Interruption expands the envelope only if necessary and rebases coordinates so no visible pixel teleports. DPI/work-area changes cancel/rebase the transaction atomically instead of writing through another owner's old coordinates.

This requires `AllowsTransparency`/per-pixel transparency or an equivalently demonstrated composition solution. It is not obtained by setting a brush to Transparent on the existing opaque HWND. It also changes native corner/shadow ownership: Microsoft documents that per-pixel alpha windows do not receive DWM corner rounding. The prototype must render the approved outline/shadow itself and verify text quality, mouse behavior, and rendering cost. Do not assume this path is automatically hardware-fast, or automatically software-only, on the current runtime.

Required spike acceptance: correct visible silhouette; no opaque rectangular flash; no clipped shadow; no click interception outside the visible surface/shadow policy; no invisible interactive controls; pinned native dragging and nonactivating bubble preserved; focus and keyboard navigation correct during motion; 100–200% DPI text/QR sharpness; satisfactory frame and idle budgets. Include software rendering/RDP as degraded environments. A software-rendering fallback may settle instantly through the same coordinator.

If the surface prototype fails these requirements, do not layer compensating patches onto it. Record the failure and evaluate a native composition host as a separate architecture decision, or explicitly retain instant geometry as a degraded result. A fixed oversized opaque window with animated children is not an acceptable substitute: the rectangle would still pop to its final size. DirectComposition/WinUI is not a dependency to introduce speculatively.

## Behavior coverage

| Action | Shared-system behavior |
| --- | --- |
| First open / reopen | Prepare target layout and host geometry before visibility; short translate + entire-surface fade; no 500 DIP visible placeholder or end-of-open size snap. |
| Escape / light dismiss / tray toggle | One close intent and generation-owned completion; reopening cancels that completion and takes over at the sampled presentation value. Define target visibility separately from native visibility so the bubble/notifications transition consistently. |
| Expand / collapse / switch expanded device | One disclosure/reflow transaction, including both affected cards, siblings, shell outline, clip, and chevron. User collapse can clip still-valid details; device removal cannot retain stale semantic content. |
| Arrival / removal / metadata rows | Latest data immediately; new content may reveal, removed content/actions disappear immediately, surviving content and empty space settle smoothly. Group a single snapshot's changes. |
| Pairing offer/code/progress/success/failure; QR; repair/health cards | Same region/reflow mechanism, stable identities, current wording/actions immediate, input focus preserved. Keep existing authorized success dwell separate from animation; never introduce a stale-state animation queue. |
| Hover / press / pin / copy feedback | Shared current-value transitions; reserve feedback space so labels do not push values around. Pin truth still comes from the snapshot. Repeated commands do not restart identical targets. |
| Busy spinner | Dedicated centrally registered busy effect, enabled only while busy AND visible AND motion-enabled; remove its clock immediately otherwise. Explicitly document its current 850 ms linear period as a busy-indicator exception instead of pretending it is a structural token. |
| Overflow / scroll | Fixed gutter and content width; 560 DIP maximum; preserve logical scroll anchor across reflow; do not animate wheel scrolling through a second smoothing loop. |
| Bubble hover / status / appearance | Same surface/reflow engine with its own anchor and nonactivation policy. Status change does not re-fade unchanged content or create resize-then-reanchor hops. |
| Theme / reduced motion / hide / suspend / shutdown | One live policy event cancels or settles every owned clock; hidden views stop age updates and busy motion; resume renders the latest target without replay. |
| Drag / DPI / display / work area | Native dragging remains OS-owned; coordinator suspends geometry motion during drag, then rebases on the current monitor. DPI invalidates measurements and trajectories together. |

## Execution sequence

1. **Record baseline and amend the motion contract.** Adopt proposed decision 0028 and update the frozen contract through its required process before production implementation. Scope is replacement of presentation mechanics, not a new product phase or a claim that Phase 11 is complete. Add the motion validation matrix. Record executable identity, monitor refresh/DPI, render tier, cold/warm runs, and per-operation traces. Profile the 141/328 ms class of stall with dispatcher/layout/GC/render timings; callbacks alone are insufficient.
2. **Prove `MotionSurface` in isolation.** Use existing fixtures and two-window roles; exercise open/close and 275↔488 DIP disclosure before any broad migration. Establish input bounds, outline/shadow, quality and performance. Choose the backend once this passes. This is the highest-risk step and prevents another series of local workarounds.
3. **Build runtime and transactional layout together.** Test fake-clock interpolation, same-target no-op, equal-endpoint settlement, current-value retargeting, completion generations, no clock accumulation, and policy cancellation. Introduce one latest-only UI mailbox, a revision guard, field-level notifications, and measurement invalidation for real geometry changes. Keep backend event delivery/notifications intact; coalesce only replaceable UI snapshots.
4. **Migrate the flyout as a complete surface.** Move show/hide, size, anchoring, disclosure, card/region reflow, scroll gutter, and DPI handling onto the coordinator. Delete the old `LayoutUpdated` self-size ownership and native reveal path once parity is proven. No element may retain both old and new geometry owners.
5. **Migrate all remaining effects and the bubble.** Replace per-site timelines with shared requests for hover/press/copy, pairing feedback and spinner; replace bubble SizeToContent/reanchor motion. Remove the reduced-controls clone after all consumers obey the live gate. Remove unused tick sources, general bounds animator, and old callback composition. One shared system must remain at completion.
6. **Validate and deliver the actual build.** Run the full gate; capture still fixtures for visual parity and frame sequences for motion. Launch the Release executable from a non-smoke path; verify its identity. Only install/update the user's deployed copy within an authorized installation step. Record all unexercised hardware/performance rows explicitly.

Each increment must remain runnable and pass applicable tests. Temporary migration seams are implementation scaffolding, not separate final animation systems. Do not refactor the connection/discovery backend in this work.

## Acceptance criteria

- No full-height native slide, per-frame HWND resize, end-of-open height pop, stretched text, reset-to-zero flicker, stale completion hiding a reopened window, or simultaneous geometry writers.
- Latest state is rendered without waiting for animation; out-of-order UI revisions rejected; only one pending replaceable UI snapshot; no lost user commands or backend events.
- Runtime WPF tests prove that all in-flight animations, including existing card-local effects and spinner, stop/settle when motion is disabled; hidden surfaces have no active presentation clocks or age ticker.
- A frozen target entails one target-layout transaction, not a new measure pass on every motion frame. Additional real metadata/DPI changes may retarget; a no-op snapshot may not.
- After settlement, the preserved anchor is within one physical pixel and no geometry alternation continues. Stable width through scrollbar appearance. Correct clamping on small work areas and the monitor where a pinned window actually sits.
- Test interruptions at start, middle, and completion: repeated tray toggles; expand/collapse; switch cards; remove expanded device; health/QR/pairing transitions; theme/motion preference; DPI/display change; hide/suspend; shutdown. Assert both visual and accessible/hit-test state.
- Proposed performance targets for a controlled 60 Hz run: p95 presented-frame interval ≤20 ms, p99 ≤33.4 ms, no unexplained >50 ms stall, input response within the next two refresh opportunities, and settlement within the nominal transition deadline plus at most two refresh opportunities. Report cold-start separately; first useful flyout frame still targets the existing <120 ms budget and meaningful state <250 ms. These are targets, not current results or universal OS guarantees.
- Run at least 100 scripted interactions per main scenario, with both quiet and defined snapshot-burst load. At 120/144 Hz, record actual presented cadence against that display's frame budget; do not label a fixed 60 Hz cadence refresh-matched. The rendering spike must establish supported refresh behavior before promising it.
- Test 100/125/150/175/200% DPI, available mixed-DPI monitors, reduced motion at startup and mid-flight, light/dark/forced colors, overflow, 1/2/8 devices, long text, opaque content, and nonactivating bubble. Real hardware rows remain open if unavailable.
- Preserve the app's existing idle/memory budgets and near-zero hidden idle activity. Confirm renderer/clock resources do not grow across repeated transitions.
- Static captures prove geometry/theme parity only. Frame traces/recordings prove temporal behavior; callback counts alone cannot prove smooth presentation.

## Status of this task

Completed: code audit, current-build log review, isolated control reproductions, official documentation review, this plan, research note, and proposed contract decision. `scripts/verify.ps1` passed: 1,010 tests, zero failures/skips, warnings-as-errors Release build, formatting, source boundaries, and publish smoke.

Not done: motion-system implementation, contract modification, new live animation capture, profiling attribution of the long stalls, rendering-backend prototype, or installation. The current rendered shell was not changed. The earlier reliability work remains intact; its passing tests do not establish animation quality.
