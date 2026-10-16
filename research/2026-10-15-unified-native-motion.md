# Unified native motion: scheduling, layout, and surface ownership

- **Date:** 2026-10-15
- **Author:** Codex
- **Phase:** Native presentation hardening alongside the open Phase 11 acceptance work
- **Status:** current as WPF audit research; its implementation proposal is superseded by [the WinUI research](2026-10-15-winui-shell-rebuild.md) and decision 0029. No replacement renderer has been implemented.

## Problem

Determine why the existing flyout reveal and structural interactions can stutter or snap, and identify a viable universal presentation system. Separate code/runtime evidence from older audit conclusions and from design recommendations. In particular, changing one timer or duration cannot resolve independent native geometry, WPF layout, and storyboard lifecycles.

## Sources consulted

Official sources rechecked 2026-10-15. API pages use the Windows Desktop 10.0 view where applicable. A page retrieval date is not treated as its release date.

| Source | URL | Version / date |
| --- | --- | --- |
| DispatcherTimer | https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatchertimer?view=windowsdesktop-10.0 | Windows Desktop 10.0 |
| WPF threading model | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model | Current WPF documentation |
| Layout and design performance | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-layout-and-design | Current WPF documentation |
| CompositionTarget rendering | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget | Current WPF documentation |
| Transform overview | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/transforms-overview | Current WPF documentation |
| Hardware and software rendering | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-taking-advantage-of-hardware | Current WPF documentation |
| Window.AllowsTransparency | https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency?view=windowsdesktop-10.0 | Windows Desktop 10.0 |
| Windows 11 rounded corners | https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners | Windows 11 |
| Property-trigger animations | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-trigger-an-animation-when-a-property-value-changes | Page dated 2025-05-07 |
| StopStoryboard | https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.animation.stopstoryboard?view=windowsdesktop-10.0 | Windows Desktop 10.0 |

## Versions investigated

- Repository HEAD `4dcc61ed4f5f3f7ab028cbadd8db120d2e4b6be7` plus the existing, uncommitted connection-reliability work.
- SDK 10.0.401, target `net10.0-windows`, installed Microsoft.WindowsDesktop.App 10.0.12.
- Windows 11 build 10.0.26100.0.
- Running repository Release build identified as 2026-10-15 01:27, version 0.1.0.0.
- Audit control harness uses the same Release app DLL and Desktop runtime. No new NuGet package or application project.

## Findings

1. **Documented:** DispatcherTimer work shares the dispatcher queue and is not guaranteed to execute exactly at its interval. A 10 ms interval therefore does not establish a 60/120/144 Hz presentation guarantee. The current log's uneven callback gaps are consistent with this contract; they do not identify the blocking task.
2. **Documented:** WPF layout involves measure and arrange and can be expensive. RenderTransform can avoid the layout work required by LayoutTransform. Layout modification from a rendering callback can require further layout. **Repository fact:** the app explicitly remeasures the live root inside LayoutUpdated, while the reveal uses separate native writes.
3. **Documented:** CompositionTarget.Rendering is associated with WPF rendering, not a documented cross-subsystem DWM presentation fence. Switching the native-position timer back to this callback alone is not evidence of a solved problem. **Historical evidence:** earlier repository notes report variable callback cadence for position-only HWND movement. **Proposal:** WPF clocks should own visual animation; any custom rendering callback must have an active lifetime and perform bounded work.
4. **Documented:** WPF window-background transparency does not expose the desktop unless AllowsTransparency is enabled; that property requires WindowStyle.None. A fixed opaque native rectangle with internally animated children cannot reproduce a smoothly changing external silhouette.
5. **Documented:** Per-pixel alpha windows and windows with regions cannot receive DWM's rounded-corner treatment. A transparent-host proposal must therefore account explicitly for its own visible outline/corners/shadow. It cannot promise the current DWM styling remains unchanged internally.
6. **Documented:** WPF supports hardware and software pipelines; performance depends on rendering work and affected pixels. The consulted documentation does not establish the proposed app's actual layered-window cost on this machine. Both claims that transparency is automatically cheap and that all modern transparent WPF windows are necessarily software-only would exceed this evidence.
7. **Observed in isolated actual controls:** Motion.State changes to zero while already-instantiated card storyboards retain 167/83 ms durations. Static resource values captured in those local styles are not replaced by swapping another control dictionary.
8. **Observed in isolated actual controls:** The pairing spinner remains animated and advances after its VM enable flag is false. The production trigger has only an enter action. The official trigger example explicitly supplies exit behavior; StopStoryboard is available for stopping a named clock. The replacement should centrally own and remove clocks rather than add one-off exit fixes at every site.
9. **Evidence correction:** The earlier note's AnimateWindow shadow warning is API-specific. The current implementation calls SetWindowPos. The warning is not sufficient evidence that shadows are the root cause of its measured stalls.

## Alternatives considered

| Option | Advantage | Limitation |
| --- | --- | --- |
| Tune timer interval or lengthen the native slide | Small change | Does not remove dispatcher contention, full-height travel, instant resizing, or split lifetime rules. |
| Single coordinator but keep opaque native frame and instant geometry | Lower renderer risk | Improves consistency, but does not fulfill smooth outer expansion/shrink. Honest degraded fallback only. |
| Per-frame HWND size changes | Can follow an interpolated rectangle | Reintroduces native/layout coordination and repaint concerns; expressly prohibited by the current contract; not recommended. |
| Bounded transparent WPF host with animated visual surface | Keeps existing WPF controls and one visible rendering space | Requires a contract change, own corners/shadow, and measured input/text/performance proof. Preferred prototype, not yet validated. |
| Native composition host or WinUI migration | Different rendering capabilities | More interop/dependencies, accessibility and control-hosting consequences; not justified before the smaller prototype is measured. |
| Animation package | Convenience primitives | Cannot assign window ownership, coalesce snapshots, or fix an inappropriate surface model by itself. No dependency proposed. |

## Chosen approach

Prepare the unified coordinator/runtime/layout/surface design in `docs/MOTION-SYSTEM-PLAN.md` and contract proposal 0028. Prototype the bounded transparent WPF surface first. Migrate only once silhouette, hit testing, typography, focus, DPI, idle behavior, and real frame performance are demonstrated. Keep a single policy and lifecycle even for an explicitly degraded instant fallback.

## Why

It addresses the verified mismatch between native geometry, layout, and local clocks. It reuses the existing product and WPF controls, avoids arbitrary patches, and makes the uncertain rendering choice a testable early decision rather than an assumption hidden in a broad rewrite.

## Rejected approaches

Do not promise smoothness from a timer frequency, use process-wide timer-resolution changes, animate every WPF Height/Margin, scale live text/QR content, delay device truth for exit animations, keep oversized opaque chrome, introduce a third shadow window, or migrate frameworks without evidence. Do not describe the passing unit gate as proof of presented-frame smoothness.

## Compatibility concerns

Transparent-area hit testing across processes, native drag/nonactivation, screen-reader bounds, text antialiasing on transparent targets, mixed DPI and refresh rates, RDP/software rendering, high contrast, suspend/reopen, taskbar/work-area placement, and current renderer's native corner/shadow behavior all need explicit checks. These concerns are properties of the proposed implementation choice, not findings that those environments currently fail.

## Testing implications

Use actual WPF controls and clocks for motion-gate/cancellation tests, deterministic math tests for interruption/geometry, headed fixture sequences for interactions, and presented-frame evidence for pacing. Attach frame and UI-work timings to operation/revision identifiers, without device secrets. Retain existing no-stale-state and backend tests. Gate result for this planning task: 1,010 passed, no failures/skips, full verify.ps1 green. No product motion code changed.

## Open items

- V-8: structural transition and mixed-DPI proof for the new surface.
- Attribution of current 141/328 ms callback gaps through profiling.
- Current-machine display refresh and new renderer performance; not inferred from nominal timer intervals.
- Surface prototype and exact visual shadow fidelity; no live video claimed.
- Contract adoption before replacement implementation, as specified in proposed decision 0028.
