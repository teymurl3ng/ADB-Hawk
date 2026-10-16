# WinUI native tray, receiver and bubble ownership

- **Date:** 2026-10-16
- **Author:** Codex
- **Phase:** M08 native-owner slice
- **Status:** current

## Problem

Port candidate native plumbing using proven M02 mechanisms, preserving production WPF, registry/IPC and all assertions. This slice is not full M08 live integration.

## Sources consulted

| Source | URL | Version / last updated |
| --- | --- | --- |
| Microsoft Shell_NotifyIconW | https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw | 2024-05-29; consulted 2026-10-16 |
| Microsoft NOTIFYICONDATAW | https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw | Win32 contract; consulted 2026-10-16 |
| Microsoft WTSRegisterSessionNotification | https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsregistersessionnotification | Win32 contract; consulted 2026-10-16 |
| Microsoft InputNonClientPointerSource | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.input.inputnonclientpointersource?view=windows-app-sdk-2.0 | App SDK API; consulted 2026-10-16 |
| Microsoft CreateIconFromResourceEx | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createiconfromresourceex | 2024-01-26; consulted 2026-10-16 |
| Microsoft RegisterClassW | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerclassw | 2023-02-09; consulted 2026-10-16 |
| Microsoft GetModuleHandleW | https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandlew | 2024-12-09; consulted 2026-10-17 |
| Microsoft SetMenuDefaultItem | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setmenudefaultitem | 2024-02-22; consulted 2026-10-17 |

## Version investigated

.NET SDK 10.0.401; Windows App SDK 2.5.1 / WinUI 2.3.9; CsWin32 0.3.333; Windows build 26100. Existing M02 NativeTrayProbe/NativeLifetimeProbe input proof and unchanged Core StatusBubblePresenter are reused. No package addition.

## Findings

Version negotiation follows every icon addition, including TaskbarCreated. Version-4 callbacks pack coordinates/IDs; legacy mouse-up must not double-toggle. Balloon clicks open. Broadcast reception requires a hidden top-level receiver rather than a message-only window. Native callbacks cannot unwind managed exceptions. Every owned HWND/menu/icon/hook must be disposed, and native failures reported. M02 already proved nonactivation using NOACTIVATE, mouse activation rejection and a thread-local CBT guard; do not reopen that investigation.

Use the raw borrowed executable module handle for class registration, never FreeLibrary it. Copy RT_ICON bytes into DWORD-aligned storage before CreateIconFromResourceEx, and DestroyIcon for every nonshared result. Preserve the two existing menu separators and default Open item with native menu state. The existing controlled SendInput helper is reused for actual bubble hover/down/click; no new input/automation provider is introduced.

Native fixtures configure the already-pinned Serilog file sink before creating native owners, using the correlated result path (or a GUID-named log beside the nonshipping executable for interactive capture). No production log/preference/IPC path is used. The native runner requires this log and rejects warning/error/fatal API outcomes, in addition to assertion/exit checks. Ordinary pre-M08 fixtures retain their existing behavior. This closes failure reporting for the newly introduced adapters without a new dependency.

## Alternatives considered

| Option | Pros | Cons |
| --- | --- | --- |
| CsWin32 adapters and hidden top-level receiver | Pinned interop, one dispatcher | Explicit resource ownership |
| WinForms tray / second pump | Legacy implementation | Forbidden replacement architecture |
| Message-only receiver | Small | Misses required broadcasts |

## Chosen approach

Candidate Host owns receiver/tray/native callbacks and one bounds owner; NativeUi owns a fixed 32-DIP bubble. Feed the existing Core aggregate/lifetime policy and snapshot vocabulary. Use random fixture identities and memory-only preference callbacks. Reuse existing local icon assets. Exercise recreation by deleting only the candidate icon and directly sending its receiver TaskbarCreated; no Explorer restart. Route hide/suspend through M07 cleanup.

## Why / rejected approaches

Reuse the proven narrow owners. No renderer/provider rewrite, new dependency/polling, third product surface or production preference/IPC change. Fixtures never reconfigure ADB or the machine.

## Compatibility concerns

Synthetic messages do not prove physical mixed-DPI, monitor unplug, Explorer restart or power/session behavior. Real native input requires independent observation. Actual startup, preferences, single instance and live backend composition remain open M08 work.

## Testing implications

Verify exact menu/check state, real icon add/modify/version/delete/recreation, fixed bubble nonactivation/tooltip/open, existing transient/permanent/suppression policy, keyboard and cleanup. Full existing gate/assertion map stays intact; capture changed surfaces on the recorded build.

## Native-owner slice result — 2026-10-17

The pre-log-owner full gate passes 1,067 C# cases and 29 headed Release fixtures / 833 assertions. The final-code native-only driver also passes 172 assertions and 11 external observations with its required isolated log and no warnings/errors. The unchanged M02 guarded input API/underlay covers actual hover/down/click, native dragging, pin/outside activation, Esc and native menu cancellation. Initial in-process UIA-only opening left another window foreground; preserved failures are not proof of a framework defect. Conditional actual input passes the unchanged activation criterion and subsequent in-process invocation. No provider/renderer experiment, permission bypass or third experiment. Final full-gate verification passes after the owner-click recheck: 1,067 cases, 29 fixtures / 833 assertions, no native warning/error/fatal outcomes. Earlier unchanged M07 resource and empty-fixture foreground failures remain recorded. M07 resource recheck measures +1 handle and +471,040 private bytes; its earlier +69-handle failure has no established cause and stays categorized for M09. Do not repair M07 or loosen assertions during M08. Only verification/capture/evidence work followed the 00:54 investigation deadline.

Final-binary bubble and flyout captures were inspected on build 26100 at 125%. The ordinary flyout fixture stays stable; the new bubble is 32 DIP and does not expand on hover. Native ownership disposes both created HWNDs, icon, menu/submenu and hooks; queued environment/expiry work cannot revive it. Synthetic owned-icon removal/recreation renegotiates version 4 without touching Explorer. Exact injected 60-second waits test existing Core lifetime; no actual wall-clock minute or physical power/display change is claimed. All prior sources/assertions and installed binary remain preserved.

## Remaining M08 acceptance

M08 cannot pass until live composition/startup/preferences/instance integration and required input/environment checks are complete. M09–M12 qualification/cutover/deployment/device/soak remain separate.

