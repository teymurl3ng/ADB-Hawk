# 0024 — Animation follow-up: open-reveal retargeting, position-before-show, appear-only fades, and the reduced-motion spinner gate

- **Date:** 2026-10-10
- **Companion research:** research/2026-10-10-animation-reveal-retarget-and-appear-fades.md
- **Amends:** nothing in DESIGN.md. Completes the retargeting rule of DESIGN §26 / R-06 on the open
  path, the recorded "fade-in on appear" of decision 0021, and the reduced-motion defect of
  decision 0023. ENGINEERING.md §9.2/§11 row 30 wording updated to match.
- **Basis:** the owner reported that after decision 0023 the shell still reads as jittery with a
  "disappearing and reappearing" quality, and asked for a full animation audit against industry
  standards and official guidelines.

## Problem

The audit (research note above) found five remaining defects, all presentation behaviour:

1. **The open path snapped to the off-tray origin even when the window was already visible.** A
   re-open during an in-flight close slide, or a display change re-running the show path
   (`OnDisplayChanged`), teleported the flyout fully behind the taskbar before sliding it back in —
   a visible disappear/reappear. The close path already retargeted from current bounds; the open
   path did not.
2. **`Show()` ran before positioning.** On a reopen after a reduced-motion hide the window became
   visible at its previous on-screen position before being yanked behind the tray, and the layout
   pass inside `Show()` ran the self-size step at the old position before the animator started. On
   first show the source was created at the OS cascade position.
3. **The status bubble re-faded from opacity 0 on every state change.** `BubbleState` is a record
   that includes the tooltip `Detail`, so every tooltip/colour change while the bubble was visible
   reset `Shell.Opacity = 0` and faded back in — a blink per status change. The fade was also
   linear (no `BezierEase`) and read the app-level `Motion.Feedback` token, which the reduced-motion
   gate never swaps, so the bubble animated under OS reduced motion.
4. **The pairing spinner ignored reduced motion.** A hardcoded 850 ms `RepeatBehavior="Forever"`
   loop is not covered by any `Motion.*` token, so the `ControlsReduced.xaml` dictionary swap could
   not reach it (decision 0023 recorded this as a separate defect).
5. **`RowCardButton`'s hover overlay had no initial `Opacity="0"`**, so the first hover popped
   (the enter animation ran 1→1) while later hovers faded.

## Decisions

1. **The open reveal retargets when the window is already visible.** The placement plan is
   `FlyoutPlacement.OpenRevealPlan.Resolve(windowVisible, reducedMotion, currentBounds,
   finalBounds, offscreenOrigin)`: visible+animated → stay at current bounds and slide from there;
   visible+reduced → snap to the final placement; hidden+animated → place at the off-tray origin;
   hidden+reduced → place at the final placement. This applies DESIGN §26 / R-06 to the open path
   exactly as the close path already did — no new rule, the existing one extended to the missing
   direction. `OnDisplayChanged` on a visible flyout now re-places it smoothly instead of
   vanishing it behind the tray.
2. **Position before showing.** On reopen (the handle persists across `Hide()`) the plan's placement
   is applied with `SetWindowPos` *before* `Window.Show()` — `ShowWindow` displays a window "in its
   current size and position", so a hidden window keeps whatever rect it was given. On the
   first-ever show, where the HWND does not exist until `Show()` creates it, `Left`/`Top`/
   `Width`/`Height` are pre-seeded in DIP at the reveal origin; `WindowStartupLocation.Manual`
   positions the window from those values when first shown. After `Show()`, the placement is
   re-applied with the freshly measured height (off-screen, invisible) so the slide endpoints carry
   the real size and the position-only invariant (decision 0023) holds.
3. **The bubble fade is an appear transition, eased and gated.** `StatusBubblePolicy.
   ShouldFadeIn(wasVisible, animationsEnabled)` is the single decision: the fade runs only when the
   bubble was hidden and animations are enabled. A state change while visible updates colour and
   detail text in place and clears any in-flight fade. The `DoubleAnimation` carries `BezierEase`
   (the frozen curve, DESIGN §24) and `AppHost` syncs the reduced-motion gate to the bubble window.
   This aligns the implementation with decision 0021's recorded intent ("content fade-in 83 ms on
   appear") — a defect fix, not a contract change.
4. **The pairing spinner is gated by a view-model flag, not a token.** `NativeShellViewModel` reads
   `snapshot.Shell.ReducedMotion` and exposes it to the pairing card as `SpinnerEnabled` (ctor +
   `SetSpinnerEnabled`); `PairingCardView.xaml` starts the loop from a `DataTrigger` on
   `SpinnerEnabled` instead of an unconditional `Loaded` trigger. Under OS reduced motion the ring
   is static. The 850 ms period and the linear constant-velocity rotation are unchanged — they
   remain the documented exception in `MotionCurveTests`. This is the option the 2026-10-07
   research note named ("a view-model flag") precisely because it needs no fifth `Motion.*` token
   (R-02 allows only the four approved durations) and no arbitrary timing value (DESIGN §24).
   Tokenizing the period remains an open item needing a DESIGN §24 amendment.
5. **`RowCardButton`'s overlay starts at `Opacity="0"`** in `Controls.xaml`, with the matching
   instant `Opacity=1` hover `Setter` in `ControlsReduced.xaml` (with no animations, a starting 0
   would otherwise hide the hover fill forever). The exit storyboard stays: it returns the overlay's
   opacity to 0 so the next enter can fade in. Hover-out remains instant — the trigger's background
   setter reverts with the condition, which is structural to triggers.

## Non-changes

- **No DESIGN.md edit.** The frozen durations (0/83/167/333 ms), the frozen curve
  `cubic-bezier(0.55, 0.55, 0, 1)`, the position-only slide, the instant height steps, and the
  opaque-surface contract are all untouched.
- **No new `Motion.*` token, no arbitrary timing value, no fifth duration.** The spinner's 850 ms
  period stays as the documented exception.
- **No new dependency, no new surface, no new component variant, no new timer.** The gate flag is
  presentation state; the spinner gate rides the existing snapshot flag.
- **The card-expand interaction is unchanged** (instant height step + 167 ms detail fade from 0,
  per DESIGN §24/952). Softening it needs a DESIGN amendment and was not part of this change.
- **`Controls.xaml`/`ControlsReduced.xaml` key parity is unchanged** (attributes and setters only).

## Acceptance

- `FlyoutPlacementTests`: the `OpenRevealPlan.Resolve` matrix (visible/hidden × animated/reduced)
  pins the placement and the slide flag for all four cases.
- `StatusBubblePresenterTests` (or a new `StatusBubblePolicy` test): `ShouldFadeIn` — hidden fades,
  visible does not, gate off never fades.
- `NativeShellViewModelTests`: a pairing card under a `ReducedMotion=true` snapshot has
  `SpinnerEnabled == false`; the flag follows the gate across snapshots on the same card instance.
- `ReducedMotionDictionaryTests`: both twins' `RowCardButton` overlay starts at `Opacity="0"`; the
  reduced twin raises it to 1 on hover.
- Existing suites stay green: `FlyoutRevealAnimatorTests`, `MotionCurveTests` (the spinner remains
  exactly one uneased animation in `PairingCardView.xaml`), `ReducedMotionDictionaryTests`,
  `ShellMotionGateTests`, `ShellSizingTests`, `FrozenVisibleBoundsTests`.
- `scripts/verify.ps1` green, plus a Release launch from a path that does not contain "smoke"
  (R-12).
- `docs/DEVICE-TEST-MATRIX.md` gains a section for this change: the four automated rows above, plus
  live rows for rapid toggle (no teleport), display change while open (no vanish), bubble state
  change while visible (no blink), and the static ring under OS reduced motion.

## Open items

- Tokenizing the spinner's 850 ms period needs a DESIGN §24 amendment (an owner-facing contract
  decision).
- The card-expand fade/step interaction is per DESIGN §24/952; changing it needs a DESIGN amendment.
- If the retargeted reveal still reads as jerky to the owner's eye, the honest next step remains
  **no slide** (instant open/close, decision 0013's rollback) — an owner-facing contract decision.
