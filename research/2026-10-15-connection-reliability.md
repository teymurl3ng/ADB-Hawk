# Connection, discovery, and recovery reliability

- **Date:** 2026-10-15
- **Author:** Codex
- **Phase:** 11 release hardening; repairs to existing Phase 12 mechanisms
- **Status:** current

## Problem

The code audit reproduced frozen device truth, missed post-pair connection, stale service data, and DNS record-correlation failures. Reliability fixes must preserve the single-writer state model, shared ADB citizenship, event-driven tracking, and frozen visual contract.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Multicast DNS | https://www.rfc-editor.org/rfc/rfc6762.html | RFC 6762, February 2013; consulted 2026-10-15 |
| DNS-Based Service Discovery | https://www.rfc-editor.org/rfc/rfc6763.html | RFC 6763, February 2013; consulted 2026-10-15 |
| Android SDK Platform Tools release notes | https://developer.android.com/tools/releases/platform-tools | Current page consulted 2026-10-15; implementation schema remains pinned to the repository's investigated 37.0.1 |
| Process.Start | https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.start?view=net-10.0 | .NET 10; consulted 2026-10-15 |
| GetWindowRect | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect | Updated 2025-04-11; consulted 2026-10-15 |
| SetThreadDpiAwarenessContext | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddpiawarenesscontext | Updated 2024-02-22; consulted 2026-10-15 |
| ImmutableArray.Builder.MoveToImmutable | https://learn.microsoft.com/en-us/dotnet/api/system.collections.immutable.immutablearray-1.builder.movetoimmutable?view=net-10.0 | .NET 10; consulted during audit |

## Version investigated

Repository SDK pin: .NET SDK 10.0.401, target net10.0-windows. Host Windows build 26100. Existing ADB schemas and CLI compatibility were investigated against platform-tools 37.0.1 in the repository's earlier dated research; this repair does not change the schema pin, install platform-tools, or claim a new live Android OS validation. Source baseline: 4dcc61ed4f5f3f7ab028cbadd8db120d2e4b6be7.

## Findings

- MoveToImmutable requires Count == Capacity. Appending distinct services to an existing builder does not ensure that condition; the audit reproduced its failure with two ADB rows and one additional fallback row.
- RFC 6763 section 12 requires clients to resolve necessary records absent from additional records. PTR-only queries and arrival-order-dependent correlation are insufficient.
- RFC 6762 section 6.7 recommends legacy-unicast response TTLs of at most ten seconds. A sixty-second refresh minimum for populated short-lived data can create a discovery gap.
- RFC 6762 section 10 defines record-level cache lifetime and withdrawal behavior. A PTR refresh must not extend the independent validity of an SRV target or host address.
- Process.Start can throw on executable launch failure. Such failures belong in the subsystem's existing recovery/backoff rather than terminating an unobserved task.
- The installed app identified an October 10 build. Rebuilding source alone does not replace that process. Fake-server results describe the reviewed assembly, not an unmeasured live phone session.
- Native capture observations: the repository capture script's explicit System.Drawing reference compiled under Windows PowerShell 5.1, whereas the current PowerShell runtime required different forwarded assembly references. Running it under Windows PowerShell avoided changing the script. Its unaware GetWindowRect bounds cropped the physical PrintWindow bitmap at the host's scaling. Microsoft documents DPI virtualization of those bounds; the capture wrapper temporarily uses per-monitor awareness on its own thread, then restores the previous context. Product window DPI policy is unchanged.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Restart all workers/server periodically | Simple appearance of recovery | Hides defects, introduces polling, disrupts shared tools; rejected |
| Implement a new generic supervisor/message bus | General fault containment | Adds architecture and dependencies beyond the repair; rejected |
| Keep existing owners and make lifetime/error signals explicit | Small, testable, preserves ownership | Requires lifecycle and cross-subsystem regression coverage; chosen |
| Use one service-wide DNS expiry | Small record model | Incorrect independent address/port validity; rejected |
| Record-level DNS resolution within the existing browser loop | Correct ordering and expiry, same scheduler | More explicit cache bookkeeping; chosen |

## Chosen approach

Repair existing subsystems in six bounded waves recorded in docs/RELIABILITY-REPAIR-PLAN.md. Keep the empty-set discovery query floor at sixty seconds; for known records, use expiry-driven refresh without letting that floor exceed their validity. Resolve missing records through the same query mechanism and loop, without adding an independent polling timer. Preserve native surfaces and existing snapshot semantics.

## Why

These changes repair confirmed failure boundaries rather than restarting shared infrastructure or redesigning the application. Independent record validity and explicit stream/operation lifetimes allow meaningful fake-server and deterministic clock tests.

## Rejected approaches

No automatic kill-server, no machine/network repair without the existing explicit user action, no general remembered-device reconnect feature, no constant device-list polling, no optimistic connected cards, and no new visual component family.

## Compatibility concerns

Older ADB servers retain the documented CLI discovery/pairing fallbacks. Unknown capabilities remain degraded states with logged evidence. Socket idleness remains normal; no timeout is added to a quiet device stream. IPv6 link-local addresses require an interface scope; do not guess one from an unrelated network.

## Testing implications

Test mixed-source merge capacities; stream loss before socket failure; sequence ordering across wall-clock rollback; duplicate transport preference; discovery changes without rendered snapshot changes; multi-endpoint retries; real pairing-client cancellation classification; split/order-varied DNS records with different TTLs; empty CLI updates and continuous stderr drainage; executable launch failure; power-triggered recovery; repair terminal states and undo persistence; native health/warning rendering. Capture only existing approved states.

## Open items

Real Wi-Fi interruption, sleep/wake with a phone, network-family coverage, and long-duration soak remain live-matrix work. This source repair does not substitute fake-server tests for those observations or claim Phase 11 release acceptance.
