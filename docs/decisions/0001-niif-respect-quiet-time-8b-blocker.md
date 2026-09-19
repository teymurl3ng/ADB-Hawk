# 0001 — NIIF_RESPECT_QUIET_TIME not required for Phase 8B

- **Date:** 2026-09-19
- **Status:** ACCEPTED — Option A
- **Phase:** 8B

## Issue

`ENGINEERING.md` §8.3 originally listed `NIIF_RESPECT_QUIET_TIME` alongside
`BalloonNotificationSink`. Phase 8B hard rules forbid private reflection into
`NotifyIcon`, a second tray icon, and a Phase 1 tray rewrite — the only ways
to OR that flag onto the existing WinForms icon.

## Facts

1. WinForms `NotifyIcon.ShowBalloonTip` maps severity to `NIIF_INFO` /
   `NIIF_WARNING` / `NIIF_ERROR` and never ORs `NIIF_RESPECT_QUIET_TIME`
   (0x80). See `research/2026-09-19-tray-balloon-notifications.md`.

2. Microsoft defines Shell "quiet time" as the first hour after first login
   to a new user account, or after OS upgrade / clean install. Outside that
   window the flag has no effect. It is **not** general Focus Assist /
   Do Not Disturb semantics.

3. Applying the flag to the existing icon would require private hwnd/uID
   reflection, re-owning the tray via native GUID registration, or
   WinAppSDK — all disproportionate to this narrow legacy Shell behaviour.

## Decision

**Option A — accepted.**

Keep the frozen v1 backend: `NotifyIcon.ShowBalloonTip`.

Amend `ENGINEERING.md` §8.3 to require Info / Warning / Error only.
Do **not** require `NIIF_RESPECT_QUIET_TIME`.
Do **not** claim Focus Assist / Do Not Disturb integration.

## Rejected alternatives

| Option | Rejected because |
|---|---|
| **B. Private reflection** | Unsupported WinForms internals; tray fragility. |
| **C. Native GUID tray rewrite** | Phase 1 architecture rewrite disproportionate to Phase 8 requirement. |
| **D. Windows App SDK** | New dependency / packaging architecture outside v1 Phase 8 scope. |

## Follow-on

Phase 8B proceeds with `BalloonNotificationSink` → `ITrayBalloonHost` →
existing `NotifyIcon.ShowBalloonTip`. No coordinator→StateEngine wiring in 8B.
