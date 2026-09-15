# Windows host integration: acrylic, tray anchoring, notifications, lifecycle

- **Date:** 2026-09-16
- **Phase:** pre-Phase 0 (architecture); refreshed in Phases 1, 8, 10
- **Status:** current

## Problem

A tray flyout must look and behave like a Windows flyout: real Desktop Acrylic on a frameless
window, correct anchoring to the tray icon across taskbar positions and mixed DPI, light dismiss,
pinned always-on-top with native dragging, Explorer-restart survival, and quiet notifications —
with the fewest dependencies possible.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| `DWM_SYSTEMBACKDROP_TYPE` enumeration | https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type | requires Windows 11 build 22621 |
| PowerToys work replacing private `SetWindowCompositionAttribute` with public `DWMWA_SYSTEMBACKDROP_TYPE` on an overlay window | https://github.com/microsoft/PowerToys (PR 50494 discussion) | 2026 |
| Windows notifications overview / "use `AppNotificationManager` for new development" | https://learn.microsoft.com/en-us/windows/apps/develop/notifications/ | 2026-04 |
| Activating toast notifications from desktop apps (unpackaged AUMID/CLSID requirements) | https://learn.microsoft.com/en-us/windows/apps/design/shell/tiles-and-notifications/toast-desktop-apps | 2025-03 |
| Practitioner survey of tray icons + toasts in 2026 (Toolkit archived; WinRT maintenance-only) | https://comcomponent.com/en/blog/windows-tray-icon-toast-notification-guide/ | 2026-07 |
| WinForms `NotifyIcon` handling of the `TaskbarCreated` broadcast | .NET WinForms `NotifyIcon` source (`WmTaskbarCreated`) | .NET 10 |
| Notification-area behaviour on Explorer restart | https://support.microsoft.com/en-us/windows/desktop/shell/notification-area/ | current |

## Version investigated

Windows 11 (22621+ for backdrop attributes), .NET 10 WinForms, WebView2 1.0.4191.47.

## Findings

1. `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW` is the public API for Desktop Acrylic
   behind an entire window (`DWMSBT_MAINWINDOW` = Mica, `DWMSBT_TABBEDWINDOW` = Mica Alt,
   `DWMSBT_AUTO` lets DWM decide and may draw nothing). Windows 11 build 22621 minimum.
2. Two non-obvious constraints, both hit by shipping Windows utilities and fixed the same way:
   DWM **silently ignores** the backdrop attribute on frameless `WS_POPUP` windows (the fix is to
   keep `WS_CAPTION` and collapse the non-client area in `WM_NCCALCSIZE`), and DWM **flattens** the
   material to a solid tint on windows it considers inactive (the fix is to answer `WM_NCACTIVATE`
   with `TRUE` for compositing purposes only). A pinned flyout is inactive most of the time, so
   both apply here.
3. Private `SetWindowCompositionAttribute` / `ACCENT_POLICY` acrylic is no longer necessary and is
   actively being removed from Microsoft's own code in favour of the public attribute.
4. Tray anchoring: `Shell_NotifyIconGetRect` with a `NOTIFYICONIDENTIFIER` gives the icon rect
   (including a sensible rect when the icon lives in the overflow flyout);
   `SHAppBarMessage(ABM_GETTASKBARPOS)` gives the taskbar edge and rect as a fallback.
5. WinForms `NotifyIcon` re-adds its icon on the `TaskbarCreated` broadcast, so Explorer restarts
   are handled without custom code. Current Windows 11 no longer re-broadcasts `TaskbarCreated` on
   primary-DPI change, so tray crispness must come from multi-resolution icons.
6. Notifications in 2026: `Microsoft.Toolkit.Uwp.Notifications` is archived, bare WinRT
   `ToastNotificationManager` is maintenance-only, and Microsoft recommends Windows App SDK
   `AppNotificationManager` (works unpackaged, `Register()` handles AUMID/COM) for new work — at the
   cost of taking the Windows App SDK dependency. Unpackaged toasts without the App SDK require an
   AUMID and a shortcut with a CLSID. Shell balloon notifications through the app's own tray icon
   require none of that and cannot carry buttons.

## Chosen approach

Borderless WinForms `Form` with `WS_EX_TOOLWINDOW`, `ShowInTaskbar=false`, `WS_CAPTION` retained and
collapsed via `WM_NCCALCSIZE`, `WM_NCACTIVATE(TRUE)` for compositing, and DWM attributes for
immersive dark mode, small rounded corners and `DWMSBT_TRANSIENTWINDOW`; WebView2 transparent over
it, with the already-designed opaque token set as the fallback whenever the backdrop is unavailable.
Placement resolves tray icon rect → taskbar edge → cursor, then clamps inside the target monitor's
work area using that monitor's DPI. Pinned dragging uses WebView2 non-client region support
(`app-region: drag`) with a native `WM_NCHITTEST`/`HTCAPTION` fallback; exactly one mechanism ships.
Light dismiss uses window deactivation, not mouse hooks. Notifications go through the tray icon's
own balloon channel behind an `INotificationSink` seam. Startup-at-login is an installer-written
`HKCU\...\Run` value; single instance is a named mutex plus a control pipe; power/session/display
changes come from `SystemEvents`.

## Why

Every mechanism is the one Windows already owns, with no private APIs, no Windows App SDK, and no
tray-icon library. The notification choice matches the design (informational text, no actions) at
zero dependency cost, and the seam keeps the "official" path one isolated change away if quality
demands it.

## Rejected approaches

- **Private `SetWindowCompositionAttribute` acrylic** — undocumented, being removed upstream.
- **CSS `backdrop-filter` as production acrylic** — explicitly forbidden by `DESIGN.md §8`.
- **Windows App SDK `AppNotificationManager` in v1** — a large dependency plus runtime bootstrapper
  for capabilities the design forbids; kept as a documented fallback (V-5).
- **`Microsoft.Toolkit.Uwp.Notifications`** — archived.
- **Mouse hooks or polling for light dismiss** — fragile and unnecessary.
- **JS-simulated dragging** — `DESIGN.md §19` assigns dragging to the native host.
- **Task Scheduler or MSIX StartupTask for login start** — admin prompts or packaging identity.
- **Persisting pin state** — v1 starts unpinned every launch: deterministic with zero storage.

## Compatibility concerns

Windows 11 22621+ for backdrop; below that (or with transparency/composition disabled, or a contrast
theme active) the app reports `transparencyEnabled=false` and renders the opaque design. Tray icon
rect can be unavailable when the icon is hidden → taskbar-edge fallback. Balloon notifications are
suppressed by Do Not Disturb; the tray tooltip and flyout remain the authoritative channels.

## Testing implications

Placement is implemented as pure functions over rects so all four taskbar edges, work-area clamping,
mixed DPI and small screens are unit-testable. Integration tests cover show/hide, light dismiss,
pinned deactivation, Explorer restart, second-instance toggle and WebView process-failure recovery.
The acrylic and drag behaviours require manual verification in Phase 1 (V-2, V-4), as does balloon
presentation in Phase 8 (V-5).

## Open items

V-2 (acrylic on the pinned, unfocused flyout with a transparent WebView2), V-3
(`Shell_NotifyIconGetRect` in overflow/hidden and mixed-DPI cases; WebView2 clipboard-write
permission), V-4 (`app-region: drag` in a frameless host), V-5 (balloon quality on current
Windows 11).
