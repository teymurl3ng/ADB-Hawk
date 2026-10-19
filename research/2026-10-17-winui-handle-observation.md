# Bounded handle-growth observation

- **Date:** 2026-10-17
- **Author:** Codex
- **Phase:** M08 blocking verification, unchanged M07 resource criterion
- **Status:** current

## Problem

The 500-navigation fixture reports process handle growth of +33/+15 against +10, despite zero owned animation objects/subscriptions and declining private bytes. Identify handle types before changing ownership code. No cause is yet established.

## Sources consulted

Official Microsoft Learn documentation, consulted 2026-10-17:

| Source | URL | Version / last updated |
| --- | --- | --- |
| PssCaptureSnapshot | https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/nf-processsnapshot-psscapturesnapshot | Windows 8.1+ |
| PSS_CAPTURE_FLAGS | https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/ne-processsnapshot-pss_capture_flags | 2023-03-13 |
| PSS_HANDLE_ENTRY | https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/ns-processsnapshot-pss_handle_entry | 2023-03-13 |
| PssWalkSnapshot | https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/nf-processsnapshot-psswalksnapshot | 2025-10-31 |
| PssWalkMarkerCreate | https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/nf-processsnapshot-psswalkmarkercreate | 2025-10-31 |
| PssFreeSnapshot | https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/nf-processsnapshot-pssfreesnapshot | 2025-10-31 |

## Version investigated

Windows 10.0.26100 x64; SDK 10.0.401; .NET 10; Microsoft.WindowsAppSDK 2.5.1; unchanged Release candidate. Exact source/binary hashes accompany observations. No package added.

## Findings

PSS can capture handle values, type/name and basic information without a VA clone. PssWalkSnapshot returns ERROR_NO_MORE_ITEMS at completion. Type/name pointers last only as long as the walk marker, so the observer copies strings before freeing it. The snapshot captured locally is freed with GetCurrentProcess; the walk marker and target Process wrapper also require cleanup. These APIs identify object types, not the managed/native allocation caller. Observer sampling can perturb scheduling; a diagnostic pass alone will not establish a fix or erase prior failures.

## Alternatives considered

| Option | Pros | Cons |
| --- | --- | --- |
| External PSS observer | Documented, built into Windows; no product dependency | Scheduling perturbation; no allocation stack |
| Forced GC / longer warmup | Could change counts | Would hide rather than establish ownership; rejected |
| Download another diagnostic tool / global tracing | Potential stack detail | Broader machine changes and setup; outside first experiment |

## Chosen approach / why

Artifact-only PowerShell/C# observer samples only its explicitly launched fixture process, with deterministic snapshot/marker/process cleanup. Preserve unchanged workload and assertions. First validate interop against the observer's own process; then use type deltas to choose at most one discriminating next step.

## Rejected approaches / compatibility concerns

No undocumented kernel enumeration, product polling, new dependency, production process capture or assertion relaxation. This helper is x64-only diagnostic code for the recorded Windows build, not shipping integration.

## Testing implications / open items

Compare PSS entry totals with process totals; fail on every native API error, bound fixture lifetime, retain original failed results. Actual owner and correction remain unknown until evidence is collected. Any source correction requires relevant checks and full scripts/verify.ps1.

## Bounded result (19:35 Asia/Karachi)

Observer self-check returned exactly 514 entries / 514 process handles. Animated fixture PID 13324 passes 86 assertions, +6 handles, declining private bytes. Nearest external samples differ by +5 Event / +2 File, with stable Composition/graphics/Thread counts; the external baseline differs from the in-process baseline by one handle. Three named LiftedCompObj Events are present at both endpoints; names/identities change, so this does not establish retention. Sampling changes scheduling and did not reproduce the failure. First-run provenance includes source/apphost hashes, not the managed assembly hash; this limitation is retained.

The matched disabled-motion control PID 24028 fails its unchanged foreground prerequisite before resource work. No comparison or allocation-caller evidence obtained. Owner expressed fatigue with repeated desktop requests; close without another retry. Restore temporary test edits exactly, rebuild ordinary Release candidate (zero warnings/errors), retain all failed/passing records. No resource-owner fix justified; last full gate remains red on the previously recorded handle assertion. A future failing-run allocation-owner trace is more useful than guessing a cleanup change or repeatedly seeking a passing sample. No further experiment is authorized inside this closed item.
