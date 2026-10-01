# Flyout presentation animation: mode-B reveal, tray slide, reduced motion

- **Date:** 2026-10-02
- **Author:** agent (flyout presentation animation; decision 0013 amending 0012/0005)
- **Phase:** post-Phase-12 usability
- **Status:** current

## Problem

0012's instant step-resize removed the stale-height races but introduced a feel problem the
owner reports as jitter: multiple settled content changes in quick succession (device connects →
metadata → battery → expand) each jump the window height instantly. Separately, light-dismiss
hides the window with no transition ("feels broken rather than fast"). The owner approved:
animated growth (167 ms), animated open (167 ms slide out of the tray), animated close (83 ms
slide into the tray), honoring OS reduced motion.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| In-repo archaeology | git history + research/2026-09-20-prephase10-runtime-diagnosis.md (A/B/C measured table), research/2026-09-20-prephase10-acceptance-…md (retarget rule, fixed tray-adjacent edge, Y-jitter), 0003 (easing history) | 2026-10-02 audit |
| Microsoft Learn — WebView2 threading model | https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model | fetched 2026-10-02 |
| Microsoft Learn — SystemParametersInfo, SPI_GETCLIENTAREAANIMATION | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfowa | fetched 2026-10-02 |
| DESIGN.md §24 (motion vocabulary; flyout open 167 / close 83 examples), decision 0012 | in-repo | current |

## Version investigated

Windows 11 26100 · WebView2 1.0.4191.47 · .NET 10 WinForms.

## Findings

1. **Root cause of the 0003/0005 failures was per-frame CHILD resize, not animation itself.**
   The old `StructuralSizeAnimator` set `_flyoutWindow.Bounds` every ~16 ms tick while WebView2
   was `Dock=Fill` → WinForms dock layout resized the Chromium child every frame → relayout per
   frame → blank/flicker (measured: 12 child resizes per expand click). The diagnosis's own
   "mode B" — **final child size once; HWND clips/reveals** — measured
   `childHeightChangeCount 0/0, childResizeCount 0/0` (elapsed ~360 ms vs A's ~344 ms: removing
   jank cost nothing). Mode B was never implemented as a product path and never human-rejected.
2. **The easing curve was never convicted.** `runtime-diagnosis.md:274-275`: easing alone
   "insufficient to explain human fail"; 0003's 333 ms Fluent Point-to-Point
   (`cubic-bezier(0.55, 0.55, 0, 1)`) is the same curve DESIGN §24 retains today.
3. **Timing tokens already sanctioned by DESIGN §24:** "flyout open: 167 ms", "flyout close:
   83 ms", "status change: 167 ms". Growth animation uses 167 ms; open 167 ms; close 83 ms.
4. **Fixed tray-adjacent edge during animation** (0003 guidance, live-measured jitter otherwise:
   unpinned Y recomputed per frame made the window "jitter"). `FlyoutPlacement`: bottom taskbar
   ⇒ bottom edge fixed, animate top Y; top taskbar ⇒ mirror; left/right ⇒ X fixed, freeze the
   top edge and animate height only (no per-frame vertical re-centering).
5. **Retargeting rule (0003-era, kept):** latest target wins mid-flight; live reports during the
   animation are absorbed as retargets; one reconcile at completion. This is exactly R-06.
6. **Fade is rejected on evidence:** the FlyoutWindow has no `WS_EX_LAYERED` and WebView2's
   DirectComposition child does not reliably honor layered-window alpha; `Form.Opacity` on a
   window hosting WebView2 is a known-fragile path. Position/size animation only.
7. **Animation clock:** no timer primitive exists in the app today. A UI-thread
   `System.Windows.Forms.Timer` (~16 ms tick) requires no marshaling (ticks on the WinForms
   thread where bounds are applied) and is injectable via a seam (`ITickSource`) for
   deterministic tests. R-05 (no ADB-polling timers) is not implicated: this is a UI
   presentation clock sanctioned by decision 0013.
8. **Reduced motion:** Win32 `SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)` exposes the OS
   "show animations" setting without WinRT; wired into the existing `PostCurrentShellAppearance`
   pipeline (replacing the hardcoded `false`) so both native (skip animation) and web (existing
   CSS media query) honor it.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Fade out (opacity) | Most Win11-literal | Layered alpha + WebView2 DirectComposition = fragile/unreliable; rejected |
| Height-collapse close | Reuses growth animator | Height animation reflows Chromium per frame (the banned cost) for an 83 ms effect nobody sees fully |
| Instant everything (status quo 0012) | Zero risk | The owner-reported jitter/broken-close complaints |
| CSS-only animation | No native work | Cannot move the HWND; window clips content — not the requested behavior |

## Chosen approach

1. `Host/FlyoutRevealAnimator`: UI-thread tick loop (16 ms `Forms.Timer`, `ITickSource` seam);
   animates either **bounds** (growth: height toward target, tray-adjacent edge frozen per
   placement edge) or **position** (open/close slide along the tray-normal axis, size constant);
   Fluent easing sampled at tick boundaries; retarget replaces the target and extends from the
   current animated state; cancel snaps to target (growth) or hides immediately (close);
   `IsAnimating`/generation exposed so AppHost's show/hide guards compose.
2. Mode-B child pinning during growth: WebViewHost gains `PinChildBounds()`/`UnpinChildBounds()`
   (detach `Dock=Fill`, set final bounds once, restore on completion) so Chromium performs zero
   per-frame work.
3. AppHost wiring: `ResizeFlyout` (visible, changed height) → animator.GrowTo; `ShowFlyout` →
   slide-out then final bounds; every hide path → animator slide-in → `Hide()`. `_showGeneration`
   invalidates in-flight close animations on show. Reduced motion ⇒ all of the above become the
   0012 instant steps.
4. `SystemTheme.ReadClientAreaAnimation()` (SPI_GETCLIENTAREAANIMATION via CsWin32 or
   SystemInformation.IsPresentationCompatible fallback) → `PostCurrentShellAppearance`.

## Why

Mode B is measured (0/0 child churn), the easing is the retained DESIGN curve, the durations are
sanctioned tokens, and the slide axis does zero web work. This is the smallest honest path to
"graceful" that does not repeat the banned per-frame child resize.

## Rejected approaches

- Fade — layered-alpha fragility with WebView2.
- Height-collapse close — per-frame Chromium reflow for an 83 ms effect.
- Status quo — owner-reported broken feel.

## Compatibility concerns

- Unpinned re-anchor math must freeze the tray-adjacent edge during growth (left/right taskbar:
  freeze top, animate height — no per-frame re-centering).
- `_showGeneration` guards: a show during a close animation must cancel it before `Show()`
  (else the slide-in's terminal `Hide()` would eat the show).
- Reduced-motion / forced-colors / remote-session environments: animations skip; 0012 instant
  steps remain the floor.
- `WM_DPICHANGED` mid-animation: cancel animation, adopt OS bounds (rare; validated live).

## Testing implications

- Animator with fake tick source: easing sampling, retarget mid-flight (latest wins), cancel
  snaps, completion callbacks fire once, reduced-motion bypass.
- Dock pin/unpin: child bounds set once per animation, restored after.
- Placement: frozen-edge assertions per taskbar edge.
- Existing suites (FrozenVisibleBounds live invariants, rapid-toggle, motion-flip, pairing
  pins) must stay green; `test-motion.ps1` gains the animator tests.
- Headed: feel at 100/125/150 % DPI; rapid toggle during close; grow during open.

## Open items

- Human acceptance of 167 ms growth / 83 ms close (owner's eyes) — rollback path is reverting
  to 0012 instant steps; the animator is a single isolated class.
- Left/right taskbar slide axis (horizontal) — implemented via the same tray-normal vector.
