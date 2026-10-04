# Motion system: guidance, technique, and the chosen tokens

- **Date:** 2026-10-04
- **Author:** coding agent (full animation-system audit requested by the owner)
- **Phase:** decision 0021 (motion system + native self-sizing)
- **Status:** current

## Problem

The owner asked for a full audit of the app's animation system: what exists, what the design
specified, what Microsoft's guidance recommends, and what should move where. The audit (three
parallel deep-dives plus primary-source fetches) found the native shell animated only the
flyout's open/close slides, a broken reduced-motion gate, and several design-specified motions
that were never ported.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Motion in Windows (the timing/easing table) | https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion | fetched 2026-10-04 |
| Timing and easing (WinUI durations, enter/exit curves) | https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing | fetched 2026-10-04 |
| Optimizing WPF performance: layout | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-layout-and-design | current |
| Animation and Timing System Overview (WPF) | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/animation-and-timing-system-overview | current |
| `AnimateWindow` (window animation; shadow warning) | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-animatewindow | current |
| `SystemParametersInfoW` / SPI_GETCLIENTAREAANIMATION | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow | current |
| WCAG 2.3.3 Animation from Interactions | https://www.w3.org/WAI/WCAG22/Understanding/animation-from-interactions.html | WCAG 2.2 |
| MDN prefers-reduced-motion (vestibular triggers) | https://developer.mozilla.org/en-US/docs/Web/CSS/@media/prefers-reduced-motion | current |
| Fluent 2 Motion (qualitative) | https://fluent2.microsoft.design/motion | current |
| Fluent 2 UI React numeric tokens | https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/durations.ts | current |
| Project: decision 0013 + research 2026-10-02-flyout-presentation-animation.md | docs/decisions/0013-graceful-presentation-animation.md | 2026-10-02 |

## Findings

**Microsoft's table (fetched verbatim).** Direct Entrance Fast-In `cubic-bezier(0,0,0,1)`
167/250/333; Existing Elements Point-to-Point `cubic-bezier(0.55,0.55,0,1)` 167/250/333;
Direct Exit Fast-Out `cubic-bezier(0,0,0,1)` 167 ms — *"ALWAYS combine with fade out"*; Gentle
Exit Soft-Out `cubic-bezier(1,0,1,1)` 167 ms; Bare Minimum fade Linear 83 ms; WinUI resources
`ControlNormalAnimationDuration` 250 ms, `ControlFastAnimationDuration` 167 ms,
`ControlFasterAnimationDuration` 83 ms.

**The repo's tokens are correctly derived but half-transcribed.** 0/83/167/333 and the
Point-to-Point curve match Existing Elements + the 83 ms Bare-Minimum fade exactly (provenance:
research/2026-09-20-fluent-point-to-point-structural-motion.md; DESIGN §24). What was never
transcribed: the exit half of the table. The flyout CLOSE currently rides the "existing
elements" curve, where Microsoft files a leaving surface under exit motion (Gentle Exit
Soft-Out, or Direct Exit + fade). Recorded for a future decision; not changed now.

**Naming is a velocity profile, not a direction.** "Fast Out, Slow In" `(0,0,0,1)` is for
objects ENTERING (they arrive fast and decelerate to rest); "Slow Out, Fast In" `(1,0,1,1)` is
for objects EXITING (accelerate away). This was mis-transcribed in passing during the audit and
corrected by fetching the primary source.

**Cheap vs expensive properties.** `Opacity` and `RenderTransform` are composition-level;
`Width`/`Height`/`Margin` force measure+arrange ("a mathematically-intensive process"). Update
an existing Transform rather than replacing it. The layout pass is what made the historical
per-frame child resize expensive (measured 12 child resizes + jank under WebView2,
research/2026-09-20-prephase10-runtime-diagnosis.md:350-354) — that cause is gone since
decision 0015, but the rule stands: content opacity/transform animate; the window's height
changes as ONE instant step per settled change (self-sizing, this decision).

**Windows-specific warnings.** `AnimateWindow` warns that animating a window with a drop shadow
is "visually distracting, jerky" — our slides are position-only over a shadowed window and
measured fine; a per-frame window RESIZE over a shadow is exactly what to avoid. Topmost does
not beat exclusive fullscreen; `SHQueryUserNotificationState` gates (used by the status bubble).

**Accessibility.** WCAG 2.3.3 requires interaction-triggered motion be disableable; SPI
GETCLIENTAREAANIMATION is the Windows switch. The audit found the gate was inert (the only
producer of ShellAppearanceChangedEvent hardcoded `ReducedMotion=false`); decision 0021 fixes
the gate AND propagates it into the XAML token layer so "instant" is one override.

**Testing motion.** The repo's `ITickSource` + injectable clock pattern (animator tests) is the
practical deterministic approach: test the value function, not pixels. The WPF Performance Suite
(Perforator/Visual Profiler) and ETW/GPUView are the standard frame-time tools; pixel baselines
pin static layout only.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Cheap properties only (chosen) | Composition-level, no layout, honours every DESIGN motion line that is actually implementable | No structural height animation (recorded gap) |
| Animated height steps inside the flyout | DESIGN 919-922's literal intent | The twice-failed area; requires window-resize-per-settled-change discipline; deferred with a stop-condition note |
| Adopt Microsoft's exit curve + fade for the close | Platform-aligned | Amends DESIGN §24; recorded with exact values for a later one-token change |
| Fluent 2 tokens (50-500 ms, decelerate-max) | "Modern" | Cross-platform scale; its decelerate-max is DESIGN §24's explicitly retired curve |

## Chosen approach

1. **One token layer.** `Motion.Feedback` (83 ms), `Motion.State` (167 ms),
   `Motion.Structural` (333 ms) as Duration resources in Themes/Controls.xaml (theme-neutral);
   the reduced-motion gate overrides them with zero at window level, so every consumer goes
   instant with no per-call-site branch.
2. **Cheap-property motion** (opacity + RenderTransform only): hover/press 83 ms overlays;
   card chevron rotation 167 ms + details fade-in 167 ms (height instant); pairing
   success/progress fade-in; "Copied" badge fade-in 83 ms (now with its 1200 ms hold restored);
   status bubble content fade-in 83 ms on appear.
3. **Native self-sizing** (companion decision 0021 change): the flyout follows its content as
   one instant step per settled change, bottom edge frozen, 160-560 DIP clamp, scroll beyond.
4. **Deferred, with exact values recorded**: exit curve `cubic-bezier(1,0,1,1)` or
   `(0,0,0,1)`+fade for the close; the 250 ms token; animated height steps.

## Why

The tokens are already the platform's; the missing half was the exit guidance and the gate.
Animating opacity/transform is the cheap, universally-recommended layer; height stays an
instant step so "no animation delays truth" (DESIGN §29) and the twice-failed territory stays
unentered without a dedicated research pass.
