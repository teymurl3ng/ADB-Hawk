# 0029 — Stable WinUI shell replacement

- **Date:** 2026-10-15
- **Status:** ADOPTED for implementation on 2026-10-15 after the owner instructed “let's begin”. M01 amends DESIGN §41 and ENGINEERING §23 with a deliberate authority-hash update. Production cutover remains conditional on the M02 prototype gate and later acceptance.
- **Resource amendment, 2026-10-15:** owner response “Allow up to 200 MiB for WinUI” authorizes a ≤200 MiB working-set ceiling for the complete integrated replacement, superseding its historical 120 MB target. Other acceptance thresholds remain unchanged. DESIGN §41.5 and ENGINEERING §23 record the amendment.
- **Supersedes:** the implementation proposal in decision 0028 and `docs/MOTION-SYSTEM-PLAN.md`. Their audit evidence remains applicable to the current WPF shell.
- **Context:** the owner explicitly requested an unconstrained recommendation and then a researched execution plan for another model. This document records the proposed contract so an executor does not have to reconcile competing directions from conversation history.

## Decision

Prefer a WinUI 3 shell, conditional on a passing feasibility prototype. The highest-confidence improvement is stable geometry: a 376 × 480 DIP panel, clamped to the work area before opening, with device list, details and pairing inside the same region. Content changes do not resize the open window. Replace the expanding bubble with a stationary 32 DIP indicator and tooltip.

Use a small shared motion policy over platform animations: 167 ms surface/navigation effects where the renderer proves them, 83 ms app feedback, immediate semantic/environment updates, live reduced-motion handling and generation-checked interruption. Do not retain stale device/QR pages to animate them out. Controls retain platform focus/press behavior. A bounded Acrylic panel is preferred; opaque fallbacks are explicitly defined if transparency or input quality fails.

Preserve the working backend and its reliability repairs. Extract UI-independent logic into `AdbView.Core`; keep the executable and current xUnit test project, with a fixture assertion path inside the actual WinUI application for XAML-dependent checks. Replace WinForms host plumbing with a narrow Win32 tray adapter and one WinUI application dispatcher. Distribute an unpackaged self-contained directory through the existing installer and portable ZIP.

The complete behavioral contract, ownership, fallback order, migration sequence, test map requirement, performance budgets and stop conditions are in [WINUI-REBUILD-RUNBOOK.md](../WINUI-REBUILD-RUNBOOK.md). Version/API evidence is in [the research note](../../research/2026-10-15-winui-shell-rebuild.md). Do not use this short decision as a substitute for those acceptance criteria.

## Evidence and uncertainty

Current implementation probes establish competing geometry writers, long reveal callback gaps and reduced-motion/lifetime defects. They do not establish that WPF is fundamentally incapable of smooth motion. Windows documentation supports Composition, native controls and bounded material, but does not prove this app's resource usage, frameless focus behavior, transparent hit testing or deployment. M02 must prove those assumptions before production migration. Stable-size WPF remains a credible alternative if the WinUI prototype fails.

## Contract adoption

M01 explicitly updates affected AGENTS/DESIGN/ENGINEERING/PHASES provisions and narrowly associated boundary tests. The owner authorized implementation after reviewing the plan. No additional plan-approval loop is required. The existing WPF application remains operational during the feasibility work.

Changes include WPF-only ownership, project-count restrictions, expanding cards, content-driven window sizing, bubble hover expansion, motion implementation, Windows App SDK dependency avoidance and single-file publishing. Update the design hash only alongside the intentional design-contract amendment. Preserve immediate snapshot truth, failure reporting, secrets, shared ADB citizenship, event-driven observation, vocabulary ownership and mandatory verification.

## Acceptance

M00–M12 in order, with recorded evidence. No production migration before the M02 feasibility gate passes. Every old behavior assertion has a retained or equivalent replacement check; package and headed UI validation are distinct from a headless smoke. Real-device, mixed-DPI, ARM64, clean-machine and soak results remain unverified until actually exercised. No public-release claim from an incomplete matrix.
