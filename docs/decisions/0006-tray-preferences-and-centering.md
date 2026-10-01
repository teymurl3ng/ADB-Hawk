# 0006 — Native tray preferences + symmetric scroll gutters

- **Date:** 2026-09-21
- **Status:** ACCEPTED — explicit human design-authority change
- **Phase:** pre-Phase-10D product polish

## Issue

1. With classic scrollbars, `scrollbar-gutter: stable` reserved space on one
   inline edge, so the flyout content looked left-shifted when no scrollbar
   thumb was visible.
2. Humans approved a small set of tray context-menu settings (Start with
   Windows, Notifications, Theme) without a settings screen — superseding the
   prior “Exit only” tray menu rule.

## Decision

**Centering:** `.flyout-scroll` uses `scrollbar-gutter: stable both-edges`.

**Tray menu (v1):**

```
Open ADB View
----------------
Start with Windows        [check]
Notifications             [check]
Theme >
    System                [one checked]
    Light
    Dark
----------------
Exit
```

- No settings screen, modal, or WebView preferences page.
- Checked state refreshed on every `ContextMenuStrip.Opening` from
  authoritative registry/preferences.
- Start with Windows uses the same `HKCU\...\Run` value name as the installer
  (`ADB View`) with quoted path and `--startup`.
- Notifications master-switch suppresses all app balloons; drops pending;
  re-enable rebaselines (no backfill).
- Theme preference System/Light/Dark resolves to effective Light/Dark via
  existing SystemTheme + shell appearance bridge (preference itself is not
  an AppSnapshot field). Forced-colors remain authoritative.

## Follow-up

- Update `DESIGN.md` §33 and related tray wording
- Update `AGENTS.md` R-03 tray-menu sentence
- Update `ENGINEERING.md` startup / preferences / no-settings claims
- Update `docs/DESIGN.sha256` in the same change
- Research: `research/2026-09-21-tray-preferences-and-centering.md`
