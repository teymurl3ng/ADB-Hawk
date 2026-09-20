# Pre-Phase-10 acceptance: caption, motion, theme, serial, startup

- **Date:** 2026-09-20
- **Author:** agent (pre-Phase-10 acceptance + FLIP + Fluent 333 ms + Pair fixes)
- **Phase:** post-9 / pre-10 product acceptance
- **Status:** current — automated gates green; motion + Pair still need human verdict

## Acceptance status (human authoritative)

| Defect | Status |
|---|---|
| 1 Caption | **HUMAN PASS** |
| 2 Motion / native-window expand | **AUTOMATED PASS / HUMAN REQUIRED** (Fluent 333 ms Point-to-Point) |
| 3 Light theme | **HUMAN PASS** |
| 4 Wi-Fi Serial semantics | **HUMAN PASS** |
| 5 Manual launch indication | **HUMAN PASS** (automated reconfirmed) |
| Pair button click | **FIXED** (hit-area `::after` stole clicks; handlers on host control) |
| Pair hover | **FIXED** (CSS `:hover` on Pair host + 30-cycle Playwright) |
| Raw `adb-*` pairing name | **FIXED** (`PairingDisplayName`; never instance fallback) |

Prior instrumented “smoothness PASS” does **not** count. Human: “When more phones are available and you click details, the app jitters around.” Manual launch: “literally zero indication that it started.”

## Problem

Five Release defects originally. Correction pass fixes **only** motion jitter and manual-launch visibility without touching the three human-accepted fixes unless a shared root cause forces it.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| DWM system backdrop / immersive dark mode | https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type | Win11 22621+ |
| SetWindowPos SWP_FRAMECHANGED | https://learn.microsoft.com/windows/win32/winuser/nf-winuser-setwindowpos | current |
| WM_NCCALCSIZE | https://learn.microsoft.com/windows/win32/winmsg/wm-nccalcsize | current |
| CSS grid-template-rows transition | https://developer.mozilla.org/en-US/docs/Web/CSS/grid-template-rows | current |
| ResizeObserver | https://developer.mozilla.org/en-US/docs/Web/API/ResizeObserver | current |
| requestAnimationFrame throttling in background | https://developer.mozilla.org/en-US/docs/Web/API/window/requestAnimationFrame | current |
| ENGINEERING.md §9.1 / sizing / first show | repo | Phase 9+ |
| DESIGN.md §§21–26 motion / light | repo | frozen |

## Version investigated

Windows 11 10.0.26100, .NET 10, WebView2 Evergreen, Chromium in WebView2.

---

## Correction-pass root causes (measured)

### Motion — feedback loop **YES**

Live Release log excerpt (two-device expand/collapse, 2026-09-20 01:44):

```
ResizeFlyout: applied 399 … UpdateFlyoutBounds Y=… Height=…
ResizeFlyout: applied 400 … Y increases / height +1
… every ~7 ms through 406 …
ResizeFlyout: applied 385 … 369 … 357 … (collapse, Y walks the other way)
```

Architecture today:

CSS `grid-template-rows` transition
→ ResizeObserver on content
→ rAF-coalesced `reportContentSize` (many intermediate heights)
→ `ResizeFlyout` → `UpdateFlyoutBounds` → `SetWindowPos`/Bounds
→ WebView viewport resize
→ layout recomputation
→ ResizeObserver again

Also observed: unpinned **Y recomputed every frame from tray anchor + new height**, so the window “jitters” as height and top edge both move. Duplicate near-equal heights still applied when values differ by 1 px.

Exact failure: **two independent size authorities** (CSS transition intermediates + native immediate resize) fighting; no single structural target.

### Manual launch — pending show never completes

Clean Release launch log (2026-09-20 02:07:47):

```
pendingShow=true
WebView2 initialized / NavigationCompleted
Bridge ready handshake accepted (protocol 1). Resending snapshot rev 4.
… no ResizeFlyout …
… no ShowFlyout / completing pending …
OnFlyoutDeactivate (spurious; flyout never shown)
```

Exact failure: **deadlock**. First show requires a valid `reportContentSize`, but `useContentSize` only measures inside `requestAnimationFrame`. While the hosting `Form` stays `Visible=false`, WebView2 throttles/skips rAF, so no height is ever reported, so `TryCompletePendingInitialShow` never runs.

Secondary risk once show works: `OnFlyoutDeactivate` → `HideFlyout` can fire during/after programmatic `Show` + `SetForegroundWindow` before the user perceives the window. Initial show must not be light-dismissed by that activation churn.

## Chosen correction approach

1. **One structural resize request** with truthful final content height (DOM measure with transitions forced off for one synchronous layout pass — no hard-coded expanded heights).
2. **Native owns** outer HWND interpolation for 250 ms with frozen easing; latest target retargets; fixed taskbar-adjacent edge for the duration.
3. **ResizeObserver live reports ignored** while structural animation runs; **reconcile** once at end.
4. **Startup:** sync/`setTimeout(0)` measure path so first size arrives while hidden; pending flag cleared only after `Visible`; light-dismiss armed only after show message batch (not cleared by sync deactivate during Show).

## Compatibility concerns

Unmapped action members are skipped (forward-compatible). Optional `phase` on `reportContentSize` is additive — not a protocol bump.
