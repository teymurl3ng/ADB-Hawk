# Animation follow-up: reveal retargeting, position-before-show, appear-only fades, and the reduced-motion spinner gate

- **Date:** 2026-10-10
- **Author:** agent (opencode)
- **Phase:** post-Phase-12 hardening (native shell)
- **Status:** current

## Problem

After decision 0023 (single-writer bounds, composition-clock slide, frozen curve in XAML), the owner
still reports jitter with a "disappearing and reappearing" quality. A full animation audit of the
native shell found five remaining defects, all in presentation behaviour:

1. `AppHost.ShowNativeFlyout` always re-places the window at the off-tray slide origin before the
   open slide. When the window is already visible — a re-open during an in-flight close slide, or
   `OnDisplayChanged` re-running the show path on a visible flyout — the window is teleported fully
   behind the taskbar and then slides back in: a visible disappear/reappear. The close path already
   retargets from current bounds; the open path does not.
2. `ShowNativeFlyout` calls `window.Show()` before positioning. On a reopen after a reduced-motion
   hide the window sits hidden at its final on-screen position, so it becomes visible there before
   being yanked behind the tray; the layout pass inside `Show()` also runs the self-size step at the
   old position before the animator starts. On first show the handle does not exist before `Show()`,
   so the source is created at the OS cascade position.
3. `StatusBubbleWindow.Apply` resets `Shell.Opacity = 0` and re-fades on every call. The presenter
   fires `Changed` whenever the `BubbleState` record changes — and the record includes the tooltip
   `Detail` text — so every status-text change while the bubble is visible blinks it off and on.
   The fade is also linear (no `BezierEase`) and reads the app-level `Motion.Feedback` token, which
   the reduced-motion gate never swaps, so the bubble animates under OS reduced motion.
4. The pairing spinner (`PairingCardView.xaml`) is a hardcoded 850 ms `RepeatBehavior="Forever"`
   linear loop — not covered by any `Motion.*` token, so the `ControlsReduced.xaml` dictionary swap
   cannot reach it, and it keeps spinning under OS reduced motion (decision 0023 recorded this as a
   separate defect).
5. `Controls.xaml` `RowCardButton`'s hover `Overlay` has no initial `Opacity="0"` (the `Button`
   template's layers do), so the first hover-in animates 1→1 (a no-op pop) while later hovers fade;
   the exit fade animates a `Transparent` background, so hover-out snaps.

This note establishes the official basis for the fixes and records the rejected alternatives. It is
required by R-10 before touching presentation behaviour.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| `ShowWindow` function | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow | ms.date 2023-06-06, updated 2025-07-01 |
| `SetWindowPos` function | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos | ms.date 2018-12-05, updated 2025-07-01 |
| `AnimateWindow` function | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-animatewindow | ms.date 2018-12-05 |
| `Window.WindowStartupLocation` property | https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.windowstartuplocation | ms.date 2025-07-01, updated 2026-05-27 |
| `Window.Left` property | https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.left | ms.date 2025-07-01, updated 2026-05-27 |
| `Window.Show` method | https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.show | windowsdesktop-10.0 |
| WCAG 2.2 Understanding SC 2.2.2 Pause, Stop, Hide | https://www.w3.org/WAI/WCAG22/Understanding/pause-stop-hide.html | updated 2026-08-10 |
| WCAG 2.2 Understanding SC 2.3.3 Animation from Interactions | https://www.w3.org/WAI/WCAG22/Understanding/animation-from-interactions.html | WCAG 2.2 |
| Motion in Windows (Fluent durations) | https://learn.microsoft.com/en-us/windows/apps/design/motion/motion-in-windows | cited in research/2026-10-04-motion-system.md |
| `CompositionTarget.Rendering` event | https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.compositiontarget.rendering | cited in research/2026-10-07-animation-jitter-compositor-alignment.md |

## Version investigated

- .NET SDK 10.0.401, `net10.0-windows` (the pinned toolchain; `windowsdesktop-10.0` docs moniker).
- Windows 11, OS build 10.0.26100 (the machine under test).
- Target app: `AdbView` 0.1.0, native WPF shell only (decision 0022), post decision 0023.

## Findings

Each finding is traceable to a row above. Where the documentation is silent, that is stated
explicitly; implementation findings observed in this repository are labelled as such.

**F1 · `ShowWindow` displays a window "in its current size and position" — positioning while hidden,
then showing, is the documented order.** `ShowWindow` (SW_SHOW / SW_SHOWNA): *"Displays the window
in its current size and position."* The Remarks document the create-hidden-then-show pattern:
*"Applications create their main window by calling `CreateWindow` … with the `WS_VISIBLE` flag
cleared, and later call `ShowWindow` with the `SW_SHOW` flag set to make it visible."* A hidden
window therefore keeps whatever rect it was given; making it visible is a pure visibility change.
Consequence: a window that must not be seen at its old position has to be *moved before* it is
shown. `SetWindowPos` operates on hidden windows (its contract is position/size/visibility of an
existing HWND, visible or not). This is the official basis for fix 2's reorder.

**F2 · WPF `WindowStartupLocation.Manual` positions the window from `Left`/`Top` when first shown.**
`Window.WindowStartupLocation` Remarks: *"Setting the `WindowStartupLocation` property to `Manual`
causes a window to be positioned according to its `Left` and `Top` property values. If either the
`Left` or `Top` properties aren't specified, their values are determined by Windows."* `Window.Left`
documents logical units (1/96 inch) and that an unspecified value (`NaN`) is the system default.
Consequence: on the first-ever show — where the HWND does not exist until `Show()` creates it and
`SetWindowPos` cannot run — pre-seeding `Left`/`Top`/`Width`/`Height` (in DIP) places the source at
the reveal origin instead of the Windows-determined cascade position. This is the official basis
for fix 2's first-show pre-seed.

**F3 · `Window.Show()` on a hidden WPF window re-shows it at its current rect.** Implementation
finding (observed here, consistent with F1/F2): WPF `Show()` creates the `HwndSource` on first show
and otherwise issues a plain show; it does not re-apply stale logical geometry, so the OS rect set
while hidden is what the user sees. The flash in the current code comes from showing *before*
positioning, not from `Show()` itself.

**F4 · A retargeted open must start from the last applied frame, not from the reveal origin.**
DESIGN §26: *"If backend state changes during an animation: cancel/retarget current animation;
converge on latest AppSnapshot"* and *"No animation may delay current truth."* R-06: *"If a
snapshot arrives mid-animation, the animation retargets."* The close path already does this
(`HideNativeFlyout` slides from `window.GetPhysicalBounds()`); the open path snapping to the
off-tray origin on a visible window contradicts the same rule in the opposite direction — the
window's *position* is truth-adjacent presentation, and a snap is a visible teleport. This is a
contract reading, not a new rule.

**F5 · The bubble's fade is an appear transition, and decision 0021 records it as one.**
Decision 0021: *"status bubble content fade-in (83 ms) on appear (window-level fades need the
banned layered path; content opacity is free)"*. The implementation faded on *every* `Apply`,
which the record equality of `BubbleState` (a record including `Detail`) turns into a blink on every
tooltip change. Aligning the code with the recorded intent is a defect fix, not a contract change.

**F6 · The bubble fade must carry the frozen curve and respect the reduced-motion gate.** DESIGN
§24: *"Every production animation uses this curve"* and *"OS reduced-motion … reduces every one
of these to the instant behavior."* The code-behind fade had no easing function (linear) and read
`Application.Current.Resources["Motion.Feedback"]` — the app-level token. The reduced-motion gate
(`NativeFlyoutWindow.ApplyMotionGate`) swaps only the flyout window's nested `Controls.xaml`;
`SwapAppThemeDictionary` re-creates the app-level `FluentDark` dictionary with the animated
`Controls.xaml`. The app-level token is therefore never zeroed, and the bubble animated under OS
reduced motion. Fix: the window takes the gate as a settable presentation flag (AppHost already owns
it), the fade is skipped under the gate, and the `DoubleAnimation` carries `BezierEase` like every
other production animation.

**F7 · WCAG 2.2.2 (Level A) requires moving content that starts automatically, lasts more than
five seconds, and is presented in parallel with other content to be pausable/stoppable/hideable
unless essential.** The spinner loops forever (`RepeatBehavior="Forever"`) alongside the card's
text. The OS client-area-animation setting is the platform's pause mechanism; the product's
reduced-motion contract (DESIGN §24, decision 0013/0021) is to honor it. Decision 0023 recorded
the ungated spinner as a separate defect whose fix needs *"either a fifth token or a view-model
flag"*. A fifth token would add an arbitrary timing value (DESIGN §24: *"Do not introduce
arbitrary timing values for production behavior"*) and a fifth `Motion.*` name (R-02: only the
four approved durations may appear in the shell) — both need a DESIGN §24 amendment, which is out
of reach here. The view-model flag needs no token and no DESIGN change: the snapshot already
carries `Shell.ReducedMotion`, the shell view model already applies snapshot-derived presentation
state (pin), and the card's storyboard starts only when the flag is true. The 850 ms loop period
itself stays (the documented exception in `MotionCurveTests`); only its *running* is gated.

**F8 · `RowCardButton`'s overlay must start at `Opacity="0"` like the `Button` template's layers.**
Implementation finding (observed here): the `Button` template's `HoverLayer`/`PressedLayer` declare
`Opacity="0"` and animate `To="1"`/`To="0"`; `RowCardButton`'s `Overlay` declared no initial opacity,
so the enter animation ran 1→1 (a no-op) and the first hover popped. The reduced twin needs the
matching instant `Setter` (`Opacity` 1 on hover), because with no animations a starting `0` would
otherwise hide the hover fill forever. `ReducedMotionDictionaryTests` pins key parity between the
twins; adding an attribute and a setter changes no keys.

**F9 · Microsoft's `AnimateWindow` shadow warning still bounds the whole reveal.** (Restated from
research/2026-10-07, F1.) The slide animates a shadowed, rounded HWND by construction; the fix
set does not add motion, it removes *wrong* motion (teleports, flashes, blinks).

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Retarget the open from current bounds; position before `Show()` | Removes the teleport and the flash; matches F1/F2/F4 | Slightly more branches in `ShowNativeFlyout`; first show still needs the DIP pre-seed (no HWND yet) |
| Keep Show-then-place; add a fade to mask the flash | Masks one symptom | Adds motion to hide a defect; the layered/alpha path is banned (decision 0004/0013) |
| Bubble: fade on every change (status quo) | Simplest | Blinks on every tooltip change; linear; ignores the gate (F5/F6) |
| Bubble: fade only on appear, gated, eased | Matches decision 0021's record; no blink; gate-honoring | Needs the gate flag plumbed to the bubble window (one property) |
| Bubble: swap the app-level token dictionary too | Reuses the existing mechanism | The gate is window-scoped by design; the bubble is a second window; a per-window flag is smaller and testable |
| Spinner: fifth `Motion.*` token for 850 ms | Tokenized | R-02 violation (only four durations); arbitrary timing value (DESIGN §24); needs a DESIGN amendment |
| Spinner: view-model flag from `Shell.ReducedMotion` | No token, no DESIGN change; the research note's own suggested option | The flag is presentation state on the card VM (acceptable: the VM already carries snapshot-derived presentation state) |
| Spinner: leave it (decision 0023 non-change) | Zero risk | WCAG 2.2.2 exposure under reduced motion; the owner's audit asked for it |
| RowCardButton: two constant-background layers (Button-template parity) | Fades in AND out | More template chrome; the exit fade's background-revert problem is structural to triggers |
| RowCardButton: initial `Opacity="0"` + reduced-twin setter | One attribute plus one setter; fixes the first-hover pop; twins stay consistent | Hover-out remains instant (the exit storyboard resets opacity for the next enter; the background reverts with the trigger) |

## Chosen approach

1. **Open reveal retargets when the window is already visible** (`AppHost.ShowNativeFlyout`): the
   placement plan is `FlyoutPlacement.OpenRevealPlan.Resolve(windowVisible, reducedMotion,
   currentBounds, finalBounds, offscreenOrigin)` — visible+animated: stay at current bounds and
   slide from there; visible+reduced: snap to final; hidden+animated: place at the off-tray origin;
   hidden+reduced: place at final. The animator then flies from the placed rect, so a re-open during
   a close slide and a display-change re-place both retarget (F4).
2. **Position before showing.** Reopen (handle exists, hidden): `SetPhysicalBounds` at the plan's
   placement *before* `window.Show()` (F1). First-ever show (no handle): pre-seed `Left`/`Top`/
   `Width`/`Height` in DIP at the origin before `Show()`, which `WindowStartupLocation.Manual`
   applies at source creation (F2). After `Show()` the plan's placement is re-applied with the
   freshly measured height (off-screen, invisible) so the slide endpoints carry the real size.
3. **Bubble fade is appear-only, eased, and gated.** `StatusBubblePolicy.ShouldFadeIn(wasVisible,
   animationsEnabled)` is the pure, tested decision; `StatusBubbleWindow.Apply` fades only when it
   returns true, otherwise clears any in-flight animation and shows the content whole. The
   `DoubleAnimation` carries `BezierEase`; `AppHost` syncs the gate flag to the bubble in
   `RefreshStatusBubble` (F5/F6).
4. **Spinner gated by a view-model flag.** `NativeShellViewModel` reads `snapshot.Shell.
   ReducedMotion`, passes `SpinnerEnabled` into `PairingCardViewModel` (ctor + `SetSpinnerEnabled`),
   and `PairingCardView.xaml` starts the loop from a `DataTrigger` on `SpinnerEnabled` instead of an
   unconditional `Loaded` trigger. The 850 ms period and the linear constant-velocity rotation stay
   (the documented `MotionCurveTests` exception); under reduced motion the ring is static (F7).
5. **`RowCardButton` overlay starts at `Opacity="0"`** in `Controls.xaml`, with the matching
   `Opacity=1` hover `Setter` in `ControlsReduced.xaml` (F8).

## Why

Each fix removes a cause of the reported "jittery, disappearing and reappearing" feel rather than
masking it: no teleports (retarget), no show-before-place flashes (position first), no per-change
blinks (appear-only fade), no ungated looping motion (spinner flag), no first-hover pop (initial
opacity). No new dependency, no new surface, no new component variant, no new timer, no new
`Motion.*` token, and no DESIGN.md edit. The frozen durations, the frozen curve, the position-only
slide, the single-writer gate, and the reduced-motion dictionary swap are all untouched.

## Rejected approaches

- **A fifth `Motion.*` token for the spinner's 850 ms period.** R-02 allows only the four approved
  durations in the shell; DESIGN §24 bans arbitrary timing values; adopting it needs a DESIGN §24
  amendment (R-01), which this change does not make. The view-model flag achieves the user-facing
  fix (no spinning under reduced motion) without either violation.
- **Slowing or lengthening any animation.** Not a cause; the tokens are frozen (R-01/R-02) and a
  longer duration makes jitter more visible (research/2026-10-07).
- **Fading the flyout window to mask the show/place flash.** Window-level alpha needs the layered
  path, which is banned (decisions 0004/0013; research/2026-10-07 F8–F10).
- **Removing the exit storyboard from `RowCardButton`.** It looks dead (the background reverts with
  the trigger) but it returns the overlay's opacity to 0 so the *next* enter can fade in.
- **Rebuilding the pairing card on phase change to restart the spinner.** Card identity is stable by
  design (decision 0014-consistency); the flag approach does not rebuild anything.

## Compatibility concerns

- **Reduced motion.** The gate now reaches the bubble (flag) and the spinner (flag); the flyout
  chrome keeps the dictionary swap. Toggling the OS setting mid-fade leaves the bubble's opacity to
  settle on the next `Apply` (the else-branch clears any in-flight animation).
- **First show.** The DIP pre-seed uses `_currentEffectiveHeight` (the 500 DIP seed on first show);
  the post-`Show()` placement re-applies with the measured height off-screen, so the slide's
  endpoints carry the real size (decision 0023's position-only invariant is preserved).
- **Reopen during a close slide.** `_presenter.Cancel()` drops the close's `Hide()` completion;
  the open retargets from the last applied frame; the `_showGeneration` guard still arbitrates.
- **DPI.** The pre-seed converts physical→DIP with `window.DpiScale` (screen-DC fallback before the
  source exists); `WM_DPICHANGED` handling is unchanged.
- **`MotionCurveTests`.** `PairingCardView.xaml` keeps exactly one uneased `DoubleAnimation` (the
  spinner), so the documented-exception assertion still holds.
- **`ReducedMotionDictionaryTests`.** No `x:Key` changes; the reduced twin stays free of
  `BeginStoryboard`/`DoubleAnimation`.

## Testing implications

- `FlyoutPlacementTests`: the `OpenRevealPlan.Resolve` matrix — visible/hidden × animated/reduced
  (4 cases) pins Placement and Slide.
- `StatusBubblePresenterTests` (or a new policy test): `ShouldFadeIn` — hidden→visible fades;
  visible→visible does not; gate off never fades.
- `NativeShellViewModelTests`: a pairing card under a `ReducedMotion=true` snapshot has
  `SpinnerEnabled == false`; the flag follows the gate across snapshots on the same card instance.
- `ReducedMotionDictionaryTests`: both twins' `RowCardButton` overlay starts at `Opacity="0"`;
  the reduced twin raises it to 1 on hover.
- Live (owner's eyes), recorded in `docs/DEVICE-TEST-MATRIX.md`: rapid toggle open/close shows no
  teleport; display change while open re-places without vanishing; bubble colour/text change while
  visible does not blink; OS reduced motion leaves the pairing ring static.
- `scripts/verify.ps1` green, plus a Release launch from a path that does not contain "smoke"
  (R-12).

## Open items

- The spinner's 850 ms period is still a hardcoded non-token value. Tokenizing it needs a DESIGN
  §24 amendment (a fifth duration or an explicit loop-period allowance) — an owner-facing contract
  decision, recorded here rather than smuggled in.
- The card-expand interaction (instant height step + 167 ms content fade from 0) is per DESIGN
  §24/952; softening it (e.g. fading the step's exposed strip) needs a DESIGN amendment.
- Tick-quality evidence: watch the animator's `worst tick gap` log line; if it ever reads far
  above one frame, the composition clock is render-starved and the slide stalls between renders.
- If the retargeted reveal still reads as jerky, the honest next step remains **no slide** (instant
  open/close, decision 0013's rollback) — an owner-facing contract decision.
