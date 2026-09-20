# 0003 — Structural motion: Fluent Point-to-Point (333 ms)

- **Date:** 2026-09-20
- **Status:** SUPERSEDED by [0005](0005-v1-frozen-visible-hwnd-no-structural-resize.md) — v1 no longer animates native HWND height while visible
- **Phase:** pre-Phase-10 acceptance (historical)

## Issue

Human acceptance repeatedly rejected structural expand/collapse motion that
used the Pass B.1 curve `cubic-bezier(0.1, 0.9, 0.2, 1)` at **250 ms**. The
FLIP architecture was geometrically stable in automation, but the trajectory
felt front-loaded (“goes crazy” / rushes into place).

## Decision (historical)

Revised `DESIGN.md` §24 timing/easing toward Fluent Point-to-Point (333 ms /
`cubic-bezier(0.55, 0.55, 0, 1)`) and required frontend FLIP + native
`StructuralSizeAnimator` to share those tokens.

## Supersession

Repeated human rejection of cross-layer structural HWND animation led to
decision **0005**: freeze native flyout height for the visible session;
interactive disclosure is web-only (83/167 ms feedback). FLIP coordinating
native resize and `StructuralSizeAnimator` were removed for v1.

Easing token `cubic-bezier(0.55, 0.55, 0, 1)` and durations 0/83/167/(333 reserved)
remain valid; 333 ms is not used for interactive native resize in v1.

## Rejected alternatives (at the time)

- Further patches to CSS layout-height ↔ ResizeObserver loops
- Keeping 250 ms / old easing
- Spring/bounce/overshoot (forbidden by DESIGN §24)
