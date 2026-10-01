# Flyout live content-driven sizing (step-resize while visible)

- **Date:** 2026-10-02
- **Author:** agent (flyout sizing rework; decision 0012 amending 0005)
- **Phase:** post-Phase-12 usability (flyout sizing)
- **Status:** current

## Problem

The flyout's height is flaky: launches present sometimes tall enough for three device cards,
sometimes short enough for one (the rest behind a scroll), and growth while visible — new
devices, card expansion — scrolls instead of resizing. The owner reports it as "does not feel
stable" and asks for natural, logical expansion. Root cause (pipeline audit,
2026-10-02): the presented height is whatever the **single first** content-size measurement saw
at a launcher-timing-dependent instant (WebView2 warm vs cold racing ADB tracker delivery), and
decision 0005 then freezes the HWND for the whole visible session, so every later correction is
deferred to the next hide→show.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Microsoft Learn — WebView2 threading model / PostWebMessageAsJson | https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model | fetched 2026-10-02 |
| In-repo evidence | research/2026-09-20-prephase10-runtime-diagnosis.md (child-resize counts A/B/C; ±2 px scrollbar fights; pair-code geometry contract) | current |
| In-repo evidence | research/2026-09-20-prephase10-acceptance-caption-motion-theme-serial-startup.md (rAF-in-hidden-WebView throttling; serial-startup deadlock fix) | current |
| In-repo decisions | 0003 (superseded animated FLIP), 0005 (frozen visible HWND), 0006 (scrollbar-gutter both-edges) | current |
| Pipeline audit | 2026-10-02 audit (6 races; constants inventory; single control point) | this note's basis |

## Version investigated

Windows 11 26100 · WebView2 1.0.4191.47 (Evergreen) · .NET 10 · Chromium web renderer (WebView2).

## Findings

1. **What 0005 actually fixed.** The pre-0005 failure was **animated** cross-layer resizing: a
   FLIP animator + `StructuralSizeAnimator` resized the WebView2 (`Dock=Fill`) every frame, so
   each expand/collapse drove ~12 browser relayouts racing the HWND; simultaneous ±2 px card
   height changes (digit-entry cursor ring, scrollbar threshold) created resize/scrollbar
   feedback fights; rapid toggling blanked content. Measured A/B/C table: animation mode 12/11
   child resizes per expand/collapse; final-size-once and fixed-HWND modes 0/0.
2. **Step-resize is categorically different from the failed animation.** A step applies one
   native resize per *settled content change* (frontend already coalesces equal heights; the
   clamp at 560 caps overflow; `scrollbar-gutter: stable both-edges` keeps content width
   constant so scrollbar appearance cannot change measurement width; the pairing card's frozen
   outer geometry means digit entry produces zero height deltas). The feedback loop requires a
   measurement that *depends on the window size*; the only such dependency is the scrollbar
   gutter, which is pinned stable, and the work-area clamp, which is monotone (applies once,
   clamped, stable).
3. **WebView2 resize semantics.** The CoreWebView2 WinForms control with `Dock=Fill` reflows
   after the HWND resize (WM_SIZE → layout → browser viewport → DOM → ResizeObserver). Each
   step therefore produces at most one follow-up report; when the reported height equals the
   applied height the native side suppresses duplicates (existing `ApplyImmediateResize`
   guard) — convergence in ≤2 reports, no oscillation.
4. **Threading.** `reportContentSize` arrives on the WebView2 UI thread and is marshaled to the
   WinForms thread (existing `TryMarshalToFlyoutUi`); steps are applied on the WinForms thread.
   No timers needed — reports are event-driven (ResizeObserver + snapshot commits).
5. **Races named by the audit** (first-report-wins show gate `>= 1` sample; triple-schedule
   mid-commit sampling; font reflow frozen out; hidden convergence after the gate; 500 px
   placeholder; manual show leaving the pending flag; DPI override without bookkeeping) are all
   *presentation-time* problems. With live step-resize they become self-correcting: the window
   presents at its best-known height and converges to truth within one or two reports. No
   settle-gating is needed; added gating would delay first paint and still be weaker than
   convergence.
6. **The 560 ceiling stabilizes the system.** Content taller than the clamp scrolls inside the
   stable container; `contentEl.offsetHeight` (the inner wrapper) is unclamped, so reports stay
   flat at >560 and the native height is stable at 560.
7. **Regulatory conflict to resolve.** DESIGN §24/§25/§28 + decision 0005 freeze the visible
   HWND; §4 simultaneously demands "grow only as required". Decision 0012 resolves this in favor
   of live step-resize while retaining every anti-churn guard: 0 ms steps (no interpolation, no
   FLIP, no animation), no timers, pairing/offer frozen geometries unchanged, scroll only past
   560.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Keep 0005 freeze; add settle gating before first show | No contract change | Does not fix growth-while-visible (scroll for details remains); slower first paint; still racey (two equal samples ≠ settled) |
| Animated visible resize (167–333 ms) | Smooth feel | The twice-failed path (0003→0005): blank frames, cross-layer jitter; rejected |
| Resize only on discrete events (device add/remove), never on expand | Fewer resizes | Inconsistent: expand is exactly when the user needs the window to grow; event classification duplicates state knowledge in the host (R-07 violation) |
| Fixed height always | Dead simple | Wastes screen for 1 device, scrolls for many — the original complaint |

## Chosen approach

1. `FlyoutInteractionPolicy`: the freeze becomes a **height-comparison policy** — apply a
   content-driven resize whenever the clamped reported height differs from the applied height,
   visible or hidden (single policy function, single call site preserved).
2. `AppHost.ResizeFlyout`: while visible, apply the step and re-place (pinned keeps X/Y;
   unpinned re-anchors to the tray with work-area clamp). Hidden path unchanged (priming +
   convergence). Manual show clears a dangling pending-initial-show.
3. Steps are instant (0 ms). No timers, no animation, no new dependencies. Frontend unchanged
   (it already reports continuously and coalesces equals).
4. `WM_DPICHANGED` keeps OS-suggested bounds and is followed by a forced re-measure request
   (report flow already follows via ResizeObserver; bookkeeping aligned by adopting applied
   height).
5. Validation: `FrozenVisibleBoundsTests` rewritten as live-resize invariants; mutation M1 and
   `test-motion.ps1` updated to the new policy; `centering.spec.ts`, `rapid-toggle`,
   `motion-flip`, pairing geometry pins must stay green; headed live validation at
   100/125/150 % DPI.

## Why

One policy change fixes all six named races by construction (convergence instead of freezing),
removes the scroll-for-details complaint, and preserves the reliability lessons that motivated
0005 by never animating and never fighting per-frame. The only user-visible motion is the
window instantly matching its content — 0 ms truth.

## Rejected approaches

- Animated resize — twice failed human acceptance (0003/0005 history); rejected without a new
  validation cycle.
- Settle gating only — slower and strictly weaker than convergence.
- Event-classified resizing (only on device add/remove) — duplicates semantic knowledge in the
  host; R-07 violation.

## Compatibility concerns

- Hidden priming must keep the window at 376 epx width before first measure (WinForms default
  size would inflate heights) — preserved.
- Unpinned placement: growth re-anchors to the tray edge; near work-area edges the existing
  clamp trims Y (documented, not a regression).
- Rapid content flurries (device connect + metadata + battery within ~1 s) produce a few steps,
  not per-frame churn; the visual baseline is unaffected because browser-side layout is
  unchanged.
- 0005's recorded evidence (12 resizes/click) is not reproducible by this design: steps fire on
  settled content changes only.

## Testing implications

- Live invariants: visible + different height ⇒ applied once; same height ⇒ suppressed;
  100-report burst ⇒ bounded applies (no loop); pairing-digit reporting ⇒ zero height deltas
  (frontend pins) ⇒ zero applies.
- Update `test-motion.ps1` frozen-visible gate and `test-mutations-prephase10.ps1` MUTATION M1
  to the new policy text.
- `centering.spec.ts` (gutter symmetry), `rapid-toggle` (no blank/stale), `motion-flip`
  (instant layout) must stay green — they validate the browser side, which is unchanged.
- Headed validation: growth on device arrival, growth on expand, clamp+scroll >560, pin/unpin,
  100/125/150 % DPI, manual show before first report.

## Open items

- Live DPI matrix rows (100–200 %, mixed-DPI, four taskbar edges) — owner session.
- Whether the 2-minute QR session expiry producing a shrink-step feels right headed (expected:
  yes, 0 ms truth).
