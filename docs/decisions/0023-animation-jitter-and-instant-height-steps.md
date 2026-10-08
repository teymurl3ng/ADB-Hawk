# 0023 — Animation jitter: single-writer window bounds, a compositor-aligned tick, the frozen curve in XAML, and DESIGN §24 reconciled to instant height steps

- **Date:** 2026-10-07
- **Companion research:** research/2026-10-07-animation-jitter-compositor-alignment.md
- **Amends:** DESIGN.md §24 (the v1 interactive structural policy, the examples list, and the
  "No" list); ENGINEERING.md §9.2, §10.4, §11. Supersedes nothing.
- **Basis:** the owner reported that the shell's animations are "quite jittery" (flyout sliding
  out of and into the tray; device details opening and closing) and asked why, whether the
  animations should be slowed down, and whether a window alpha should be added so a resize does
  not expose background.

## Problem

Five causes were established by reading the code, three of them in the shell's own control flow.

1. **Two writers owned one window rect, with no mutual exclusion.** `FlyoutRevealAnimator` wrote
   X/Y/W/H every 16 ms; `ApplySelfSizedHeight()` was attached to `shell.ShellRoot.LayoutUpdated`,
   which fires on every layout pass — *including the passes the animator's own `SetWindowPos`
   provoked*. When the self-sizer decided a step was warranted (its threshold is 0.5 DIP against
   `ActualHeight`, a value the animator was actively changing), it issued its own `SetWindowPos`
   **and** assigned the WPF `Height`/`Top` properties — a second application of the same rect.
2. **The tick was not aligned to the compositor and outranked rendering.** The 16 ms
   `DispatcherTimer` ran at `DispatcherPriority.Send` (10), above `Normal` (9) and `Render` (7),
   so each tick's geometry change could be dispatched ahead of the render pass that would present
   the previous frame.
3. **The self-sizer did heavy work on every layout pass** — a full root
   `Measure(new Size(width, PositiveInfinity))` plus a Win32 taskbar/AppBar query — work that
   starves the very tick driving the animation. The repository had already documented one stall
   of this class ("a 3 s slide on first show").
4. **No XAML animation used the frozen curve.** DESIGN §24 declares the shared production easing
   as `cubic-bezier(0.55, 0.55, 0, 1)`; there was no `EasingFunction`, `KeySpline`, or `CubicEase`
   anywhere in `src/`, so hover, press, the chevron rotation, the details fade and both Copied
   badges all used WPF's default linear interpolation.
5. **The window seeded `Background = Brushes.Black`**, so a frame composited before the themed
   opaque brush painted could show black — the visible form of "a resize exposes background",
   which the platform mechanism explains: only the newly exposed strip is invalidated, and it is
   painted later.

The open/close slide also animates a *shadowed, rounded* top-level window by construction (it
keeps `WS_CAPTION` for DWM framing and opts into `DWMWCP_ROUNDSMALL`). Microsoft's `AnimateWindow`
page states plainly: *"Avoid animating a window that has a drop shadow because it produces
visually distracting, jerky animations."*

Separately, **DESIGN §24 contradicted the shipped implementation.** §24 lines 919–926 mandate an
animated 167 ms height step written for the WebView era ("The WebView child is pinned to its
final size once per animation — the window clips/reveals it"), and line 946 lists "device
insertion/removal: 167 ms (native height step animation)". Decisions 0012 and 0021 and
ENGINEERING §9.2 ship **instant** steps and record the animation as unimplemented. Decision 0012
records why: animated visible resize *"failed human acceptance twice (0003 → 0005); not retried"*.

## Decisions

1. **One writer per window rect.** `FlyoutRevealAnimator.IsAnimating` becomes the gate:
   `ApplySelfSizedHeight()` returns early while a bounds animation is in flight, and the WPF
   `Height`/`Top` properties are assigned only through one guarded path, never in addition to the
   `SetWindowPos` that already applied the rect. `AppHost` gains no new policy — it wires the
   existing animator state to the existing self-sizer.
2. **The open/close slide is position-only, and provably so.** Endpoints are derived from computed
   rects of identical size (never from the live window rect after a layout pass that may have
   stepped the height); the animator asserts and logs if a slide's width or height ever differs;
   a frame identical to the last applied one is not re-applied.
3. **Self-sizing gets out of the layout path.** The taskbar work-area query is hoisted out of
   per-pass evaluation and invalidated on DPI change, display change and taskbar change. The
   redundant root `Measure(PositiveInfinity)` is removed **only if** a fixture-rendered height test
   proves the `ScrollViewer` already measures unconstrained; otherwise it stays.
4. **The bounds animation is driven by `CompositionTarget.Rendering`** — attached on `Start`,
   detached on `Stop`, per the WPF performance guidance (*"If you use this event, detach it at
   every opportunity"*). Progress remains wall-clock derived, so an extra callback is idempotent.
   The `DispatcherTimer` source is kept as a fallback at `DispatcherPriority.Render`, not `Send`.
   This is a **sanctioned exception** to ENGINEERING §10.4's ban, which is amended to distinguish
   the one window-bounds clock from content choreography.
5. **One `EasingFunctionBase` carries the frozen curve into XAML.** `NativeUi/Motion/BezierEase`
   delegates to the existing `Host.Easing.FluentPointToPoint`, so the storyboards and the code
   animator cannot disagree about `cubic-bezier(0.55, 0.55, 0, 1)`. Because the frozen curve is a
   single asymmetric curve, the class fixes its `EasingMode` in the constructor rather than
   exposing the `EaseIn`/`EaseOut`/`EaseInOut` composition. `Formatting`: the animation remains a
   plain `DoubleAnimation` on the existing `Duration` tokens — which preserves the dictionary-swap
   reduced-motion mechanism and the To-only mid-flight retargeting `DESIGN.md` §26 requires.
   (`SplineDoubleKeyFrame` was rejected: an absolute `KeyTime` decouples the animation from the
   swappable token, and an explicit `0%` keyframe breaks retargeting.)
6. **The window background is seeded from the themed opaque brush**, not `Brushes.Black`.
7. **DESIGN §24 is reconciled to the instant-step contract.** The v1 interactive structural policy
   paragraph is rewritten for the native shell, the `device insertion/removal` example is
   corrected, and the stale WebView-child entries in the "No" list are replaced by an explicit ban
   on **per-frame HWND resize**. `docs/DESIGN.sha256` is re-hashed in the same change, as R-01
   requires.
8. **Reduced-motion parity is preserved.** `ControlsReduced.xaml` keeps its key parity and stays
   free of `BeginStoryboard`/`DoubleAnimation`; the reduced twin's hover/press triggers gain the
   instant setters they were missing (an empty trigger is *no* behaviour, not the "instant
   behaviour" DESIGN §24 specifies for reduced motion).

## Non-changes

- **The 83 / 167 / 333 ms token values are untouched.** The owner asked whether to slow the app
  down; the answer is no. The durations are frozen DESIGN §24 tokens already at the fast end of
  the Fluent scale (`ControlFastAnimationDuration` 167 ms, `ControlFasterAnimationDuration`
  83 ms), they are not a cause of the jitter, and a longer duration makes jitter *more* visible.
- **No window transparency, no layered window, no whole-window alpha.** The owner asked whether an
  alpha should be added so a resize does not expose background. Recording the reason so it is not
  re-litigated: per-pixel-alpha layering windows *"cannot ever be rounded"* on Windows 11 (which
  would forfeit the `DWMWCP_ROUNDSMALL` corners the shell opts into); WPF's position is that
  layered windows are for per-pixel alpha and *"Constant alpha and color keys are not supported"*,
  and the framework's constant-alpha branch is commented out; `AllowsTransparency` requires
  `WindowStyle=None` and therefore conflicts with the `WS_CAPTION` + `WM_NCCALCSIZE` framing and
  the light-dismiss hit testing; and it contradicts the opaque-surface contract (decision 0004,
  DESIGN §8/§22). The correct fix for a resize exposing background is to **stop resizing the HWND
  per frame**, which decisions 1–3 do.
- No `WS_EX_COMPOSITED` (a GDI double-buffering style; WPF renders through DirectX and manages its
  own extended styles).
- No new NuGet package, no animation library, no new surface, no new component variant, no new
  dependency of any kind. No new *timer*: the sanctioned animation clock already exists and only
  changes its source.
- DESIGN §25 (grid `0fr`/`1fr` without animating rows) and the 560 epx ceiling are honoured.
- The pairing spinner's hardcoded 850 ms `RepeatBehavior="Forever"` loop stays as it is. It is not
  gated by reduced motion and is covered by no `Motion.*` token — a real but separate defect whose
  fix needs either a fifth token or a view-model flag, i.e. its own §24 decision. Recorded in the
  research note's open items rather than smuggled into this change.

## Acceptance

- `FlyoutRevealAnimatorTests`: the slide applies position-only frames (constant width/height), an
  identical frame is not re-applied, and the animator reports itself as the sole writer while
  running.
- A self-sizing test: `ApplySelfSizedHeight` does not step while a bounds animation is in flight
  (the oscillation this decision removes).
- `EasingTests` / a `BezierEase` probe test: the XAML curve equals `Easing.FluentPointToPoint`
  across a grid of progress values, exercising the `EasingMode` composition — so XAML and code
  cannot drift.
- `ReducedMotionDictionaryTests` stays green (key parity; no `BeginAnimation`/`DoubleAnimation` in
  the reduced file).
- `ShellSizingTests` and `FrozenVisibleBoundsTests` stay green (the instant-step contract is
  unchanged).
- `scripts/verify.ps1` green, plus a Release launch from a path that does not contain "smoke"
  (R-12), with the open/close slide and the expand/collapse eyeballed at 100 % and 200 % and
  recorded in `docs/DEVICE-TEST-MATRIX.md`.

## Open items

- If the aligned, single-writer slide still reads as jerky to the owner's eye, the honest next
  step is **no slide** (instant open/close) — which decision 0013 already named as its own
  rollback — not a longer slide. That is an owner-facing contract decision.
- DESIGN §24's "reserved token only" 333 ms and the deferred exit curves recorded by decision 0021
  remain untouched.
