# Tray balloon notifications (V-5)

- **Date:** 2026-09-19
- **Author:** agent (Phase 8A)
- **Phase:** 8
- **Status:** current

## Problem

Phase 8 must deliver notifications through the existing unpackaged WinForms
tray icon (`ENGINEERING.md` §2.8 / §8.3) and record whether
`NotifyIcon.ShowBalloonTip` meets the quality bar on current Windows 11,
including quiet-time / Focus Assist behaviour and whether
`NIIF_RESPECT_QUIET_TIME` is actually reachable from WinForms.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| WinForms `NotifyIcon.ShowBalloonTip` implementation | https://github.com/dotnet/winforms/blob/e83409daa530605da4eb5f847c6740a520325d25/src/System.Windows.Forms/src/System/Windows/Forms/NotifyIcon.cs | commit `e83409da` (read 2026-09-19) |
| `NOTIFYICONDATAW` / `NIIF_*` | https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw | Microsoft Learn (current) |
| Notification area overview | https://learn.microsoft.com/en-us/windows/win32/shell/notification-area | Microsoft Learn (current) |
| `Shell_NotifyIconW` remarks (Win10/11 banner + quiet time) | https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw | Microsoft Learn (current) |
| Local host | Windows 11 build 10.0.26100 · .NET 10 WinForms · unpackaged `AdbView` | observed context |

## Version investigated

- OS: Windows 11 10.0.26100
- Runtime: .NET 10 (WinForms `NotifyIcon`)
- Deployment: unpackaged exe (Phase 8 before Phase 10 packaging)

## Findings

### ShowBalloonTip path

WinForms `NotifyIcon.ShowBalloonTip(timeout, title, text, tipIcon)` builds
`NOTIFYICONDATAW` with `uFlags = NIF_INFO` and maps `ToolTipIcon` to exactly:

- `Info` → `NIIF_INFO`
- `Warning` → `NIIF_WARNING`
- `Error` → `NIIF_ERROR`
- `None` → `NIIF_NONE`

It then calls `Shell_NotifyIconW(NIM_MODIFY, …)`. **It never ORs
`NIIF_RESPECT_QUIET_TIME` (0x80).**

### NIIF_RESPECT_QUIET_TIME

Official meaning (Windows 7+): do not display the balloon during “quiet
time” (first hour after first logon / after OS upgrade or clean install).
A notification sent with the flag during quiet time is **discarded, not
queued**; `NIN_BALLOONHIDE` may be delivered.

**WinForms cannot express this flag** via public `ShowBalloonTip` APIs.

### BalloonTipClicked

WinForms raises `BalloonTipClicked` when the user activates the balloon.
`ENGINEERING.md` §8.3 freezes click → show flyout (8B wires this).

### Windows 11 presentation (documented)

- Legacy balloon/`NIF_INFO` notifications appear as **banner** notifications.
- On Windows 11, when a banner times out or is dismissed, it **does not
  remain** in Notification Center (closer to legacy transient behaviour;
  Win10 retained them longer). Documented on `Shell_NotifyIconW` remarks.
- Focus Assist / Do Not Disturb can suppress banners independently of
  quiet-time; apps cannot override user Focus Assist from balloons.

### Unpackaged identity

Shell balloons attach to the existing tray icon. No AUMID / App SDK
registration is required for `ShowBalloonTip` on an unpackaged WinForms
`NotifyIcon` (matches Phase 8-before-10 constraint).

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| WinForms `ShowBalloonTip` only | Zero deps; matches §2.8 | No `NIIF_RESPECT_QUIET_TIME` |
| Thin `Shell_NotifyIcon` MODIFY on existing icon | Can set quiet-time flag; still no WinAppSDK | Small Host-only native seam in 8B |
| Windows App SDK `AppNotificationManager` | Official modern path | Forbidden unless V-5 forces swap; large dep |

## Chosen approach

- **8A:** policy/coordinator only; no balloons.
- **8B default:** `BalloonNotificationSink` via `ShowBalloonTip` +
  `ToolTipIcon` severity map (INFO/WARNING/ERROR).
- **8B quiet-time:** if ENGINEERING §8.3’s `NIIF_RESPECT_QUIET_TIME` must
  be honoured literally, add the **smallest** Host-only
  `Shell_NotifyIcon(NIM_MODIFY)` path that sets
  `dwInfoFlags = NIIF_* \| NIIF_RESPECT_QUIET_TIME` on the existing icon
  (reuse hwnd/id already owned by `NotifyIcon`). No WinAppSDK.
- Re-evaluate V-5 presentation quality during 8C headed acceptance; swap
  sink only if balloons fail the quality bar (`ENGINEERING.md` §2.8 risk).

## Why

Preserves zero-dependency tray citizenship, keeps packaging out of Phase 8,
and isolates quiet-time to a reviewable Host seam if required.

## Rejected approaches

- Pulling Windows App SDK solely for quiet-time
- Browser / WebView notifications
- Emitting from policy without a sink (8A stays Windows-free)

## Compatibility concerns

- Quiet time ≠ Focus Assist; both can hide balloons.
- Timeout argument is ignored on Vista+ (system accessibility timeout).
- Concurrent balloons are serialized by the shell.

## Testing implications

- 8A: hermetic timeline tests; `FakeNotificationSink` only.
- 8B: unit-test sink mapping; optional live smoke that a balloon appears.
- 8C: headed A06 acceptance; record DND/quiet-time if observed.

## Open items

- 8B: choose ShowBalloonTip-only vs Shell_NotifyIcon quiet-time OR.
- 8C: live V-5 presentation checklist on this host’s Win11 build.
