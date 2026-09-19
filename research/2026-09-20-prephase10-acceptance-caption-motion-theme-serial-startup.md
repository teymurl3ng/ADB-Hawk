# Pre-Phase-10 acceptance: caption, motion, theme, serial, startup

- **Date:** 2026-09-20
- **Author:** agent (pre-Phase-10 acceptance wave 1)
- **Phase:** post-9 / pre-10 product acceptance
- **Status:** current

## Problem

Five Release defects: intermittent native caption, jerky card/window expansion,
partial light theme, Wi-Fi endpoint shown as Serial, and silent manual launch.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| DWM system backdrop / immersive dark mode | https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type | Win11 22621+ |
| SetWindowPos SWP_FRAMECHANGED | https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos | current |
| WM_NCCALCSIZE | https://learn.microsoft.com/windows/win32/winmsg/wm-nccalcsize | current |
| CSS grid-template-rows transition | https://developer.mozilla.org/en-US/docs/Web/CSS/grid-template-rows | current |
| ResizeObserver | https://developer.mozilla.org/en-US/docs/Web/API/ResizeObserver | current |
| ENGINEERING.md §9.1 / §5.1 / acrylic notes | repo | Phase 9 HEAD |
| DESIGN.md §§21–26 motion / light | repo | frozen |
| research/2026-09-16-windows-host-integration.md | repo | current |

## Version investigated

Windows 11 10.0.26100, .NET 10, WebView2 Evergreen, Chromium in WebView2.

## Findings

### A. Caption / frame

ENGINEERING requires **keep `WS_CAPTION` + collapse NC via `WM_NCCALCSIZE`**. Current
`FlyoutWindow.CreateParams` **strips** `WS_CAPTION`. Intermittent visible caption /
blue strip matches a lifecycle where non-client layout is not forced until a
subsequent size change (user expand) re-enters `WM_NCCALCSIZE`. Official guidance:
after style/custom-frame establishment, call `SetWindowPos(..., SWP_FRAMECHANGED)`
to force NC recalculation without moving/sizing.

### B. Motion

`DeviceCard` mounts/unmounts details with `{isExpanded && …}` — **no structural
CSS transition**. `useContentSize` already ResizeObserver + rAF-coalesces; once
layout animates, intermediate heights reach native `ResizeFlyout`. Prefer
`grid-template-rows: 0fr → 1fr` at 250 ms with frozen easing; no rAF choreography
beyond the existing measure coalesce.

### C. Light theme

Frontend light tokens come from `@media (prefers-color-scheme: light)` while
`AppSnapshot.Shell.Theme` stays hardcoded Dark and native opaque fallback is
always dark grey (`#1E1E1E`). Acrylic immersive dark mode is registry-read once
at handle create and not coordinated with snapshot. Need SystemTheme → shell
event → native `ApplyBackdrop(theme)` + `[data-theme]` driven tokens.

### D. Wi-Fi Serial

For endpoint-only Wi-Fi, `HardwareSerial` falls back to `host:port`. Mapper sets
`Detail.Serial` and `Address` both to that string. Display Serial must omit when
the value is only a transport endpoint; `AdbSerial` remains the transport selector.

### E. Startup

Normal interactive launch never requests `ShowFlyout` (only tray toggle / fixture).
`--startup` is listed in ENGINEERING file tree but not parsed. Freeze: normal
first launch → pending show after presentable size; `--startup` → tray only.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Cover caption with CSS | Quick | Forbidden; violates shell ownership |
| Remove WS_CAPTION permanently | Hides caption | Breaks Acrylic per ENGINEERING |
| Framer Motion / rAF height tween | Smooth | Forbidden dependency / dual animation |
| Paint opaque light CSS over Acrylic | Looks light | Forbidden; kills material |

## Chosen approach

1. Restore `WS_CAPTION`, keep NC collapse, centralize `RecalcNonClientFrame`.
2. CSS grid structural expand + ResizeObserver size follow; single expanded card.
3. `SystemTheme` + shell appearance events; native light/dark backdrop + light opaque fallback; `[data-theme]` tokens.
4. Nullable display `Serial`; omit endpoint-only; Address from `AdbSerial`.
5. Parse `--startup`; pending initial show for normal launch.

## Compatibility concerns

DWM invisible borders vs caption height (~32px): frame tests use tolerance that
rejects full caption inset but allows few-pixel DWM borders.
