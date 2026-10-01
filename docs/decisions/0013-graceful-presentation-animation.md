# 0013 — Graceful presentation: animated growth and open/close reveal

- **Date:** 2026-10-02
- **Status:** ACCEPTED — explicit human design-authority change (amends 0012; narrows 0005's ban to its actual cause)
- **Phase:** post-Phase-12 usability

## Issue

0012's instant step-resize removed the stale-height races but the owner reports the result feels
jittery (several rapid steps when devices/metadata arrive, jumps on expand) and that light-dismiss
hiding without any transition "feels broken rather than fast". The owner approved animated growth
(167 ms, retargeting), animated open (167 ms slide out of the tray), and animated close (83 ms
slide into the tray) — all from DESIGN §24's already-sanctioned token vocabulary.

## Decision

1. **Growth/shrink while visible — mode-B animated retarget.** On a settled height change, the
   WebView child is pinned to its final bounds **once** (Dock detached for the animation,
   restored on completion), and the window frame animates current→target over **167 ms** with
   the Fluent curve (`cubic-bezier(0.55, 0.55, 0, 1)`), the tray-adjacent edge frozen for the
   duration (bottom taskbar ⇒ bottom edge fixed; left/right ⇒ top edge fixed, height only). New
   content changes mid-animation **retarget** from the current animated state (R-06).
2. **Open — slide out of the tray edge, 167 ms** (position-only; size constant; zero web work).
3. **Close — slide into the tray edge, 83 ms**, then `Hide()`. Applies to light-dismiss, Esc,
   tray toggle, and power/session hides. A show during a close cancels the close (generation
   guard).
4. **Reduced motion honored:** the OS client-area-animation preference
   (`SPI_GETCLIENTAREAANIMATION`) is queried and piped through the existing
   `ShellAppearanceChangedEvent` (replacing the hardcoded `false`); when animations are off,
   everything reduces to 0012 instant steps and instant hide.
5. **Unchanged bans:** per-frame child resize (the measured 0003/0005 killer — the child is
   sized once per animation), FLIP coordination, spring/bounce/overshoot, CSS animation of
   structural rows, the 560 epx ceiling, 376 width, and the 0 ms truth rule for *content*
   (reports still measure final natural height; only the window's presentation animates).
6. **Animation clock:** a UI-thread `System.Windows.Forms.Timer` (~16 ms tick) inside
   `FlyoutRevealAnimator`, injected via an `ITickSource` seam for deterministic tests. This is
   the first sanctioned UI animation clock (R-05 concerns ADB polling, not presentation);
   decision 0013 supersedes 0012's "no timers" wording on this axis.

## Rejected alternatives

- Fade close — WebView2's DirectComposition child does not reliably honor layered-window alpha.
- Height-collapse close — per-frame Chromium reflow for an 83 ms effect.
- Instant everything — the owner-reported broken feel.

## Honest limits

- Mode B (final child size once) is **measured (0/0 child resizes) but never human-accepted**;
  this decision carries that validation burden. If human acceptance fails again, the rollback is
  reverting to 0012 instant steps — the animator is a single isolated class.
- `WM_DPICHANGED` mid-animation cancels the animation and adopts the OS-suggested bounds
  (validated live).

## Testing

- Animator unit tests with a fake tick source: easing sampling, retarget mid-flight (latest
  wins), cancel snaps to target, completion callbacks fire once, reduced-motion bypass.
- WebView child pinned exactly once per animation and restored after.
- Placement: frozen tray-adjacent edge per taskbar edge.
- `test-motion.ps1` gains animator tests; existing suites stay green.
- Headed: feel at 100/125/150 % DPI; toggle during close; grow during open.
