# Stable WinUI list/details presentation

- **Date:** 2026-10-16
- **Author:** Codex
- **Phase:** M05, decision 0029
- **Status:** current

## Problem

Implement the authorized stable list/details contract without queuing stale snapshots, rebuilding rows, losing copy identity or resizing the native window when data changes. Keep the fixture-only candidate and installed WPF process isolated.

## Sources consulted

Official sources accessed 2026-10-16, checked against the pinned stable package by compilation.

| Source | URL | Version / scope |
|---|---|---|
| DispatcherQueue.TryEnqueue | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.dispatching.dispatcherqueue.tryenqueue?view=windows-app-sdk-2.0 | WinUI dispatcher enqueue and shutdown rejection |
| ScrollViewer.ChangeView | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.scrollviewer.changeview?view=windows-app-sdk-2.0 | Offset restoration with disableAnimation=true |
| UIElement.TransformToVisual | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.transformtovisual?view=windows-app-sdk-2.0 | Keyed row coordinates within scroll content |
| AppWindow.MoveAndResize | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.moveandresize?view=windows-app-sdk-2.0 | One pre-show physical-pixel bounds operation |
| GetDpiForWindow | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow | Existing M02/M04 per-window DPI query; no new awareness mode |
| Clipboard.SetContent | https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.clipboard.setcontent?view=winrt-26100 | Local text DataPackage; failures must be reported |
| ButtonAutomationPeer | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.peers.buttonautomationpeer | Default Invoke pattern drives the actual button Click path; compiled and exercised on pinned package |
| Existing placement/startup proof | research/2026-10-15-winui-shell-rebuild.md; research/2026-10-16-winui-candidate-harness.md | M02 fallback B and M04 exact toolchain |

## Version investigated

.NET SDK 10.0.401, WindowsAppSDK 2.5.1, CsWin32 0.3.333, net10.0-windows10.0.26100.0, minimum Windows 11 22000, x64 host build 26100. Existing package pins unchanged.

## Findings

TryEnqueue reports shutdown rejection; application code must handle that failure. ChangeView can suppress application-requested scrolling animation. TransformToVisual provides actual row positions for a bookmark rather than guessing constant heights. Clipboard.SetContent takes a local DataPackage; clipboard ownership contention is a failure, never a false Copied badge. These APIs do not establish race correctness, unchanged geometry or focus restoration: executable tests must do that. A one-slot visual mailbox never carries commands or domain events. Locking pending data and drain ownership together closes the publish/reset race.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Snapshot reference + keyed row controls | Current truth, preserves control identity | Explicit property diffs and removal required |
| Rebuild all controls per revision | Simple | Loses focus/drafts/scroll and makes stale controls harder to reject |
| Queue every snapshot behind navigation | Easy ordering | Delays truth; forbidden |

## Chosen approach

Core owns a small race-safe AppSnapshot mailbox, presentation bookmarks and physical-pixel placement calculation. The candidate owns native controls, current snapshot reference, keyed property diffs and immediate list/detail navigation. Exactly one Host controller writes AppWindow bounds before show; content/navigation never calls it. Copy commands resolve ID/key against the current snapshot and remain separate from mailbox delivery. Reuse existing 1,200 ms one-shot copy feedback semantics, with cancellation on disposal/navigation; no periodic timer or motion is added. Age text uses the existing pure formatter with snapshot time; visible age ticking remains M07.

## Why / rejected approaches

The contract is about immediate truth and stable geometry, not a new layout or animation framework. No WPF edits, backend policy changes, generic message bus, new packages, animated geometry or full pairing migration.

## Compatibility concerns

Synthetic work-area/DPI and doubled-text fixtures do not replace M09 physical mixed-DPI, system text scale, Narrator or contrast testing. Fixture copy tests use an injected recording writer and do not replace the user's clipboard. Actual Clipboard.SetContent is the normal candidate adapter; failures are logged with subsystem/reason. Native environment event subscriptions/drag belong to M08.

## Testing implications

Pure tests: 20→19, duplicate20, publish21 during drain, concurrent reset/publication, dispatcher rejection/disposal, nearest-neighbor bookmark fallback and all DPI/negative-origin/small-area geometry. Actual XAML: keyed row identity across battery/same-value/transport updates, exact copy values, stale details removed on loss/health, Back restores anchored scroll/focus, lossless actions during coalescing, 0/1/2/8/50 devices, long/unknown/missing data, overflow and doubled text; native bounds unchanged after show. Run full gate and capture new surfaces via capture-native.ps1.

## Open items

M06 pairing/diagnosis/action parity; M07 animation/reduced motion/ticking; M08 native environment/tray/bubble; M09 hardware/accessibility/integrated resources; M11 deployment; M12 device/soak. No release-readiness claim.

## Observed implementation results

- The shared Application-resource phone Geometry failed the native Path.Data assignment on this build, including through the typed setter. An equivalent fresh control-local PathGeometry using the unchanged existing vector works. The root cause of the shared-resource failure was not established; no renderer change or further resource investigation was made.
- A detail field's FrameworkElement.Parent is null before visual attachment; field groups now have explicit owners. Initial details assertions wait for the new XAML to load, rather than pretending detached controls are visible.
- Back restoration must wait for the reattached list to load and apply its keyed offset after native focus's bring-into-view work. A generation guard cancels stale restoration on newer navigation/disposal. The controlled test setup also waits for previous focus/view requests before setting its seven-DIP anchor. The one-pixel restoration assertion is retained and passes.
- Copied feedback uses the existing one-shot hold and is cancelled on replacement/navigation/disposal. Failed writes clear old feedback. The verification writer records fixture values in memory only, with no real clipboard or credential writes.
- Copy callbacks also retain their page device ID; an old callback is rejected after navigating to another device. Detached copy buttons are disabled. The actual presenter tests exercise that rejection separately from backend command revalidation.
- Source-boundary scan found a new property named Label; it was renamed Heading, preserving the existing WinForms guard unchanged. No old test assertion, warning threshold or dependency pin was relaxed.
