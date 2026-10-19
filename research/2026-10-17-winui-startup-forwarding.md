# Isolated candidate startup and forwarding

- **Date:** 2026-10-17
- **Author:** Codex
- **Phase:** M08
- **Status:** implementation and actual launch probe pass; full gate failed at existing resource criterion

## Problem

Complete candidate normal/startup/secondary-launch behavior with existing local IPC semantics and executable Host startup ownership, preserving production identities.

## Sources consulted

| Source | URL / reference | Version checked |
| --- | --- | --- |
| PipeOptions | https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0 | .NET 10, consulted 2026-10-17 |
| WaitForConnectionAsync | https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream.waitforconnectionasync?view=net-10.0 | .NET 10, consulted 2026-10-17 |
| Mutex | https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex?view=net-10.0 | .NET 10, consulted 2026-10-17 |
| Run and RunOnce | https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys | Updated 2026-02-21, consulted 2026-10-17 |
| Existing owners | src/AdbView/App/SingleInstance.cs; Host/StartupRegistration.cs; Host/AppHost.cs | Exact existing source inspected |

## Version investigated

SDK 10.0.401; App SDK 2.5.1 / WinUI 2.3.9; CsWin32 0.3.333; Windows 26100. No package changes.

## Findings

CurrentUserOnly restricts pipe clients and servers to the same account and, on Windows, elevation level. Asynchronous pipe creation is required for cancellable connection waits. A named local mutex selects the owner; ownership/release must remain on the same bootstrap thread. Existing normal launch opens, startup does not; manual secondary launch sends toggle. Run entries are named command lines; unchanged StartupRegistration formats quoted executable plus --startup. Inject a store that maps only that named entry to the candidate Run value; never instantiate its production default adapter. Candidate accepts --startup as an explicit live/silent entrypoint.

## Alternatives considered

| Option | Pros | Cons |
| --- | --- | --- |
| Candidate-only mutex/pipe and fixed toggle/ack | Same ownership model, independently testable, no dependency | Must bound client I/O and observe dispatcher rejection/disposal |
| Source-link old SingleInstance unchanged | Fewer lines | Fixed production names collide; current-user and acknowledgement boundaries absent; rejected |
| Broadcast arbitrary window commands or adopt a new activation framework | Avoids pipe | Adds integration scope or dependency; unnecessary |

## Chosen approach / why

Preserve bootstrap-owned mutex, isolate names with current user/session, and use an asynchronous current-user-only local pipe with a fixed one-byte command and checked acknowledgement. Startup duplicates exit silently; only manual duplicates send toggle. No visual snapshot queue, polling, remote access or external command payload. One-shot bounded IPC deadlines are operation timeouts, not periodic work. Invalid/disconnected clients are logged without executing an action; owner failures are observed, shutdown cancels pending I/O. Reuse unchanged StartupRegistration via source link in executable Host, injecting a name-restricting candidate adapter; only command 2 changes registration. Readback supplies menu checks.

## Compatibility concerns

Different elevation levels cannot connect through CurrentUserOnly; report failure, do not bypass it or start a second backend. Actual Windows foreground rules can refuse activation; existing native owner logs that refusal. No new foreground workaround. Run command length and actual login/silent behavior remain deployment acceptance; this item tests the explicit startup entrypoint, not reboot/login. Production app stays supported.

## Testing implications

Actual local pipes and deterministic dispatcher callbacks test protocol/rejection/disposal, while fake Run stores verify isolated names, exact command, failed writes and per-command persistence. Actual Release multi-process launch validates ownership, startup visibility, manual toggles and one backend. Keep fixtures independent and preserve prior assertions. Full gate/captures required; physical Explorer/DPI, remaining environment and device/soak stay open.

## Open items

M08 remaining adapters/events/physical checks, M09 deferred contrast/resources, M11 real login/upgrade/uninstall, M12 device/soak.

## Observed result, 2026-10-17 19:18 Asia/Karachi

12 new focused cases and all 1,102 C# cases pass, zero-warning Release build/format pass. Actual Release startup primary initially hidden, duplicate startup silent, two manual secondaries toggle one backend open/hidden, separate normal primary opens; observed foreground on both opens. Owned primary/secondary cleanup exits 0, no warning/error/fatal in log, production executable/Run/preferences and candidate Run entry unchanged. Run enable/disable is fake-store covered only; no login/reboot proof. Final live image inspected at Windows 26100 / 125%.

The full gate fails unchanged M07 500-cycle process-handle bound twice: 2501→2534 (+33), then 2497→2512 (+15), allowance +10; private bytes decrease in both. No resource policy, threshold, warmup, forced collection or renderer change. New native startup-menu assertions, publish smoke and prescribed native capture remain unexercised. Stop implementation and record this blocking criterion for separate scoped qualification; prior variability does not establish cause or exclude regression. Evidence under `artifacts/winui-rebuild/m08/startup/`; status names exact results. No M09 advance, production cutover, dependency or polling.
