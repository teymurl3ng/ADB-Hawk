# 0004 — v1 flyout surface is intentional opaque (Acrylic deferred)

- **Date:** 2026-09-20
- **Status:** ACCEPTED — explicit human design-authority change
- **Phase:** pre-Phase-10 final acceptance

## Issue

Live Desktop Acrylic behind a transparent WebView2 child presents as a
multi-second stale “camera” background. Native Acrylic alone is live.
Composition-controller hosting is not available on the frozen WinForms +
`Microsoft.Web.WebView2` 1.0.4191.47 stack.

Shipping delayed/frozen acrylic fails product quality for v1.

## Decision

**v1 uses the already-governed intentional opaque dark/light surface**
(`DESIGN.md` §22), always:

- `shell.transparencyEnabled = false` in production
- native host paints solid theme surfaces (`#202329` / `#F3F4F6`)
- WebView2 uses matching opaque `DefaultBackgroundColor` (not transparent)
- frontend `data-opaque="true"` solidifies body/root to the same tokens
- DWM Transient Acrylic / `DwmExtendFrameIntoClientArea` not used for v1 product path
- rounded corners, B.1 geometry, light/dark palette, forced-colors preserved
- no CSS `backdrop-filter`, screenshot blur, repaint timers, DWM jiggle, or runtime pinning

Live Acrylic is **deferred architecture work**, not a v1 defect.

## Research

- `research/2026-09-20-acrylic-composition-host-proof.md`
- `research/2026-09-20-prephase10-runtime-diagnosis.md` (CASE B)

## Follow-up

- Update `DESIGN.md` §8 / §22 and `ENGINEERING.md` host backdrop rules
- Update `docs/DESIGN.sha256` in the same change
