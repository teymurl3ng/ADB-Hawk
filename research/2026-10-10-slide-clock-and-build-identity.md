# Slide clock starvation and the build-identity instrument: why "not fixed" was measured, not guessed

- **Date:** 2026-10-10
- **Author:** agent (opencode)
- **Phase:** post-Phase-12 hardening (native shell)
- **Status:** current

## Problem

After decision 0024 the owner reported the jitter was "actually not fixed". Two separate
things had to be caught before any code could be judged:

1. **Which binary was under test.** The running instance (started 15:02) launched from
   `AppData\Local\Programs\ADBView\AdbView.exe`, whose last-write time is **2026-10-06 10:11** —
   four days before decision 0023 and the 0024 fixes. Its log session shows pre-0023 slide
   lines (no tick-quality accounting) and the full pre-0023 defect set. The owner's report was
   measured against a stale binary. The repo already knew this failure mode ("stale binaries
   masqueraded as unfixed bugs", forensics 2026-10-03) and had an instrument for it — the
   `AppHost identity` startup log line — but that line logs the assembly version, which is
   `0.1.0.0` for every build. **The instrument cannot distinguish builds.**
2. **A real, measured pacing defect in the current build.** Driving the reveal from
   `CompositionTarget.Rendering` (`CompositionTickSource`, sanctioned by decision 0023) delivers
   *irregular* ticks: the same flights measured 4–10 applied frames with a 16–47 ms worst tick
   gap depending on background UI activity (log evidence below). That is stall-then-jump motion.

This note establishes the mechanism from official sources plus measurement, records the rejected
alternatives, and grounds the two changes in decision 0025.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| `CompositionTarget.Rendering` event | https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.compositiontarget.rendering | netframework-3.0 … windowsdesktop-11.0 |
| How to: Render on a Per Frame Interval Using CompositionTarget | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget | ms.date 2017-03-30 |
| `DispatcherTimer` class | https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatchertimer | netframework-3.0 … windowsdesktop-11.0 |
| `DispatcherPriority` enum | https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatcherpriority | netframework-3.0 … windowsdesktop-11.0 |
| Optimizing Performance: Other Recommendations | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-other-recommendations | WPF |
| `SetWindowPos` function | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos | ms.date 2018-12-05, updated 2025-07-01 |
| `AnimateWindow` function | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-animatewindow | ms.date 2018-12-05 |

Measured evidence (implementation-observed on this machine, 2026-10-10, log
`%LOCALAPPDATA%\ADBView\logs\adbview-20261010.log`, build of commit `c433be7`):

```
16:15:56.766 started {X=1920,Y=691,470x314}->{X=1439,Y=691,470x314}   (open, first show)
16:15:56.935 completed 6 frames over a 167 ms flight; worst tick gap 47 ms
16:23:14.800 started {X=1439,...}->{X=1920,...}                       (close)
16:23:14.892 completed 5 frames over a 83 ms flight; worst tick gap 16 ms
16:23:15.790 started {X=1920,...}->{X=1439,...}                       (open, quiet)
16:23:15.966 completed 7 frames over a 167 ms flight; worst tick gap 32 ms
16:23:16.824 started {X=1439,...}->{X=1920,...}                       (close)
16:23:16.904 completed 4 frames over a 83 ms flight; worst tick gap 32 ms
16:23:17.843 started {X=1920,...}->{X=1439,...}                       (open)
16:23:18.024 completed 10 frames over a 167 ms flight; worst tick gap 16 ms
```

A 60 Hz composition clock would apply ~10 frames per 167 ms flight and ~5 per 83 ms flight with
a ~17 ms worst gap. The measured spread (4–10 frames, 16–47 ms) is the signature of a clock that
fires only when something else happens to render.

The same quantization was then measured on the dispatcher timer itself (same machine, same day):

| Timer interval | Observed cadence | Flights measured |
|---|---|---|
| 16 ms (decision 0023's fallback class) | locks into a 2-tick period: consistent 31 ms worst gap, 3–7 frames per flight | 5 flights, 16:40 session |
| 15 ms | ~15.6 ms with an occasional double period: worst gap 16–32 ms, 4–10 frames | 6 flights, 16:43 session |
| 10 ms | the tick itself: 10 frames / 16 ms worst gap on every quiet open flight; closes (run while visible, UI thread busy with metadata updates) 4–5 frames, 16–31 ms | 6 flights, 16:47 session |

The dispatcher's wait is quantized to the system timer tick (15.6 ms by default). An interval
at or above the tick slips past tick boundaries: 16 ms locks into a 31.2 ms period outright,
and 15 ms drifts 0.6 ms per tick until it eats a double period. An interval below the tick
(10 ms) makes every tick fire the timer, so the cadence is the tick itself — the smoothest
pacing available without raising the system timer resolution globally (`timeBeginPeriod`),
which was rejected as a system-wide side effect for a flyout slide.

## Version investigated

- .NET SDK 10.0.401, `net10.0-windows` (`windowsdesktop-10.0` docs moniker).
- Windows 11, OS build 10.0.26100, 125 % DPI (the machine under test).
- Target app: `AdbView` 0.1.0, native WPF shell, build of commit `c433be7` (decision 0024).

## Findings

**F1 · `CompositionTarget.Rendering` fires once per rendered frame — and a position-only window
move never dirties the visual tree.** The event docs: *"The Rendering event is raised each time
the application's scene is drawn."* The reveal applies its frames with `SetWindowPos`, which
moves the HWND without invalidating any WPF visual (the research note of 2026-10-07, F2/F3,
established DWM composites the unchanged surface at the new position). So during a pure slide,
renders happen only when something else invalidates: metadata-driven binding updates, the 1 s
age ticker, first-paint after `Show()`. The measured spread above is exactly that: the 10-frame
flight coincided with post-`Show()` repainting; the 4–7-frame flights rode on sparse incidental
renders. The animator's own accounting (decision 0023's tick-quality log line) is what makes
this visible — the instrument did its job.

**F2 · A `DispatcherTimer` wakes the message loop itself, so it ticks reliably even when WPF has
nothing to render.** `DispatcherTimer` docs: ticks are *"placed on the Dispatcher queue like
other operations"* and fire *"not before the time interval occurs"* — the pending tick is queued
work, and the dispatcher loop wakes for it. A 16 ms timer therefore ticks ~every 16 ms for as
long as the loop runs, independent of rendering. This is the clock the reveal actually needs.

**F3 · The 2026-10-07 objection to the timer does not apply at Render priority.** Decision 0023
moved off a `DispatcherTimer` because it ran at `DispatcherPriority.Send` (10), *"above Normal
(9) and WPF's Render (7), so each tick's geometry change could be dispatched ahead of the render
pass"*. The existing `DispatcherTimerTickSource` already uses `DispatcherPriority.Render` — the
tick runs in the render band and does not preempt the render pass. (At Render priority the tick
can still be delayed by higher-priority work; that only makes a tick late, and F4 covers why
lateness is harmless here.)

**F4 · Timer lateness is harmless because the animator's progress is wall-clock and its frames
are deduplicated.** `FlyoutRevealAnimator.Tick()` computes progress from `Environment.TickCount64`
and skips a frame identical to the last applied one (decision 0023). A late tick applies the
correct frame for the current time; an extra tick is a no-op. The timer's documented imprecision
(*"not guaranteed to execute exactly when the time interval occurs"*) therefore cannot make the
slide drift, double-apply, or overshoot — it can only make individual frames land a few
milliseconds late.

**F4b · The interval must sit below the system timer tick.** The dispatcher's wait is quantized
to the system timer tick (15.6 ms by default on this machine). Measured 2026-10-10: a 16 ms
interval locks into a 31.2 ms period (consistent 31 ms worst gap, 3–7 frames per flight); 15 ms
drifts until it eats an occasional double period (worst gap 16–32 ms); 10 ms fires on every tick
and the cadence is the tick itself — 10 frames / 16 ms worst gap on every quiet open flight.
Raising the system resolution (`timeBeginPeriod(1)`) would also work but is a system-wide side
effect, rejected for a flyout slide.

**F5 · The alternative to a reliable clock is forcing renders, which is strictly worse.** Keeping
`CompositionTickSource` and calling `InvalidateVisual` on the shell root every tick would make
`Rendering` fire per frame — but it re-renders the entire window every frame for the whole flight
to feed a clock that only needs to *apply* a rect (DWM presents the move without any WPF render,
per the 2026-10-07 note's F2/F3). That is CPU work with zero visual benefit.

**F6 · The build-identity instrument could not do its job.** The 2026-10-03 forensics comment in
`AppHost.Run` says the identity line exists so *"one log glance answers 'which build, which
theme'"* — but it logs `Assembly.GetName().Version`, which is `0.1.0.0` for every build
(local, published, installed). On 2026-10-10 the owner's "not fixed" report traced to an
installed exe last written 2026-10-06; the log could not have said so. The fields that *do*
distinguish builds are the executable path and its last-write (build) timestamp.

**F7 · Microsoft's shadow warning still bounds the whole reveal.** (Restated from 2026-10-07 F1.)
The slide animates a shadowed, rounded HWND by construction. Even pacing removes the
stall-then-jump artifact; it does not change DWM's shadow recomposition. If the owner still
reads the slide as jerky after this change, the remaining cause is this, and the recorded
rollback is instant open/close (decision 0013) — an owner-facing DESIGN §24 decision, not an
implementation tweak.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| `DispatcherTimer` at Render priority as the production clock | Reliable ~16 ms ticks independent of rendering; no extra renders; wall-clock progress makes lateness harmless; the class already exists | Ticks can be delayed by higher-priority work under load (invisible at this cadence) |
| Keep `CompositionTickSource`, force a render per tick | Stays on the "compositor-aligned" hook | Re-renders the whole window every frame during the flight (F5); CPU for zero benefit |
| Win32 `SetTimer` + HWND message | No dispatcher dependency | More plumbing (message pump into the animator) for identical semantics; WPF window already has a dispatcher loop |
| Animate window bounds with a WPF storyboard | "Native" animation | WPF cannot animate HWND bounds; storyboards animate visual-tree properties only |
| Accept the variance (do nothing) | Zero risk | The measured 4–10 frame / 16–47 ms spread is the stall-then-jump jitter the owner reports |
| Identity line: log the assembly informational version | One-line change | Requires a version bump per build to be meaningful; the repo versions releases, not every build |
| Identity line: log exe path + build timestamp | Distinguishes every build with zero build-chain changes; survives reinstalls and side-by-side copies | Filesystem stat at startup (trivial) |

## Chosen approach

1. **The reveal's production clock is `DispatcherTimerTickSource`** — a `DispatcherTimer` at
   `DispatcherPriority.Render` with a **10 ms interval** (below the 15.6 ms system timer tick, so
   the cadence is the tick itself; F4b), wired in `AppHost` in place of `CompositionTickSource`.
   `CompositionTickSource` stays as the documented alternative for environments where a
   dispatcher timer must not run. ENGINEERING §10.4 is amended to name the dispatcher timer as
   the sanctioned window-bounds clock (R-05's ENGINEERING edit + this research note).
2. **The identity line carries the build stamp**: `AppHost.BuildIdentity()` returns
   `version=… path=… built=yyyy-MM-dd HH:mm` (the executable's last-write time), and the startup
   log line uses it. One log glance now answers "which build is running" — the exact question
   whose absence produced the 2026-10-10 false "not fixed".

## Why

The measured defect is a clock that only fires when something else renders; the fix is a clock
that fires on its own, at a priority that cannot preempt rendering, with lateness made harmless
by wall-clock progress and frame dedup. No new timer is introduced (the class already exists;
the reveal clock remains the single sanctioned per-frame loop, decision 0023's exception, now
driven by the dispatcher timer), no new dependency, no DESIGN change, and the animator's
contract (position-only, retargeting, dedup, single-writer) is untouched — it is tick-source
agnostic (`ITickSource`).

## Rejected approaches

- **Forcing a render per tick to keep the composition clock.** Pays a full-window re-render every
  frame to satisfy a clock that only needs to apply a rect (F5).
- **Win32 `SetTimer`.** Identical semantics with more plumbing (F2 vs. the existing dispatcher
  timer infrastructure).
- **Bumping the assembly version per build to fix the identity line.** Requires build-chain
  changes for what a file timestamp already provides (F6).
- **Treating the owner's report as a code regression without checking the binary.** The 15:02
  session ran a 2026-10-06 installed exe; no code change could have shown up in it.

## Compatibility concerns

- **Reduced motion** bypasses the animator entirely at the call sites (unchanged); the clock only
  runs while a flight is in flight.
- **Fixture mode** constructs no presenter (unchanged); the clock choice is production wiring.
- **Dispatcher starvation.** At Render priority the tick can be delayed by Normal/DataBind work
  (e.g., a metadata burst). Progress is wall-clock, so a delayed tick applies the correct frame;
  the worst case is a slightly uneven frame under load, never a wrong frame, a stall past the
  flight's end, or a dropped completion (the completion fires from the tick that observes
  progress ≥ 1, and the timer keeps ticking until then).
- **The animator's tests** are tick-source agnostic (`FakeTickSource`); no test changes are
  needed for the clock swap. The identity line gains one shape test.

## Testing implications

- `AppHostIdentityTests`: `AppHost.BuildIdentity()` carries `version=`, `path=`, and a `built=`
  stamp that is either a parseable `yyyy-MM-dd HH:mm` timestamp or the explicit `unknown`.
- Live re-measurement (this machine, device connected), recorded 2026-10-10 16:47 session with
  the 10 ms interval: quiet open flights apply **10 frames / 167 ms with a 16 ms worst tick gap
  on every flight**; close flights (which run while visible, with metadata-driven UI work at
  Render priority ahead of the tick) apply 4–5 frames / 83 ms with a 16–31 ms worst gap.
  Recorded in `docs/DEVICE-TEST-MATRIX.md`.
- The stale-binary catch itself: the identity line of a running instance now names the exe path
  and build stamp; compare against the file on disk.
- `scripts/verify.ps1` green (R-12), including the timer scan (the clock stays in `NativeUi/`,
  inside decision 0023's sanctioned window-bounds exception, now named in §10.4).

## Open items

- If the evenly-paced slide still reads as jerky to the owner's eye, the remaining cause is the
  shadowed-HWND slide itself (F7) — the rollback is instant open/close (decision 0013's
  rollback), an owner-facing DESIGN §24 decision.
- The expand/collapse height step is the frozen instant-step contract (DESIGN §24/952,
  decisions 0012/0021); the 306↔391 steps in the owner's 15:02 session were expand/collapse
  toggles plus metadata rows arriving. Changing that feel needs a DESIGN amendment.
- The owner must run a current build: the installed copy at `AppData\Local\Programs\ADBView` is
  from 2026-10-06 and predates decisions 0023 and 0024. Reinstall/repackage or run the repo's
  Release output directly.
