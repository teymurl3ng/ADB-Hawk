# Prompt for the implementation model

Date: 2026-10-17. M00–M07 passed; M08 is in progress; the final native-owner full gate passes (see status). Full gate: 1,067 cases and 29 headed fixtures / 833 assertions, 172 native assertions, 11 external-input observations and two inspected captures. All 86 prior C# tests, backend/WPF/prototype sources and 824 mapped methods retained. Next exact work: M08 live lifetime composition, startup/instance/persisted preferences, actual environment sources and physical checks. Do not advance to M09 or reopen renderer/backend policy. Preserve fixture isolation, every assertion and uncommitted work; see status and m08/native-owner-checkpoint.json.

Copy this prompt into the model that will implement it:

```text
Implement the ADB View shell rebuild in this repository using
docs/WINUI-REBUILD-RUNBOOK.md as the ordered execution plan.

First read docs/WINUI-RESEARCH-BASELINE.md, that runbook,
docs/WINUI-REBUILD-STATUS.md,
research/2026-10-15-winui-shell-rebuild.md,
docs/decisions/0029-stable-winui-shell-proposal.md, AGENTS.md,
DESIGN.md, ENGINEERING.md, PHASES.md and
docs/RELIABILITY-REPAIR-RESULTS.md.

This request authorizes the runbook's explicit design and architecture
changes, including the M01 contract amendments. Preserve its security,
reliability and product-scope invariants. Do not ask for plan approval again.

Start with the single next item in WINUI-RESEARCH-BASELINE.md and status.
Record its question, named file scope, evidence, at most two experiments
and the 60-minute investigation deadline before editing. Stop with an
explicit finding or unresolved result when its bound is reached; do not
automatically expand into another host/provider/rendering experiment.
The baseline narrows execution; all existing acceptance requirements remain.

Work at the first incomplete package in WINUI-REBUILD-STATUS.md.
Complete work packages in order, using their allowed scope, tests and
evidence. M02 must pass before production migration. Continue after a
passing checkpoint. If an assumption fails, use only the documented
fallbacks, record the evidence and resolve the specific failure; do not
silently change frameworks, use previews, weaken tests or raise budgets.

The working tree contains previous uncommitted reliability repairs.
Protect and retain them. Do not reset to HEAD, rewrite the ADB backend,
kill/reconfigure shared ADB, add polling or implement the superseded
WPF animation plan. Preserve all existing actions and vocabulary.

Use the runbook's explicit decisions: stable panel, list/detail/pairing
navigation, stationary bubble, immediate latest-snapshot truth, platform
animations with one shared policy, and self-contained folder deployment.
Do not invent API names or claim unrun tests. Verify stable APIs by
compiling the prototype before adopting them in production.

Maintain the old-to-new test assertion map. Keep pure/backend tests in
xUnit and verify XAML behavior inside the real WinUI application UI
thread. Run scripts/verify.ps1 at the required checkpoints; launch the
Release UI separately from smoke tests. Record captures and actual
presentation measurements for animation claims.

Update docs/WINUI-REBUILD-STATUS.md after each checkpoint with commands,
results, artifact paths, remaining gaps and the next exact step. At a
session boundary leave a resumable checkpoint. Unavailable hardware
and long soak runs remain NOT EXERCISED, never PASS.

Do not create a new chat, delegate work, replace the installed app,
commit unrelated files or publish a release unless separately requested.
Report implementation progress and acceptance gaps accurately.
```

For a bounded implementation session, append: `Execute through M02 first. Preserve the current production shell and report the prototype gate results.` For full execution, use the prompt without that sentence; required external/hardware evidence may still prevent release acceptance.

The model name and reasoning setting are chosen by the person starting the session. This document does not switch them automatically or assert that a particular setting can complete every prototype investigation without assistance.



