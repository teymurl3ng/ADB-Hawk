# 0025 — The reveal's clock: a Render-priority dispatcher timer, and a build-identity line that can actually identify a build

- **Date:** 2026-10-10
- **Companion research:** research/2026-10-10-slide-clock-and-build-identity.md
- **Amends:** ENGINEERING.md §10.4 (the sanctioned window-bounds clock), §9.2, §11 row 30, and
  the file map. Amends decision 0023's choice of clock (not its single-writer, position-only,
  dedup, or curve decisions, which are unchanged). No DESIGN.md change.
- **Basis:** the owner reported the jitter was "actually not fixed" after decision 0024. Deep
  re-investigation with the app's own tick-quality logging produced two catches: the running
  instance was a four-day-old installed binary (so no fix could have shown up in it), and the
  current build's reveal had a measured frame-pacing defect — 4–10 applied frames per flight
  with a 16–47 ms worst tick gap, depending on background UI activity.

## Problem

1. **Stale binary under test.** The instance the owner tested (started 15:02) ran
   `AppData\Local\Programs\ADBView\AdbView.exe`, last written **2026-10-06 10:11** — before
   decision 0023 (2026-10-07/08) and decision 0024 (2026-10-10). Its log session shows pre-0023
   slide lines (no tick accounting) and the pre-0023 defect set (two writers of the window
   rect, Send-priority tick, linear easing). The repo's 2026-10-03 forensics had already recorded
   this failure mode ("stale binaries masqueraded as unfixed bugs") and built an instrument for
   it — the `AppHost identity` startup line — but that line logs the assembly version, which is
   `0.1.0.0` for every build. **The instrument could not distinguish builds.**
2. **Render-starved slide pacing in the current build.** Decision 0023 drove the reveal from
   `CompositionTarget.Rendering`. That event fires once per *rendered* frame, and a position-only
   `SetWindowPos` slide never dirties the visual tree — DWM presents the move without any WPF
   render. So during a pure slide, ticks rode on incidental renders (metadata-driven binding
   updates, the 1 s age ticker, first-paint after `Show()`). Measured on 2026-10-10 with the
   0024 build (log `adbview-20261010.log`, 16:15 and 16:23 sessions):

   | Flight | Frames | Duration | Worst tick gap |
   |---|---|---|---|
   | open (first show, metadata burst) | 6 | 167 ms | 47 ms |
   | close | 5 | 83 ms | 16 ms |
   | open (quiet) | 7 | 167 ms | 32 ms |
   | close | 4 | 83 ms | 32 ms |
   | open | 10 | 167 ms | 16 ms |

   A 60 Hz clock would apply ~10 frames per 167 ms and ~5 per 83 ms with a ~17 ms worst gap.
   The 4–10 frame spread is stall-then-jump motion: the jitter the owner sees.

   (For completeness, the owner's 15:02 session also showed the window stepping 306↔391 DIP
   every ~750 ms — those were expand/collapse toggles plus metadata rows arriving; the instant
   height step is the frozen DESIGN §24/952 contract and is unchanged by this decision.)

## Decisions

1. **The reveal's production clock is `DispatcherTimerTickSource`** — a `DispatcherTimer` at
   `DispatcherPriority.Render` with a **10 ms interval**, wired in `AppHost` in place of
   `CompositionTickSource`. A `DispatcherTimer` wakes the message loop itself, so it ticks
   reliably even when WPF has nothing to render — exactly the slide's case.
   `CompositionTickSource` stays as the documented alternative for environments where a
   dispatcher timer must not run. Decision 0023's objection to a dispatcher timer was its
   `Send` priority preempting the render pass; at `Render` priority the tick runs in the render
   band and cannot preempt it. The timer's documented imprecision is harmless: the animator
   derives progress from the wall clock and skips frames identical to the last applied one, so
   a late tick applies the correct frame and never drifts, double-applies, or drops the
   completion. The interval is 10 ms — deliberately below the 15.6 ms system timer tick:
   measured on 2026-10-10, a 16 ms interval locks into a 31.2 ms period (consistent 31 ms worst
   gap) and 15 ms still slips into occasional double periods, because the dispatcher's wait is
   tick-grid quantized; below the tick, every tick fires the timer and the cadence is the tick
   itself. ENGINEERING §10.4 is amended to name the dispatcher timer as the sanctioned
   window-bounds clock (R-05's required ENGINEERING edit, with the companion research note).
   No new timer is introduced — the class already exists; the reveal clock remains the single
   sanctioned per-frame loop.
2. **The identity line can identify a build.** `AppHost.BuildIdentity()` returns
   `version=… path=… built=yyyy-MM-dd HH:mm` — assembly version, executable path, and the
   executable's last-write (build) timestamp — and the startup log line uses it. The version
   alone is constant across builds; the path and build stamp are the fields that distinguish
   them. One log glance now answers "which build is running", which is the exact question whose
   absence produced the false "not fixed" on 2026-10-10.

## Non-changes

- **The animator's contract is untouched**: position-only translation, retargeting from the last
  applied frame, identical-frame dedup, single-writer gate, the frozen curve, 167/83 ms. It is
  tick-source agnostic (`ITickSource`); the existing `FlyoutRevealAnimatorTests` cover it
  unchanged.
- **No DESIGN.md edit.** Durations, easing, instant height steps, and the reduced-motion
  contract are all unchanged. Reduced motion still bypasses the animator at the call sites.
- **No new timer, dependency, surface, or component variant.** The clock swap reuses the existing
  `DispatcherTimerTickSource` (created by decision 0023 as the fallback).
- **The expand/collapse instant height step is unchanged** (frozen DESIGN §24/952 contract,
  decisions 0012/0021).

## Acceptance

- `AppHostIdentityTests`: `AppHost.BuildIdentity()` carries `version=`, `path=`, and a `built=`
  stamp that is a parseable timestamp or the explicit `unknown`.
- Existing `FlyoutRevealAnimatorTests`, `MotionCurveTests`, `ReducedMotionDictionaryTests`,
  `ShellMotionGateTests`, `ShellSizingTests` stay green.
- Live re-measurement on this machine (device connected), recorded 2026-10-10 16:47 session:
  quiet open flights apply **10 frames / 167 ms with a 16 ms worst tick gap on every flight**
  (previously 6–7 frames with 32–47 ms gaps when starved); close flights — which run while
  visible, with metadata-driven UI work ahead of the tick at Render priority — apply 4–5
  frames / 83 ms with a 16–31 ms worst gap. Recorded in `docs/DEVICE-TEST-MATRIX.md`.
- `scripts/verify.ps1` green (R-12), including the timer scan (the clock stays in `NativeUi/`,
  inside the sanctioned window-bounds exception, now named in §10.4).
- The Release exe launched from a non-smoke path with the new identity line; the log names the
  exe path and build stamp.

## Open items

- **The owner must run a current build.** The installed copy at `AppData\Local\Programs\ADBView`
  is from 2026-10-06 and predates decisions 0023 and 0024. Repackage/reinstall or run the repo's
  Release output directly; the identity line now makes the running build self-evident.
- If the evenly-paced slide still reads as jerky to the owner's eye, the remaining cause is the
  shadowed-HWND slide itself (Microsoft's `AnimateWindow` warning, research 2026-10-07 F1) — the
  rollback is instant open/close (decision 0013's rollback), an owner-facing DESIGN §24 decision.
- The spinner's 850 ms period remains a non-token value (decision 0024 open item; needs a
  DESIGN §24 amendment to tokenize).
