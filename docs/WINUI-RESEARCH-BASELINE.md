# Research and scope baseline

**Latest bounded blocker item, 2026-10-17 19:24–19:35 Asia/Karachi:** one observed animated run passes (+6 handles), so prior failures remain unexplained; the comparison never reaches its workload because foreground assertion fails. Owner reports fatigue with desktop handoffs; no further retry. Temporary instrumentation/control fully restored by SHA256, ordinary Release build green, no product fix or full-gate pass. M08 remains incomplete. Current evidence and next technical decision are at the top of status and `m08/handles/checkpoint.json`.

**Latest bounded item, 2026-10-17 19:06–19:18 Asia/Karachi:** startup/forwarding implementation and multi-process probe pass, but full gate fails the unchanged M07 handle criterion on two runs (+33/+15 versus +10). 1,102 C# cases and build/format pass; final native startup-menu fixture/publish smoke and prescribed capture remain unexercised. Stop the item without resource repairs or more reruns; a separately scoped blocker decision is needed before further implementation. M08 remains incomplete, no M09 advance. See top of status and `m08/startup/`.

**Latest verified checkpoint, 2026-10-17 17:34 Asia/Karachi:** bounded unchanged-driver recheck passes 293 native assertions/11 input observations, followed by full green gate (1,090 C# cases, 29 fixtures / 954 assertions, publish smoke). No implementation, assertion, threshold or activation changes. Source/binaries match prior actual preference probe/captures; production identity unchanged. Preference slice verified, M08 incomplete; startup/forwarding is the next separate scope. Keep visual/resource findings with M09 and preserve all earlier failures. Pending records below are historical; current evidence is at the top of status and `preferences/verify-final-resumption.log`.

**Latest verification follow-up, 2026-10-17:** all 1,090 C# cases and final native preference/bubble checks pass, but external bubble input still fails after the fresh owner click. Latest helper cause is not captured; prior ShellExperienceHost obstruction does not prove this run's cause. Full gate/publish smoke incomplete. Further elevated diagnostics await approval-review availability (usage limit); no new implementation scope. See the top of status and `preferences/verify-after-codex-click.log`.

Date: 2026-10-15. Established at the owner's request before resuming implementation.

**Current resumption:** M00–M07 passed; M08 remains in progress after verified native-owner and live-composition slices (see status). Latest full gate: 1,076 cases, 29 headed fixtures / 833 assertions, unchanged native/input checks; actual isolated Release live USB list/open/normal shutdown and two inspected final captures. Next separate scope is isolated candidate preferences and per-command persistence; diagnosis/repair/multicast composition, startup/forwarding, actual environment sources and physical checks remain M08. No M09 advance. Prior Core/WPF/prototype sources, all 824 map rows and installed executable/Run identity remain preserved; one post-baseline fixture boundary is narrowly amended for the named live Host owner. See the top of [status](WINUI-REBUILD-STATUS.md) and m08/live-redo/checkpoint.json. The one-question, two-experiment, 60-minute bound and deferred-work discipline remain binding.

This is the working method for the remaining rebuild. The [runbook](WINUI-REBUILD-RUNBOOK.md) still owns M00–M12 acceptance; DESIGN §41 and ENGINEERING §23 still own product and architecture. This document narrows execution and research. It does not pass M02, change a requirement, or authorize a different framework.

**Pending preference implementation (2026-10-17):** 1,090 C# cases, final Release build/format and actual saved-value probe pass; the full gate remains red at the guarded external bubble input covered by ShellExperienceHost. The desktop prerequisite was requested. Resume final headed verification without starting another scope; additional bubble-binding checks await that run. Exact temporary candidate values/key were restored; production preferences/Run/executable unchanged. Light device-outline contrast observation is categorized for M09, renderer untouched. See status and `m08/preferences/`.

## 1. Starting point

| Item | Evidence and interpretation |
| --- | --- |
| Production | WPF remains supported. Earlier reliability repairs are preserved. No Core extraction or WinUI cutover. |
| Completed packages | M00 baseline protection and M01 contract adoption passed. M02 incomplete; M03–M12 not started. |
| Regression gate | Last full gate: 1,016 succeeded, zero failed/skipped; `artifacts/winui-rebuild/m02/checkpoint-production-verify.log`. This verifies the existing app, not new-shell readiness. |
| Prototype | Seven API/negative cases pass. Actual pinned drag, ordinary mouse opening/dismissal and Escape pass in the recorded extended test. |
| Focus | Current extended test fails `BubbleNeverBecameForeground` during external accessibility invocation. The initiating component and a valid fix are unknown. A failing test is evidence of a requirement mismatch, not proof of a WinUI framework defect. |
| Rendering choice | Runbook fallback B: stationary opaque outer window, immediate outer show/hide, internal navigation motion. Transparent-envelope input variants failed; do not reopen them without new evidence. |
| Motion | A 30-navigation desktop-pixel trace exists. One improved trace overlapped input-test startup. Useful-content readiness, clean 100-transition results and full qualification are unproven. |
| Memory | Last visible workload before the latest Grid host: 193.9 MiB sampled peak; an earlier run reached 201.5 MiB. Current build and integrated app unmeasured. Complete-app ceiling remains 200 MiB, including backend. |
| Startup / environment | Ten fresh processes passed tray-ready checks on this OS-warm developer machine. Fifteen synthetic geometry cases pass. Neither establishes clean-machine or physical mixed-DPI acceptance. |

Raw checkpoint: `artifacts/winui-rebuild/m02/wrap-up-checkpoint.json`. Detailed evidence: [prototype results](WINUI-PROTOTYPE-RESULTS.md). Prior successful, failed and confounded results remain in the record.

## 2. Rules for every work item

Before edits, write a short work-item entry in [status](WINUI-REBUILD-STATUS.md), using the template below. One active question at a time. Research is sufficient when it supports a discriminating experiment or an implementation decision; it need not explain all of Windows.

- **Question:** a decision that affects the next deliverable. Identify the product consequence and what would falsify the preferred explanation.
- **Evidence:** inspect the current implementation and the smallest relevant raw result. Label observations, documented contracts, inference and unknowns separately. Reuse valid evidence for unchanged behavior.
- **Research:** use existing notes first, then official documentation/source for the exact affected APIs. Record actual package/runtime/OS versions; a current source branch is not proof of shipped binary behavior. Every new lookup must answer a named uncertainty. No general toolkit survey during a focused bug investigation.
- **Scope:** enumerate permitted files and one deliverable. Keep backend, windowing, styling and measurement changes in separate items. Record discovered unrelated issues for later.
- **Experiments:** at most two distinct experiments, chosen before coding and conditional on the previous result. Change one variable at a time. Each has a control, expected outcomes, timeout, cleanup and interpretation. A repeated run of the same experiment does not become a new hypothesis.
- **Stopping rule:** one investigation has a maximum **60 minutes elapsed**, with start/deadline recorded. This is a work limit, not a promise of a fix. Stop earlier when the question is answered, two experiments fail to discriminate, required prerequisites are absent, or the next step exceeds scope. No automatic extension or speculative third fix. Finish cleanup and record the result even if the limit is reached.
- **Output:** deliver a verified fix with relevant checks, or an explicit unresolved/failed result, the evidence and one recommended next decision. Do not repeat another hour merely to avoid an inconclusive result.

Existing implementation authorization remains valid within scope. Routine steps do not require repeated approval. A different framework, product requirement or budget requires a concrete decision under the existing contracts. If an investigation reaches its bound, close that item with a result; independent documentation may continue, dependent implementation waits for a justified next scope.

## 3. First item: isolate the bubble activation

**Question:** what stage first makes the bubble foreground, and does evidence support a narrow correction within the adopted WinUI/native architecture?

**Scope:** the retained prototype's bubble activation path and its test client only. No backend integration, layout polish, new window host, custom UIA provider, rendering alternative, dependency, system installation, UAC request or new general diagnostic framework.

Permitted edit candidates, selected after reading evidence: `prototypes/WinUiShellProbe/test-bubble-uia.ps1`, `test-native-input.ps1`, `NativeLifetimeProbe.cs`, and its existing trace helper `ProbeDiagnostics.cs`. Add at most one small test-only client comparison under `prototypes/WinUiShellProbe/diagnostics/` if the second experiment is justified. Other source paths require a recorded scope revision before editing.

Research starting points are the four rechecked Microsoft sources in the [native lifetime note](../research/2026-10-15-winui-native-lifetime-probe.md#research-baseline-reset-2026-10-15). They distinguish click nonactivation, explicit programmatic activation, asynchronous Invoke and client threading. They do not identify this failure's caller.

| Explanation to test | Observation that would distinguish it |
| --- | --- |
| Client discovery, helper lifecycle or measurement ordering contributes activation | Activation occurs before Invoke dispatch, during element lookup, or on an equivalent no-Invoke control run. |
| The Invoke/provider/application path contributes activation | Matched lookup-only run preserves foreground; Invoke run changes it before explicit flyout opening. The stage alone does not identify the native caller. |

**Experiment 1 — stage the existing reproduction.** Reuse the same owned underlay and native input guard. Record monotonic timestamps and foreground HWND at lookup start/end, pattern acquisition, immediately before Invoke, app action entry before `ShowFlyout`, and flyout activation. Pair a lookup-only control with the Invoke case; keep the bubble otherwise identical. Record PID/HWND, source/build identity, styles and actual client apartment. Allow up to three matched trials only to establish repeatability; inconsistent/covered targets are inconclusive. Preserve the existing foreground assertion.

**Experiment 2 — only if experiment 1 leaves a useful client distinction.** Compare the existing managed client with the official native COM UIA client API against the same owned bubble. Use the same named element, action and windowless MTA conditions. Build only if existing tools/interop make this small; otherwise stop and record the missing prerequisite. No hand-built UIA framework, alternate host or provider cascade. If both behave alike, the client-comparison hypothesis is unsupported; stop rather than guessing the next workaround.

Historical custom peer, native provider, alternate AppWindow/XAML-source host, threading/style variations and unsuccessful stack walking already failed to remove the assertion. Their evidence is retained. Repeating them requires a specific new fact predicting a different outcome.

**Done:** record the discriminating result and confidence. A fix is eligible only if the cause is sufficiently supported and correction stays inside the named scope; verify unchanged focus assertion, mouse behavior, pin/Escape and cleanup. If a correction would require another experiment, close this investigation with the evidence and define a separate bounded item. Root cause may remain unknown. A clean full input pass alone does not pass all M02 criteria.

## 4. Ordered remaining M02 work

These are separate items, not permission to execute them during this baseline-writing task.

| Order | Deliverable | Scope / stop condition |
| --- | --- | --- |
| R1 | Focus finding or verified narrow fix | Section 3; 60-minute bound and at most two experiments. If unresolved, report the feasibility decision needed; do not start M03. |
| R2 | One clean 100-transition recording and analysis | Reuse existing capture/analysis drivers, serially, without a concurrent build/input test. Record useful content separately from busy pixels, observer missed frames, native geometry and method limits. If the instrument cannot establish the metric, mark it unverified; do not build another capture stack. |
| R3 | Current visible resource result | Reuse the existing 1,000-publication/100-navigation workload. Record build, visibility, motion, working/private bytes, CPU and handles. One run; repeat only for a named confound or corrective change. No forced GC or budget adjustment. A prototype pass does not establish integrated headroom. |
| R4 | M02 evidence reconciliation | Map every M02 criterion to source/build-specific evidence. Reuse startup, input and geometry results only where the relevant behavior is unchanged. Name untested hardware/60 Hz/clean-machine rows and their owning phase; do not silently defer a mandatory M02 criterion. Run required gate once at this checkpoint. Start M03 only if all M02-required evidence is satisfied. |

No remaining check becomes optional because these four items are narrow. Missing evidence keeps its criterion open. If feasibility remains unsatisfied, preserve WPF and present a specific recommendation; do not silently drop the bubble, weaken accessibility or switch frameworks.

## 5. Verification, communication and handoff

Use targeted checks while investigating; run the full required gate once after a coherent change, then repeat only for new changes, failures or unresolved concerns. Desktop input/capture tests run serially. A changed UI requires the prescribed native capture and OS/build record. Smoke checks do not establish visible UI startup.

Give updates about findings and remaining uncertainty at least every 60 seconds during active work. Report the current item, limit and result; avoid repeatedly recounting activity. At a checkpoint, state delivered behavior, evidence, failures and next decision. Do not give an unsupported completion percentage or release ETA.

Maintain one authoritative next-item entry in status and update the relevant existing research note. Create another document only for a required decision or a genuinely new integration. Preserve source changes and prior evidence; temporary failed experiments are removed with their outcomes recorded.

```text
Item / one question:
Product consequence:
Started / deadline (elapsed limit):
Current evidence and source/build identity:
Hypotheses / falsifying observations:
Official sources / exact versions / what remains undocumented:
Permitted files and explicit exclusions:
Experiment 1 / control / outcomes:
Conditional experiment 2 / control / outcomes:
Required verification and cleanup:
Stop result / evidence / confidence:
Next decision or deliverable:
```

Baseline establishment itself changed documents only. At establishment, the next item was R1. For subsequent sessions, use the current next-item entry in status; do not repeat closed experiments or start a new broad audit. The R1–R4 definitions here preserve the original scoped plan and stopping rules.



