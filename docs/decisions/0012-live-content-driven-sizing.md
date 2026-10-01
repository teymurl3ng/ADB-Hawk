# 0012 — Live content-driven sizing (amends 0005: instant step-resize while visible)

- **Date:** 2026-10-02
- **Status:** ACCEPTED — explicit human design-authority change (flyout usability)
- **Phase:** post-Phase-12 (flyout sizing rework)

## Issue

The flyout's presented height is flaky: launches show one device card or three depending on
WebView2/ADB launch timing, because the window presents at the **single first** content-size
measurement and decision 0005 then freezes the HWND for the visible session — every later
correction (devices arriving, card expansion, font reflow) becomes an internal scroll instead of
growth. Six audit-confirmed races compound the first sample (mid-commit measurement, font
reflow, hidden convergence after the gate, the 500 px placeholder, a dangling pending-show, DPI
override without bookkeeping). The owner reports the surface as unstable and requires natural,
logical expansion. §39 route: documented usability problem → contract change → implement.

## Decision

**While visible, the native HWND height follows the reported content height in instant steps.**
Specifics:

1. When a content-size report (clamped 120–560 epx; width stays 376 epx) differs from the
   applied height, apply **one native resize** immediately — 0 ms truth (R-06). No
   interpolation, no FLIP, no per-frame animation, no timers. Unchanged height ⇒ suppressed.
2. Placement follows the existing rules: pinned keeps X/Y; unpinned re-anchors to the tray with
   the work-area clamp. Content beyond 560 epx scrolls inside the stable scroll container
   (unchanged §4/§28); `scrollbar-gutter: stable both-edges` (0006) keeps measurement width
   constant, which is what prevents resize/scrollbar feedback.
3. The launch (first-report-wins) is no longer load-bearing: the window presents at its
   best-known height and converges within one or two reports. Hidden priming at 376 epx width
   before first measure is preserved.
4. Everything 0005 froze **in the web layer stays frozen**: instant grid 0fr/1fr disclosure, no
   CSS animation, pairing-card fixed outer geometry (digit entry produces zero height deltas —
   the pins stay). What 0005 banned was **animated cross-layer resizing**; this decision
   permits only settled **step** changes, which do not reproduce that failure mode (one native
   resize per real content change; the twice-failed animated path remains rejected).
5. A manual show before any report no longer leaves the pending-initial-show flag dangling;
   `WM_DPICHANGED` adopts the OS-suggested bounds and the subsequent report flow re-aligns
   bookkeeping.

## Contract changes

- DESIGN §24 ("frozen" → live 0 ms steps; the anti-animation prohibitions are retained
  verbatim), §25 ("freeze that native height for the open session" → step-follow; scroll only
  past 560), §28 (scrollbar rule now reads: the HWND follows content; the gutter rule stays),
  §4 wording aligned ("grow only as required" now happens while visible too). Hash updated in
  the same change.
- ENGINEERING §9.2 sizing bullet + §5.3 action comment rewritten; §11 row 29 records the
  content-driven visible resize (0 ms step, no animation, no timers).
- 0005 remains on record as the authority that banned **animation**; 0012 narrows its freeze to
  the animation ban plus step-resize semantics. FrozenVisibleBoundsTests are rewritten as live
  invariants; mutation M1 and `test-motion.ps1` pin the new policy text.

## Rejected alternatives

- Animated visible resize — failed human acceptance twice (0003 → 0005); not retried.
- Settle gating before first show — slower paint, strictly weaker than convergence.
- Event-classified resizing (host decides which events may resize) — duplicates semantic
  knowledge in the host (R-07 violation).

## Testing

- Live invariants (rewritten native tests): visible + different height ⇒ exactly one applied
  step; equal height ⇒ suppressed; 100-report burst ⇒ bounded applies with no oscillation.
- Pairing geometry pins (zero-delta digit entry), rapid-toggle, motion-flip, centering
  gutter-symmetry, and visual baselines must stay green (browser-side layout unchanged).
- Headed validation: growth on device arrival and card expansion; clamp + scroll beyond 560;
  pin/unpin placement; 100/125/150 % DPI; manual show before first report.
