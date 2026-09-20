# 0005 — v1 freezes native HWND height while the flyout is visible

- **Date:** 2026-09-20
- **Status:** ACCEPTED — explicit human design-authority change
- **Phase:** pre-Phase-10 stability-first simplification

## Issue

Multiple increasingly complex structural animation implementations
(FLIP + native `StructuralSizeAnimator` + Motion-B child choreography)
passed automated geometry tests but failed human visual acceptance.
Rapid expand/collapse could briefly blank or flicker content. Cross-layer
WebView2 ↔ native height animation proved visually unreliable for v1.

## Decision

**While the flyout is visible, the native HWND height is frozen.**

- No DeviceCard expand/collapse may resize the native flyout.
- No PairingCard phase change may resize the native flyout.
- No metadata/timestamp/battery update may resize the native flyout.
- No rapid interaction may resize the native flyout.

**Initial / next-show sizing only:**

When the flyout is about to become visible, measure natural content height,
`clamp(natural, minimum, 560)`, show at that height, then freeze until hide.
On the next hide → show cycle, measure again. That is the only ordinary
content-driven native resize path.

**Interactive disclosure is web-only:**

DeviceCard layout may change immediately. Restrained feedback only:
chevron / detail opacity 167 ms, button feedback 83 ms. No FLIP, no
grid-height / max-height / scaleY animation, no animation library.

Overflow that exceeds the frozen viewport scrolls inside a stable scroll
container. Do not resize HWND merely to remove a scrollbar.

## Reason

Repeated human acceptance proved cross-layer structural animation visually
unreliable. This is a deliberate reliability decision, not an unfinished
animation.

## Follow-up

- Update `DESIGN.md` §24 / §25 / §28 and `ENGINEERING.md` §9.2 sizing
- Update `docs/DESIGN.sha256` in the same change
- Remove StructuralSizeAnimator / FLIP / Motion-B interactive paths
- Gate: frozen-visible-bounds + rapid-toggle visual tests
