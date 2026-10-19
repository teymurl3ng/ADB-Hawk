# Candidate live composition redo

- **Date:** 2026-10-17
- **Author:** Codex
- **Phase:** M08 live composition slice
- **Status:** bounded composition slice verified; M08 remains incomplete

## Problem

Compose existing Core backend through the isolated WinUI candidate without repeating the reviewed fixture-clock, upstream snapshot queue or production preference mistakes. Do not change backend behavior or the supported WPF executable.

## Sources consulted

| Source | URL / reference | Version checked |
| --- | --- | --- |
| Microsoft DispatcherQueue.TryEnqueue | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueue.tryenqueue?view=windows-app-sdk-2.0 | Consulted 2026-10-17; stable pinned API compiled locally |
| Existing Core extraction research | research/2026-10-16-core-extraction.md | BackendRuntime, SDK 10.0.401 |
| Existing native integration research | research/2026-10-16-winui-native-integration.md | App SDK 2.5.1 / WinUI 2.3.9, CsWin32 0.3.333, Windows 26100 |
| Local integration authority | ENGINEERING §23; Host/AppHost.cs, BackendRuntime.cs, SnapshotMailbox.cs | Exact restored cb097ff implementation inspected |

## Findings

TryEnqueue returns false when delivery cannot be queued; delegates run serially on its dispatcher. The existing SnapshotMailbox is already thread-safe and owns the only visual queue. Feed it synchronously from the backend publication callback; never enqueue per-snapshot callbacks before its slot. Notification intents are a separate lossless dispatcher path, with rejection logged/reported and disposed queued callbacks inert. Backend reconciliation remains separate from changed visual snapshots. Use SystemClock for live state age; preserve fixture T0 only for deterministic fixture presentation.

## Alternatives considered

| Option | Pros | Cons |
| --- | --- | --- |
| Testable candidate Host session over unchanged BackendRuntime | Reuses policy, tests actual composition and shutdown | Explicit injection/ownership required |
| Reuse archived live shell verbatim | Small diff | Confirmed clock/queue/failure defects; rejected |
| Move native preferences/startup into Core or share production registry | Easy access | Violates ownership/isolation; excluded |

## Chosen approach

An explicit live option creates a distinct candidate mutex/tray identity and a Host session. The existing mailbox receives snapshots directly. UI notification delivery has its own checked dispatcher and disposal guard. Services start only after window, mailbox, sink and native owners are ready. Inject the runtime/configuration and sink for deterministic tests; production defaults compose existing services. Retain action guards and log explicitly uncomposed diagnosis/repair rather than claim full M08. Preferences stay memory-only in this slice, with no registry/startup integration. Live mode verification options cannot combine with fixture result paths.

## Compatibility concerns

The previous live launch observed bubble-visible native teardown warnings; source ownership/root cause remain unproven. Do not redesign native lifetime during this item. A repeated actual cleanup failure leaves acceptance open. Full-screen/power/session/theme events, diagnosis adapters, multicast interface discovery, forwarding, production startup and physical checks remain separate M08 work. No new ADB operations, dependency, polling, OS configuration or installation.

## Testing implications

Use actual BackendRuntime/StateEngine with injected service setup to prove direct mailbox publication, single queued drain, notification master state and rejection/disposal behavior. Keep all prior assertions and run the full gate. Actual Release launch and native capture are independent of publish smoke; fixtures alone cannot prove live composition or device acceptance.

## Result, 2026-10-17

Nine composition tests and the full gate pass: 1,076 C# cases, 29 actual Release fixtures / 833 assertions, unchanged native/input/resource assertions and production publish smoke. Actual isolated Release startup left flyout/bubble hidden; synthetic owned receiver selection opened a live connected USB list, with read-only metadata requests. Normal close exited 0 and logged backend disposal, without native warnings/errors. Final capture and prescribed native fixture capture inspected on Windows 10.0.26100, 125% scale. Production executable hash and exact `ADB View` Run value unchanged; no candidate remains. Driver compiler/default-path failures happened before product launch. A first successful run's clipped DPI-virtualized image/wrong Run-value comparison were corrected and repeated; retained results are explicitly partial. Final driver reuses the existing helper's public declarations and per-monitor capture coordinates. This is lifetime/rendering evidence only; physical tray input, real pairing/repair, environment events, startup no-flash and integrated performance remain unproven. Evidence and preservation: `artifacts/winui-rebuild/m08/live-redo/`, status and device matrix. No backend, WPF, prototype, dependency or policy change.
