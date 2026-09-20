# Pre-Phase-10 header drag — real interaction + padding dead-zone fix

- **Date:** 2026-09-20
- **Status:** root cause identified; product fix applied; SendInput probe limitation recorded

## Human report

“Sometimes I can't hold the entirety of the top of ADB View to move it;
mostly the bottom half works.”

## Root cause (product)

Flyout chrome is `main.p-4` (16 px padding) + `header.h-10` (40 px).

Only `header` had `-webkit-app-region: drag`. The top padding band is part of
what users treat as “the top of ADB View” but was **not** a drag region —
so the upper strip of the chrome was dead while the lower header pixels worked.

## Fix

When pinned, `main` / `[data-testid=flyout-shell]` is `app-region-drag`.
Scroll content remains `app-region-no-drag`. Pin remains `app-region-no-drag`.
Unpinned shell stays `app-region-no-drag` (frozen non-draggable).

## Real mouse probe (`HeaderRealDragProbeTests`)

Attempts `SendInput` absolute mouse down/move/up over a headed WebView2 +
`FlyoutWindow` and measures HWND bounds.

**Result on this agent host:** SendInput returns success and
`GetAsyncKeyState(VK_LBUTTON)` can show down, but **WebView2 does not start
a caption-drag move** for injected input (0 px HWND delta on all points,
including known drag CSS). Grid: 65/65 “failures” under that criterion.

Therefore automated **window-moved** proof via synthetic input is not
available in this environment; human verification remains required for G.

CSS/DOM sampling still confirms points resolve to drag-layer / shell / pin
as expected after the padding fix.

## DPI

Probe records `DeviceDpi` (96 on the diagnosis machine). Full 100–200%
matrix remains human + Release build.
