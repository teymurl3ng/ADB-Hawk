# 0028 — Proposed unified native motion system

> **Superseded proposal (2026-10-15):** [decision 0029](0029-stable-winui-shell-proposal.md) and [WINUI-REBUILD-RUNBOOK.md](../WINUI-REBUILD-RUNBOOK.md) replace this implementation direction following the owner's unconstrained redesign request. The historical audit evidence is retained. Neither proposal has changed DESIGN.md yet.

Date: 2026-10-15. Status: PROPOSED; no DESIGN.md or authority hash change made.

## Reason

The owner requests a universal replacement for jittery reveal, expansion, collapse, and other interactions. The current build records 141/328 ms callback gaps during nominal 167 ms reveals. Expansion is deliberately instantaneous under DESIGN §§24–25. Runtime control probes also show that the existing reduced-motion gate leaves local card durations intact and does not stop an already-started pairing spinner.

The detailed evidence, phased implementation, and acceptance criteria are in [MOTION-SYSTEM-PLAN.md](../MOTION-SYSTEM-PLAN.md). External integration research is in [2026-10-15-unified-native-motion.md](../../research/2026-10-15-unified-native-motion.md).

## Proposed contract changes

1. Separate immediate semantic truth from visual geometry. Text, device presence, QR validity, command availability, and accessibility state adopt the latest snapshot immediately. Presentation coordinates, outline, clips, and current-value opacity may converge over a bounded transition. No stale device/QR ghost is retained to animate removal.
2. Replace full-HWND travel with a short, tray-directed visual reveal and surface fade: initially 12 DIP, 167 ms open, 83 ms close. Apply to the whole visible surface, including its outline; the invisible native envelope is not product chrome.
3. Permit measured geometry transitions **inside one WPF surface**, including render-only reflow and clip animation for disclosure. Supersede the instant-only expansion/height examples in §§24–25 and §28. Continue to prohibit per-frame HWND sizing, per-frame layout-tree measurement, text/QR scaling, springs, bounce, and overshoot. Keep the 376 DIP width and 560 DIP cap.
4. Keep the existing 0/83/167/333 ms token vocabulary and nominal point-to-point curve; 333 stays reserved. Define partial/retargeted segments as portions of an authorized transition, not new arbitrary timing values. Define current-value continuity and cancellation centrally, including same-target no-op and reversal behavior.
5. Allow a bounded transparent native host with an opaque WPF visual surface, subject to the prototype's quality, input, focus, performance, and DPI gates. This changes existing decisions' no-layered-window and DWM-only corner/shadow implementation restrictions. Preserve approved visible material, geometry, and shadow character; no new user-facing surface. If the prototype fails, record another architecture decision rather than silently changing renderer or shipping a visibly oversized rectangle.
6. Make reduced motion a live runtime policy for both windows, including in-flight effects and busy indicators. Replace the duplicated ControlsReduced mechanism after migration. Explicitly classify the existing 850 ms linear progress spinner as a visibility- and activity-bound busy indicator, outside one-shot transition durations; it is not an idle decorative loop.
7. Consolidate row 29/30 engineering ownership, animation scheduling, target measurement, and native geometry writes. Retire the old per-frame dispatcher slide and per-layout self-sizing. Make hidden age/busy work stop. Preserve shared ADB and backend lifecycle behavior.

Affected authorities: DESIGN §§24–26 and the §28 instant-layout wording; ENGINEERING §§10.1/10.4, reliability rows 29/30, and relevant shell acceptance tests; AGENTS R-02/R-05 descriptions where they name the old mechanism. Decisions 0012/0013/0021/0023/0024/0025 remain historical explanations, with their presentation mechanics superseded only after adoption. Earlier phase acceptance is not retroactively declared complete.

## Invariants

One app project, one C# test project, WPF product UI, exactly the existing two windows, unchanged canonical components and product scope, no dependency by default, no ADB polling, immediate backend truth, native pinned drag/nonactivating bubble, central dynamic wording, and accurate reduced-motion behavior.

## Decision gate

This document is a concrete proposal, not an assertion that the contract has been amended or the renderer proven. The current user request is to analyze and make a replacement plan. Production implementation starts with the documented contract update and the rendering spike described in that plan. No additional approval was requested during this planning task.
