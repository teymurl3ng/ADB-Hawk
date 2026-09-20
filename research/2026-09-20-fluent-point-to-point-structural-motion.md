# Pre-Phase-10: Fluent Point-to-Point structural motion (333 ms)

- **Date:** 2026-09-20
- **Author:** agent
- **Phase:** pre-10 acceptance
- **Status:** current

## Question

Which structural duration and easing should ADB View use after humans
rejected Pass B.1’s front-loaded 250 ms curve while FLIP geometry was already
stable?

## Sources consulted

| Source | URL / location | Version / notes |
|---|---|---|
| Microsoft Fluent motion — Existing Elements / Point to Point | Fluent design motion guidance (Point to Point easing) | cubic-bezier(0.55, 0.55, 0, 1); standard durations 167 / 250 / 333 ms |
| `DESIGN.md` §24 (pre-change) | repo | 0 / 83 / 167 / 250 ms; B.1 easing |
| `docs/decisions/0003-structural-motion-fluent-point-to-point.md` | repo | ACCEPTED human design-authority change |

## Findings

1. Human verdict: geometrically stable FLIP still “goes crazy” / rushes with
   `cubic-bezier(0.1, 0.9, 0.2, 1)` at 250 ms (very front-loaded).
2. Fluent Point-to-Point easing `cubic-bezier(0.55, 0.55, 0, 1)` at **333 ms**
   keeps meaningful progress later in the curve (not ~complete by 50 ms).
3. Frontend CSS `--motion-structural` / `--motion-easing` and native
   `StructuralSizeAnimator` + `MotionEasing` must share identical constants.
4. Feedback remains 83 ms; state/detail fade remains 167 ms; reduced motion 0.

## Decision for this project

Adopt DESIGN §24 revision: structural **333 ms** +
`cubic-bezier(0.55, 0.55, 0, 1)`. Keep FLIP architecture unchanged.

## Rejected alternatives

- Tuning the rejected layout-height ↔ ResizeObserver loop
- Keeping 250 ms and only changing easing (insufficient vs human “too aggressive”)
- Spring / overshoot libraries

## Open questions / risks

Human perceptual acceptance still required after automated trajectory tests pass.
