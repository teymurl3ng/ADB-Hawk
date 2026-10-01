# Tray preferences, startup registration, and scrollbar centering

- **Date:** 2026-09-21
- **Author:** agent
- **Phase:** pre-Phase-10D product polish (human-approved)
- **Status:** current

## Problem

Human acceptance required (1) symmetric horizontal gutters when classic scrollbars
reserve space and (2) a native tray context menu for Start with Windows,
Notifications, and Theme — without a settings screen — before packaging CI closure.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| MDN `scrollbar-gutter` | https://developer.mozilla.org/en-US/docs/Web/CSS/scrollbar-gutter | CSS Overflow Module Level 3; `stable both-edges` |
| WinForms `NotifyIcon` / `ContextMenuStrip` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon | .NET 10 |
| WinForms `ContextMenuStrip.Opening` | https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.toolstripdropdown.opening | .NET 10 |
| HKCU Run key (Run and RunOnce registry keys) | https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys | current |
| `SystemEvents.UserPreferenceChanged` | https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.systemevents.userpreferencechanged | .NET 10 |
| Existing repo notes | `research/2026-09-16-windows-host-integration.md`, Phase 10 packaging notes | 2026-09 |

## Version investigated

- .NET SDK / TFM: `net10.0-windows` (repo pin)
- WebView2 / Chromium: Evergreen (scrollbar-gutter supported since Chromium 94)
- Inno Setup: 6.x; Run value name `ADB View`, command `"…\AdbView.exe" --startup`

## Findings

1. `.flyout-scroll` used `scrollbar-gutter: stable`, which reserves the classic
   scrollbar width on **one** inline edge. With no overflow, that one-sided reserve
   produces a larger right gap — matching the human screenshot.
2. MDN documents `scrollbar-gutter: stable both-edges` for symmetric reservation.
3. No existing user-preference store; pin remains non-persisted. Startup is today
   installer-only (`HKCU\...\Run` / `ADB View`).
4. `SystemTheme` + `SystemEvents.UserPreferenceChanged` already drive effective
   `ThemeKind` into `AppSnapshot.Shell.Theme` for native + WebView. ThemePreference
   must resolve to that same effective field — do not add preference to AppSnapshot.
5. `NotificationCoordinator` has no master enable flag; pending holds must be
   cancelled when Notifications are turned off, with rebaseline on re-enable.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Magic left padding / JS scrollbar width | Quick visual hack | Forbidden; brittle under DPI/overlay scrollbars |
| `scrollbar-gutter: stable both-edges` | Standards-based; no JS | Header (outside scroll) stays full-width — intentional |
| Settings WebView / WinForms dialog | Familiar UI | Explicitly out of scope; violates no-settings-screen |
| Second Run value / Startup folder / Task Scheduler | More control | Diverges from installer; elevation / opacity risks |
| Windows App SDK theme APIs | Modern | Avoided dependency; existing SystemTheme suffices |

## Chosen approach

- CSS: `scrollbar-gutter: stable both-edges` on `.flyout-scroll`.
- Native `ContextMenuStrip` with Open / Start with Windows / Notifications /
  Theme submenu / Exit; refresh checks on `Opening`.
- `UserPreferences` under `HKCU\Software\ADBView`; `StartupRegistration` owns the
  same Run value as Inno; `ThemePreference` → `EffectiveTheme` → existing shell path.
- Notification master switch on `NotificationCoordinator` (drop pending; rebaseline).

## Why

Matches documented Windows mechanisms already used by packaging, preserves opaque
v1 surface and architecture boundaries, and fixes centering without pixel hacks.

## Open questions / risks

- Overlay scrollbars may reserve no gutter (OS setting); asymmetry then disappears
  naturally — both-edges remains correct when classic scrollbars apply.
- Windows Startup Apps may independently suppress the Run entry; the menu reflects
  ADB View's registration presence only (documented; no StartupApproved mutation).

## Decision for agents

Implement the chosen approach. Do not add a settings screen. Do not begin Phase 10D
or Phase 11 as part of this polish wave.
