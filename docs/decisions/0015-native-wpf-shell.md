# 0015 — Native WPF shell: replace the WebView2 frontend

- **Date:** 2026-10-03
- **Status:** recorded retroactively (the decision was taken and implemented before this note
  existed; several source comments already cite it, so it is written up here for traceability)

## Problem

The product's flyout was a WinForms host plus a WebView2/React frontend. The two-renderer
runtime caused the defects the owner kept hitting: live sizing that "felt flaky" (per-frame
child resizes forcing Chromium relayout), animation that could not be made to feel native,
and a presentation layer that duplicated semantics the backend already owned.

## Decision

1. **Replace the frontend with a native WPF shell** on .NET 10, built for Fluent 2 idioms
   (Mica backdrop via DWM, Segoe Fluent glyphs, native controls) rather than reproducing the
   web pixels.
2. **Roll out side-by-side behind `--ui=native`**; the WebView path stays the default until
   cutover, so the backend and its test suite are unaffected.
3. **Rejected:** WinUI 3 (no tray control, tray-activation crash bugs, bootstrapper breaks
   xcopy deployment); Avalonia (third-party, non-native text stack).
4. **The backend is the single source of truth unchanged**: `State/` publishes immutable
   `AppSnapshot` records, `AppHost` keeps its explicit wiring, and the pairing/discovery
   coordinators are shared by both surfaces. The native shell binds the snapshot record
   directly (no JSON hop); `BridgeCoordinator` keeps the WebView contract alive during the
   transition, including an explicit `MarkReady()` for the native path (the WebView's `ready`
   handshake never happens there, and without it snapshots queued forever — the original
   "tray icon only, nothing works" report).
5. **Design authority:** the native screens produced for this shell (Stitch project
   "ADB View Native", `docs/design-native/`) govern the native surface; `DESIGN.md` remains
   the frozen authority for the product's behaviour and copy, and is never edited.

## Consequences

- `src/AdbView/NativeUi/` owns the shell: `NativeFlyoutWindow` (WndProc/DWM port of the
  WinForms flyout's window behaviour via `HwndSource`), `Themes/` (Fluent token dictionaries
  plus theme-neutral control chrome in `Themes/Controls.xaml`), `Shell/` (stable shell,
  pairing card, discovery-health card, view models), `Cards/`.
- Phase plan: I1 shell + device list, I2 pairing/QR/health cards, I3 tray + native
  self-sizing, I4 cutover and removal of `ui/` + the WebView host, I5 DPI matrix and polish.
- Follow-up notes: 0016 (discovery merge + QR query burst), 0017 (pairing slot release + QR
  presentation), 0018 (native shell metrics), 0019 (toast timing + visible-flyout
  suppression).
