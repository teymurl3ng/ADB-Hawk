# UI-independent Core extraction

- **Date:** 2026-10-16
- **Author:** Codex
- **Phase:** M03
- **Status:** current

## Problem

Extract existing backend and pure policies without changing ADB behavior, snapshot semantics, notification delivery or the supported WPF shell. M02 current-machine feasibility passed; M04 shell work is excluded.

## Sources consulted

| Source | URL | Version / checked |
| --- | --- | --- |
| SDK project properties and references | https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props | .NET SDK; checked 2026-10-16 |
| Friend assemblies | https://learn.microsoft.com/en-us/dotnet/standard/assembly/friend | .NET; checked 2026-10-16 |

## Version investigated

Pinned .NET SDK 10.0.401, target net10.0-windows; existing central package pins remain unchanged. Current WPF app and xUnit/MTP test project remain supported. Core uses the ordinary SDK without UseWPF/UseWindowsForms, native generator or desktop framework references.

## Findings

ProjectReference establishes the compile/runtime dependency; friend assemblies preserve existing internal types without making the backend public. AppSnapshot, ADB, State, Notifications, Diagnostics and the clock have no desktop dependency. Lifecycle classes depend on delegates and BCL APIs. Native diagnostics, repair system adapters, tray resources and WPF view models stay in the executable. Pure bubble, placement, sizing, theme and age policies can move while retaining namespaces. AppHost currently keeps SnapshotPublished separate from StateReconciled; preserving both is necessary even for semantically unchanged snapshots.

## Alternatives considered

| Option | Pros | Cons |
| --- | --- | --- |
| Move sources with existing namespaces/internal visibility | Small diff; existing assertions retain their targets | Requires friend assemblies and explicit source-path migration |
| Linked duplicate source or public API redesign | Superficially easier references | Risks two copies of truth or needless API changes; rejected |

## Chosen approach

Move files, retain exact content where possible, add an internal backend composition/lifetime owner and adapt AppHost at the existing startup/shutdown boundaries. Keep native adapters in the executable. Extend boundary scans across both production roots and add compiled-assembly independence checks. Retain both test references temporarily as M03 explicitly permits. Preserve fixture JSON before/after and byte hashes for moved backend source; run existing behavior/fake-server/notification tests and the full gate. Launch the actual Release WPF executable separately from smoke.

## Why / rejected approaches

One core assembly provides the replacement shell a backend without another host, DI framework, message bus, dependency, recovery policy, timer or product feature. Do not change native integrations, implement WinUI controls, optimize memory or fix unrelated historical issues during extraction.

## Compatibility concerns

Assembly-relative UI resources must remain with the executable. Core must not reference the old app, WPF, WinForms, WinUI or native APIs. Existing BCL sockets/process/file behavior remains unchanged. Do not confuse test-project references to the old app with Core dependencies.

Observed library build difference: CA1064 requires public exception types in a class library; the old executable did not trigger it. AdbException, its two derived exceptions and AdbErrorCode become public; constructors/properties/behavior stay unchanged. No suppression or analyzer relaxation added. Other 81 moved files retain exact bytes. Compiled-reference/PInvoke checks and 44-fixture JSON equivalence pass. PowerShell 7.6.6 preloads WinForms, so compare additional assemblies rather than attributing the host baseline to Core. Core references no desktop assembly.

## Testing implications / open items

Prove source preservation, fixture equivalence, reconciliation without visual publication, visibility inputs, backend shutdown and no desktop framework references. Existing hardware/deployment/soak gaps retain their later owners. Any unrelated finding is recorded for later rather than repaired in M03.

Result: M03 PASS. Final unchanged gate runs 1,030 successful cases, zero failed/skipped. All 44 fixture snapshots match; 81 moved files are byte-identical, with only exception visibility changed in the remaining file. The 77 original C# test source files preserve assertions, allowing only migrated source paths and wider scan roots; 824 baseline map entries remain. Actual non-smoke Release executable shows its WPF fixture window, loads Core and exits 0 without forced cleanup on Windows 10.0.26100. Installed app briefly exits through its verified message loop and is restored with the same binary hash. Fixture startup identity remains production-bound until M04; no isolation rewrite was added here. No visual/device/soak/deployment acceptance claimed. Evidence under `artifacts/winui-rebuild/m03/`, authoritative `completion.json`.

Release launch verification reuses the actual executable and empty fixture. No other ADB View process may be running. Enumerate only that launched PID's visible titled window and verify its thread ownership before cleanup. [PostThreadMessageW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postthreadmessagew) (2023-02-09) posts to an existing thread message queue; [WM_QUIT](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-quit) (2025-07-14) terminates the message loop rather than reaching a window procedure. Use only the verified test thread, never a foreign process/window message. Preserve exit/timeout results; a forced cleanup is disclosed and cannot prove graceful exit. No system or installed-app modification.
