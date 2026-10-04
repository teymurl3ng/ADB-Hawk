# 0021 — Motion system: token layer, cheap-property motion, native self-sizing, and the reduced-motion fix

- **Date:** 2026-10-04
- **Companion research:** research/2026-10-04-motion-system.md (fetched primary sources)
- **Audit basis:** three parallel deep-dives (animation engine; native-shell motion vs the
  frozen design; Microsoft/Fluent guidance) requested by the owner

## Problem

The owner requested a full animation-system audit. Findings:

1. The native shell animated ONLY the flyout's open (167 ms) and close (83 ms) slides; every
   other visual change was instant — including motions DESIGN §24 specifies (hover/press 83 ms,
   card chevron+detail 167 ms).
2. **The reduced-motion gate was inert**: the only production producer of
   `ShellAppearanceChangedEvent` hardcoded `ReducedMotion = false`, so the gate reset itself to
   "enabled" on every snapshot and the OS client-area-animation setting was never honoured
   (contradicting DESIGN 936-937 and WCAG 2.3.3).
3. The flyout was a fixed 376×500 window, making DESIGN 946's device insert/remove step
   impossible rather than merely unimplemented.
4. The "Copied" badge never cleared (the web cleared it after 1200 ms), so there was no brief
   state to animate; the status bubble's hover growth never existed (`LayoutChanged` had no
   subscriber and the window was pinned at 22×22).
5. Dead code: an unused `TickMs`, an unreachable `SlideTarget` variant, `LayoutChanged` with no
   subscriber.

## Decisions

1. **One token layer.** `Motion.Feedback` (83 ms), `Motion.State` (167 ms),
   `Motion.Structural` (333 ms) become Duration resources in `Themes/Controls.xaml`
   (theme-neutral, next to the chrome). The reduced-motion gate overrides them with **zero** at
   window level (`ApplyMotionTokenGate`), so every `DynamicResource` consumer — hover layers,
   chevron, fades — goes instant with the gate, live, with no per-call-site branch. Values are
   the frozen DESIGN §24 tokens; DESIGN.md is untouched.
2. **Cheap-property motion only.** Opacity and `RenderTransform` animate (composition-level);
   `Width`/`Height`/`Margin` never animate (layout pass), and the window's height changes as
   ONE instant step per settled change. Implemented: button hover/press opacity layers (83 ms);
   card chevron rotation (167 ms; the geometry no longer swaps — one fixed glyph rotating, so
   the motion reads as a single rotation) + details fade-in (167 ms; no fade-out — removal never
   delays truth, R-06); pairing success/progress fade-in; "Copied" badge fade-in (83 ms) —
   behind the restored 1200 ms hold; status bubble content fade-in (83 ms) on appear
   (window-level fades need the banned layered path; content opacity is free).
3. **Native self-sizing.** `ShellSizing` (pure, 11 tests): the window follows its content as one
   instant step per settled change; the tray-adjacent BOTTOM edge is frozen (growth extends
   upwards); clamp [160, 560] DIP with the scroll container taking over at the ceiling; the
   window never rises above the work area (and when a hypothetical work area cannot fit even the
   floor, the floor wins). Applied via one `SetWindowPos` per step, deduplicated against
   `ActualHeight` — NOT the logical `Height` property, which external moves never update (the
   first implementation silently no-opped on exactly that). The historical per-frame child
   resize ban is untouched; the two WebView2-era failures had a cause decision 0015 removed,
   but animated height steps remain **deferred**, not re-adopted.
4. **Defect fixes** (the gate above, plus): the Copied badge's generation-guarded 1200 ms clear
   (a second copy supersedes; card removal cancels; clipboard failures are logged and show NO
   badge); bubble hover growth via content-sizing; dead code removed (`TickMs`, the
   `SlideTarget` intoTray variant, the subscriber-less `LayoutChanged`).
5. **Deferred with exact values recorded** (research note): Microsoft's exit curves for the
   close — Gentle Exit `cubic-bezier(1,0,1,1)` at 167 ms, or Direct Exit `(0,0,0,1)` + fade;
   the 250 ms `ControlNormalAnimationDuration` token; DESIGN 919-922's animated height step.
   Each is a one-token change plus a decision note if later wanted.

## Non-changes

- DESIGN.md untouched (the tokens, durations and curve are unchanged; §4's fixed-width and the
  560 ceiling are honoured).
- The frozen curve for open/close (167/83 ms, Point-to-Point) — the exit-curve divergence is
  documented, not adopted.
- No new timers (R-05): self-sizing is layout-event-driven; the token override rides the
  existing appearance event; the badge hold reuses the one-shot generation-guarded pattern.
- The WebView path is untouched.

## Acceptance

- `ShellMotionGateTests` (8) pin the gate's convergence to the OS probe; the gate now reaches
  both the animator call sites AND the XAML token layer.
- `ShellSizingTests` (11) pin clamp, frozen bottom edge, work-area handling, and the step
  threshold.
- Badge tests (3): clear after the hold, supersede on a second copy, no badge on clipboard
  failure.
- Live verification: self-sizing measured 376×500 → 376×306 with the bottom edge frozen at
  804 DIP; hover/press, chevron, badge and phase fades await the owner's eyes with the OS
  animation setting toggled both ways (the check that has never actually worked until the
  gate fix).
