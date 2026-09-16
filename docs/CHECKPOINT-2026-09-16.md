# Checkpoint — 2026-09-16 (Phase 2 Visual Regression)

## Status: In Progress — native flyout background rendering bug

---

## What Exists

### Browser Playwright Harness (DONE, GREEN)
- 16 screenshots captured and verified: 8 dark + 8 light across 8 fixtures
- `ui/playwright.config.ts`: chromium-dark/light projects, 376x600 viewport
- `ui/tests/visual/fixtures.spec.ts`: test-only background injection via `page.evaluate()` (dark=#202329, light=#ffffff)
- `vitest.config.ts`: excludes `tests/visual/**` to avoid Playwright colliding with Vitest
- `verify.ps1` was fully green before the CSS changes made in this session

### Native Flyout Capture (WORKING)
- `scripts/capture-native.ps1`: rewritten to use `EnumWindows` by process ID (FindWindow was unreliable)
- `--test-fixture=connected-usb` CLI arg in `Program.cs` for deterministic native capture
- `CopyFromScreen` works for capturing the flyout on screen
- PrintWindow/BitBlt return transparent (expected — DWM Acrylic window surface is GDI-transparent)

### DWM Backdrop (RENDERING, BUT WRONG COLOR)
- `FlyoutWindow.cs` applies: `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW` (3), `DWMWA_USE_IMMERSIVE_DARK_MODE = 20` (value=1), `DwmExtendFrameIntoClientArea(margins=-1)`
- Both DWM calls return S_OK (0x00000000)
- WebView2 `DefaultBackgroundColor = Color.Transparent`
- `BackColor = Color.Black` on the form

### Theme Sync (VERIFIED CORRECT, NO MISMATCH)
- Registry `AppsUseLightTheme=0` → WebView reports `prefers-dark=true, prefers-light=false, forced-colors=false`
- HTML bg=transparent, body bg=transparent (was), body color=rgb(230,237,243)
- **The original "mismatch" concern was a red herring — themes are in sync**

---

## THE BUG: Native Background Washed Out

### Symptom
The native flyout renders with a **#363636 gray** background instead of the design-specified **#202329** dark blue-gray. Pixel analysis of `CopyFromScreen` captures confirms this.

### Root Cause (NARROWED, NOT FIXED)
The DWM Acrylic backdrop applies its own system-controlled tint (#363636 neutral gray) over the window. The CSS `body { background-color: #202329 }` is **not painting over the Acrylic tint** — the DWM compositor's Acrylic material shows through at full strength, ignoring the CSS background.

This was confirmed by:
1. Setting `body { background-color: #202329 }` (literal, fully opaque) — pixels still show #161616/#363636 range
2. Setting `body { background-color: var(--surface-flyout) }` where `--surface-flyout: rgba(32,35,41,0.94)` — same result
3. The Vite build output contains both the token and the body rule correctly

### What Was Tried (ALL FAILED)
| Attempt | Result |
|---|---|
| `DWMSBT_MAINWINDOW` (Mica, value 2) | Worse — reddish-brown tint |
| `WS_EX_NOREDIRECTIONBITMAP` (0x200) | Worse — white frame + desktop bleed |
| CSS literal `#202329` on body | No change — DWM Acrylic still #363636 |
| CSS `var(--surface-flyout)` rgba(32,35,41,0.94) | No change |

### Likely Fix Direction
The DWM Acrylic composition is overriding the WebView2 CSS paint. Possible approaches:
1. **Adjust `DwmExtendFrameIntoClientArea` margins** — the `-1` margins extend glass into entire client area; try `0,0,0,0` or specific margins so the client area renders independently of the Acrylic backdrop
2. **Remove DWM Acrylic entirely** and use solid `BackColor = Color.FromArgb(32, 35, 41)` — loses the blur effect but gets exact color control. DESIGN.md says opaque fallback is a first-class state (§22)
3. **Try `WS_POPUP` style instead of `WS_CAPTION` + `WM_NCCALCSIZE`** — though research notes say DWM ignores backdrop on frameless WS_POPUP
4. **Check if the WebView2 environment args affect rendering** — `--disable-gpu` or similar flags

---

## Files Changed (UNCOMMITTED)

### Modified
| File | Changes |
|---|---|
| `.gitignore` | Added `node_tool/` |
| `src/AdbView/AdbView.csproj` | (check diff) |
| `src/AdbView/Diagnostics/LogConfig.cs` | Added `buffered: false` to file sink |
| `src/AdbView/Host/AppHost.cs` | Forced `_flyoutWindow.Handle` access, `--test-fixture=` parsing, placement logging |
| `src/AdbView/Host/FlyoutWindow.cs` | `BackColor=Black`, DWM backdrop + dark mode, EnumWindows, diagnostic `File.AppendAllText` logging |
| `src/AdbView/Host/TrayIcon.cs` | (check diff) |
| `src/AdbView/Host/WebViewHost.cs` | `DefaultBackgroundColor=Transparent`, WebView theme diagnostic logging |
| `src/AdbView/Program.cs` | `--test-fixture=` CLI arg parsing |
| `ui/src/App.test.tsx` | (check diff) |
| `ui/src/App.tsx` | (check diff) |
| `ui/src/styles/app.css` | Added `--surface-flyout` tokens, body `background-color: var(--surface-flyout)` / `#202329` |
| `ui/vitest.config.ts` | Added `exclude: ['tests/visual/**']` |

### New Files
| File | Purpose |
|---|---|
| `scripts/capture-native.ps1` | EnumWindows + CopyFromScreen native capture script |
| `ui/playwright.config.ts` | chromium-dark/light projects for visual regression |
| `ui/tests/visual/fixtures.spec.ts` | Playwright test fixtures with background injection |
| `ui/src/components/` | (check contents) |
| `ui/src/types.ts` | (check contents) |
| `assets/` | (check contents) |

### Debug Code To Clean Up Before Completion
- `FlyoutWindow.cs`: `File.AppendAllText` in `OnHandleCreated` and `ApplyBackdrop()`
- `FlyoutWindow.cs`: Forced `_flyoutWindow.Handle` in `AppHost.Run()`
- `AppHost.cs`: Placement logging in `ShowFlyout()`
- `WebViewHost.cs`: WebView theme diagnostic `File.AppendAllText`
- `LogConfig.cs`: `buffered: false` (may need reverting for production)

---

## Key Architecture Facts

- **Frozen docs**: `DESIGN.md`, `ENGINEERING.md`, `AGENTS.md`, `PHASES.md` — do not edit
- **Frozen Stitch archive**: `design/reference/stitch_adb_view_frozen_design.zip` (B.1 visual master)
- **Toolchain**: .NET 10.0.410, Node 24.21.0, npm 11.19.0, TypeScript 6.0.3
- **Shell PATH fix**: `$env:USERPROFILE\.dotnet;$env:LOCALAPPDATA\Programs\node-v24.21.0;$env:PATH`
- **npm**: Use `npx.cmd` (npm.ps1 blocked by execution policy)
- **DPI scaling**: Screen is 1920x1020 logical, screenshot captures at 1536x864 (scale 0.8)
- **ENGINEERING.md §37**: Acrylic requires `WS_CAPTION` + `WM_NCCALCSIZE` collapse + `WM_NCACTIVATE(TRUE)` for pinned unfocused windows
- **ENGINEERING.md §954**: `--surface-flyout: rgba(32, 35, 41, 0.94)` over native acrylic
- **DESIGN.md §22**: Opaque token set is a first-class fallback state

## Next Steps (in order)
1. Fix the DWM Acrylic / CSS layering so body background renders (try margins, or drop Acrylic for solid BackColor)
2. Verify pixel colors match #202329
3. Clean up all diagnostic debug code
4. Re-run `verify.ps1` gate
5. Produce final before/after evidence
6. Do NOT tag Phase 2, do NOT begin Phase 3
