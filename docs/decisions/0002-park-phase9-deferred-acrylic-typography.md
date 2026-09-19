# 0002 — Park Phase 9 deferred Acrylic / typography visuals for Phase 11

- **Date:** 2026-09-19
- **Status:** PARKED — no code in Phase 9
- **Phase:** 9D → Phase 11

## Issue

During Phase 8/9 headed review, two visual issues remain outside Phase 9
reliability scope (PHASES.md Phase 9 out of scope: new UI states / redesign):

1. **Pinned + inactive light/native top strip** — Acrylic / DWM inactive-window
   tint can leave a light strip at the top of the flyout (related history in
   ENGINEERING.md §20 / V-2).
2. **Expanded-detail typography / contrast over Acrylic** — expanded device
   detail text contrast can read weak against the backdrop.

Neither is a §11 reliability failure. Fixing either would be visual/design
work under DESIGN.md authority, not chaos hardening.

## Decision

**Park only.** No product UI change in Phase 9. Track for **Phase 11** visual /
DPI sign-off (and acrylic inactive-window risk already noted in ENGINEERING).

## Rejected alternatives

- Patch spacing/contrast in Phase 9 — violates R-02 / Phase 9 non-goals.
- Silent redesign of DeviceCard expanded detail — requires DESIGN.md change.

## Follow-up

Phase 11 matrix must include headed review of pinned inactive Acrylic and
expanded-detail contrast at 100 % / 200 % DPI.
