# Native approaches for a small always-on-top status bubble

- **Date:** 2026-10-03
- **Author:** coding agent (owner-requested feature, decision 0020)
- **Phase:** post-I2 hardening (feature N)
- **Status:** current

## Problem

Decision 0020 adds a very small, persistent, always-on-top status bubble that must never take
focus, must sit above the taskbar in the bottom-right, and must stay out of the way of
full-screen applications. The project's existing window (the flyout) deliberately does the
opposite on activation — it calls `SetForegroundWindow` so light-dismiss works — so the bubble
cannot copy that part. This note establishes the native recipe and the pitfalls before any
window/DWM/DPI/placement code is written (R-10).

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| Extended Window Styles | https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles | current docs |
| Window Features (layered windows, click-through) | https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features | current docs |
| `Window.ShowActivated` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.showactivated | .NET current |
| `Window.AllowsTransparency` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency | .NET current |
| `Window.Topmost` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.topmost | .NET current |
| `WindowInteropHelper` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.windowinterophelper | .NET current |
| `Window.SourceInitialized` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.sourceinitialized | .NET current |
| `SetWindowPos` | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos | current docs |
| `DWM_WINDOW_CORNER_PREFERENCE` | https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_window_corner_preference | min build 22000 |
| `DWMWINDOWATTRIBUTE` | https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute | min build 22000 |
| `DwmSetWindowAttribute` | https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmsetwindowattribute | current docs |
| `ABM_GETTASKBARPOS` | https://learn.microsoft.com/en-us/windows/win32/shell/abm-gettaskbarpos | current docs |
| `ABM_GETSTATE` | https://learn.microsoft.com/en-us/windows/win32/shell/abm-getstate | current docs |
| `GetMonitorInfo` / `MONITORINFO` | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmonitorinfoa | current docs |
| `SystemParameters.WorkArea` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.systemparameters.workarea | .NET current |
| `GetDpiForWindow` | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow | Win10 1607+ |
| `WM_DPICHANGED` | https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged | current docs |
| `SHQueryUserNotificationState` / `QUERY_USER_NOTIFICATION_STATE` | https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate | current docs |
| `SetForegroundWindow` (restrictions) | https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow | current docs |
| Application Desktop Toolbars (`ABN_FULLSCREENAPP`) | https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars | current docs |
| Raymond Chen — "How can I have a window that rejects activation but still receives pointer input?" | https://devblogs.microsoft.com/oldnewthing/20160912-00/?p=94295/ | 2016-09-12 |
| Raymond Chen — "My window has the WS_EX_NOACTIVATE extended style, but it got activated anyway" | https://devblogs.microsoft.com/oldnewthing/20240919-00/?p=110283/ | 2024-09-19 |
| Raymond Chen — "How can I query the location of the taskbar on secondary monitors?" | https://devblogs.microsoft.com/oldnewthing/20141218-00/?p=43353 | 2014-12-18 |
| TrafficMonitor (open-source tray monitor; topmost tooltip handling) | https://github.com/zhongyang219/TrafficMonitor | current |
| PowerToys Always On Top (explicit topmost management) | https://github.com/microsoft/PowerToys/blob/main/src/modules/alwaysontop/AlwaysOnTop/AlwaysOnTop.cpp | current |

## Version investigated

Windows 11 10.0.26100; .NET 10 WPF (`UseWPF` + `UseWindowsForms`); `Microsoft.Windows.CsWin32`
for P/Invoke; existing project research `research/2026-09-16-windows-host-integration.md`
(the flyout already uses DWM attributes 20 / 33 / 38 and found DWM ignores some attributes on
frameless `WS_POPUP` windows, requiring "keep `WS_CAPTION`, collapse the non-client frame").

## Findings

**Non-activation.** `WS_EX_NOACTIVATE` (0x08000000) means the window "does not become the
foreground window when the user clicks it" and is excluded from the taskbar/Alt+Tab by default.
It suppresses *activation*, not input: mouse messages still arrive (Chen 2016 demonstrates
`WM_LBUTTONDOWN` arriving). Critically it is **not airtight** — Chen 2024 documents three
bypasses: programmatic `SetActiveWindow`/`SetForegroundWindow`; the window being selected from
Alt+Tab/taskbar if it is in them; and **active-window tracking (mouse hover)**, which does not
check the style. The documented mitigation for hover is handling `WM_MOUSEACTIVATE` and
returning `MA_NOACTIVATE`. `WS_EX_TOOLWINDOW` (0x80) keeps the window out of the taskbar and
Alt+Tab entirely.

**Showing without activation in WPF.** `Window.ShowActivated` must be `false` **before** `Show()`
— setting it afterwards has no effect — and it only governs the initial show, not later clicks.
`WindowStyle=None` is required for `AllowsTransparency=true`, which forces the `WS_EX_LAYERED`
path; that path is the one this repo already found to defeat DWM materials. There is no
published performance figure for WPF `AllowsTransparency` (community claims it is costly; treat
as unquantified).

**After changing window styles** `SetWindowLong(GWL_EXSTYLE, …)`, `SetWindowPos` must be called
with `SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER` for the change
to take effect — the same idiom the flyout's `RecalcNonClientFrame` already uses.

**Rounding without transparency.** `DWMWA_WINDOW_CORNER_PREFERENCE` (attribute 33, min build
22000) with `DWMWCP_ROUND` (2) or `DWMWCP_ROUNDSMALL` (3) rounds a normal window; the docs
qualify it with "if appropriate", which is **not defined**, and this repo previously found DWM
ignores some attributes on frameless `WS_POPUP` windows. `DWMWA_BORDER_COLOR` (34) with
`DWMWA_COLOR_NONE` (0xFFFFFFFE) suppresses the border. Both are unverified for our frame recipe
and must be tested empirically.

**Placement.** Prefer the target monitor's work area —
`GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)).rcWork` — then subtract
width/height/margin. This is edge-agnostic (works for a taskbar on any edge and on secondary
monitors). `SystemParameters.WorkArea`/`SPI_GETWORKAREA` is **primary-monitor only** and must not
be used here; Chen's guidance is explicitly to use the monitor work area rather than chasing the
taskbar. `ABM_GETTASKBARPOS`/`ABM_GETSTATE` (already used in `TrayIcon.cs`) give the taskbar edge
and `ABS_AUTOHIDE`; whether an auto-hidden taskbar is subtracted from `rcWork` is **not
documented** — commonly reported that it is not, so an extra margin is needed and the behaviour
must be verified on this machine.

**DPI.** `GetDpiForWindow` returns the monitor DPI for a per-monitor-aware process; handle
`WM_DPICHANGED` by adopting the suggested rect with `SWP_NOZORDER | SWP_NOACTIVATE`. The flyout
already does this.

**Full-screen and lock.** Topmost does not beat exclusive-mode D3D. The documented signal is
`SHQueryUserNotificationState`: show only when it reports `QUNS_ACCEPTS_NOTIFICATIONS`;
`QUNS_BUSY` (2), `QUNS_RUNNING_D3D_FULL_SCREEN` (3), `QUNS_PRESENTATION_MODE` (4) and
`QUNS_NOT_PRESENT` (1, locked/screensaver) mean stay away. This mirrors the appbar guidance that
an appbar must drop to the bottom of the z-order when `ABN_FULLSCREENAPP` opens. The UAC secure
desktop is a different desktop, so the bubble cannot appear there at all.

**Hover detail without fighting z-order.** The reliable option is a dedicated non-activating
topmost popup (what TrafficMonitor does, re-asserting `HWND_TOPMOST` on its tooltip). A WinForms
`ToolTip` is built for a control inside a normal window, and a WPF `ToolTip` on a top-level
`Window` is **not documented** and community results are mixed. The cheapest reliable alternative
is to expand the bubble itself on hover — no second window, no z-order contest.

**Click without activation.** Return `MA_NOACTIVATE` from `WM_MOUSEACTIVATE` (not
`MA_NOACTIVATEANDEAT`, which discards the click). Then open the flyout with
`SetForegroundWindow`: permitted because a genuine click means our process received the last
input event; the docs warn it can still be denied, so fall back to `FlashWindow` rather than
retrying.

**Flicker.** Position and resize before showing, in one `SetWindowPos(..., SWP_SHOWWINDOW |
SWP_NOACTIVATE)` call, and keep the HWND alive across hide/show instead of recreating it.

**Measured on this machine — a CAPTIONED window has a shell-enforced minimum size.**
`WindowStyle=SingleBorderWindow` (kept for DWM, the flyout's recipe) could not be made smaller
than **133x38 DIP**: `SetWindowPos` with a 28x28 px rect was accepted for position but the size
stayed at the minimum, stretching the 22 px capsule into a bar. `WindowStyle=None` (WS_POPUP) has
no such floor and produced an exact **22x22 DIP** window. Consequence for this feature: the bubble
must be frameless, which in turn puts DWM corner rounding (`ROUNDSMALL`) in doubt — the docs only
promise rounding "if appropriate" and this repo already found frameless windows ignore some DWM
attributes. Rounding must therefore be verified visually, with `SetWindowRgn` as the fallback
shaper for a non-layered window. (Not documented by Microsoft: the minimum size is a shell/UxTheme
behaviour, observed here rather than cited.)

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Opaque window + DWM corner rounding (chosen) | Native materials work; no layered path; matches the flyout's proven recipe | Win11 22000+; "if appropriate" is undefined — must verify rounding on our frame recipe |
| `AllowsTransparency=true` + WPF `Border CornerRadius` | Full shape control incl. outer shadow; works on Win10 | Layered path; blocks Mica/backdrop; perf unquantified; conflicts with the repo's DWM approach |
| WinForms `Form` for the bubble | Same toolkit as the existing flyout twin; `CreateParams` makes styles easy | R-04 puts WinForms in the message loop/tray only; WPF is where the native shell lives (decision 0015) |
| `WS_EX_LAYERED | WS_EX_TRANSPARENT` (click-through) | Never intercepts a click | Contradicts "clicking opens the flyout"; click-through is documented only for layered windows |
| Separate hover popup window | Rich hover content | A second topmost window to manage; z-order contest with the bubble itself |
| **Expand the bubble in place on hover (chosen)** | No second window; no z-order problem | The bubble changes size on hover (acceptable — it is ours) |
| `SystemParameters.WorkArea` | One line | Primary monitor only; wrong on multi-monitor — rejected per Chen |

## Chosen approach

A dedicated WPF window under `src/AdbView/NativeUi/`, alive for the app's lifetime, with:

- Extended styles `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, applied in
  `SourceInitialized` followed by `SetWindowPos(SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_NOMOVE |
  SWP_NOSIZE | SWP_NOZORDER)`.
- `ShowActivated=false` and `Topmost=true` set in the constructor (before `Show`), `WindowStyle`
  kept as the flyout uses it (captioned + non-client collapsed) so DWM attributes actually apply.
- `WM_MOUSEACTIVATE → MA_NOACTIVATE`, plus the existing `WM_NCCALCSIZE`/`WM_NCACTIVATE` handling.
- `DWMWA_WINDOW_CORNER_PREFERENCE` (33) for rounding — same as the flyout — and immersive dark
  mode (20) per theme; **no** `AllowsTransparency`.
- Placement from `MonitorFromWindow` + `GetMonitorInfo.rcWork`, reusing the existing
  `FlyoutPlacement` clamping and DPI helpers; extra margin when `ABM_GETSTATE` reports
  `ABS_AUTOHIDE`.
- Hidden unless `SHQueryUserNotificationState` accepts notifications.
- Hover expands the capsule in place; click opens the flyout; the 60 s transient expiry is a
  one-shot generation-guarded timer in the bubble's own view model.

## Why

The chosen shape reuses everything the flyout already proves on this machine (styles, DWM
attributes, DPI, placement math) and adds only the parts the flyout deliberately does not have —
non-activation and ambient visibility. It avoids the layered path, avoids a second window for
hover, and avoids new periodic work (R-05). The two unverified items — DWM rounding on our frame
recipe, and auto-hidden taskbar behaviour — are called out as an early spike and a live
verification row rather than assumed.
