# Tray balloon notifications (V-5)

- **Date:** 2026-09-19
- **Author:** agent (Phase 8A / 8B contract)
- **Phase:** 8
- **Status:** current

## Problem

Phase 8 must deliver notifications through the existing unpackaged WinForms
tray icon (`ENGINEERING.md` §2.8 / §8.3) and record whether
`NotifyIcon.ShowBalloonTip` meets the quality bar on current Windows 11,
including whether `NIIF_RESPECT_QUIET_TIME` is reachable from WinForms and
what that flag actually means.

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

### NIIF_RESPECT_QUIET_TIME (Microsoft semantics)

Official meaning (Windows 7+): do not display the balloon during Shell
“quiet time” — specifically the **first hour after**:

- first login to a new user account, or
- an OS upgrade / clean installation.

A notification sent with the flag during that window is **discarded, not
queued**. Outside that period the flag has **no effect**.

This is **not** general Windows Focus Assist / Do Not Disturb support.
Focus Assist / DND can suppress banners independently; apps cannot override
user Focus Assist preferences from shell balloons.

**WinForms cannot express this flag** via public `ShowBalloonTip` APIs.

### BalloonTipClicked

WinForms raises `BalloonTipClicked` when the user activates the balloon.
`ENGINEERING.md` §8.3 freezes click → show flyout (8B wires this).

### Length limits (`NOTIFYICONDATAW`)

| Field | Capacity (incl. null) |
|---|---|
| `szInfoTitle` | 64 WCHARs |
| `szInfo` | 256 WCHARs |

WinForms copies into these buffers; oversized strings must be truncated
deterministically before delivery so they cannot throw or corrupt MODIFY.

### Windows 11 presentation (documented)

- Legacy balloon/`NIF_INFO` notifications appear as **banner** notifications.
- On Windows 11, when a banner times out or is dismissed, it **does not
  remain** in Notification Center (closer to legacy transient behaviour;
  Win10 retained them longer). Documented on `Shell_NotifyIconW` remarks.

### Unpackaged identity

Shell balloons attach to the existing tray icon. No AUMID / App SDK
registration is required for `ShowBalloonTip` on an unpackaged WinForms
`NotifyIcon` (matches Phase 8-before-10 constraint).

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| WinForms `ShowBalloonTip` only | Zero deps; matches §2.8 | No `NIIF_RESPECT_QUIET_TIME` |
| Thin `Shell_NotifyIcon` MODIFY on existing icon | Could set quiet-time flag | Needs private hwnd/uID reflection or GUID rewrite |
| Windows App SDK `AppNotificationManager` | Official modern path | Forbidden unless V-5 forces swap; large dep |

## Chosen approach

- **8A:** policy/coordinator only; no balloons.
- **8B:** `BalloonNotificationSink` via `ShowBalloonTip` + `ToolTipIcon`
  severity map (INFO/WARNING/ERROR). **No** `NIIF_RESPECT_QUIET_TIME`
  (contract Option A — see `docs/decisions/0001-niif-respect-quiet-time-8b-blocker.md`).
- Re-evaluate V-5 presentation quality during 8C headed acceptance; swap
  sink only if balloons fail the quality bar (`ENGINEERING.md` §2.8 risk).

## Why

Preserves zero-dependency tray citizenship and Phase 1 tray architecture.
Shell quiet-time is a narrow legacy window; paying reflection or a tray
rewrite for it is not justified. v1 does not claim Focus Assist / DND
integration.

## Rejected approaches

- Private reflection to OR `NIIF_RESPECT_QUIET_TIME` (Option B)
- Native GUID tray rewrite (Option C)
- Windows App SDK solely for quiet-time (Option D)
- Browser / WebView notifications
- Emitting from policy without a sink (8A stays Windows-free)

## Compatibility concerns

- Shell quiet time ≠ Focus Assist / DND; do not conflate them in docs or tests.
- Timeout argument is ignored on Vista+ (system accessibility timeout).
- Concurrent balloons are serialized by the shell.

## Testing implications

- 8A: hermetic timeline tests; `FakeNotificationSink` only.
- 8B: unit-test sink mapping against `ITrayBalloonHost`; fixture/headless
  must not construct a real balloon sink; one explicit manual smoke only.
- 8C: headed A06 acceptance; record DND observation if seen (environment-dependent).

## Open items

- **Closed (2026-09-19):** quiet-time flag requirement removed from §8.3
  (Option A). See decision `0001`.
- 8C: live V-5 presentation checklist on this host’s Win11 build.
