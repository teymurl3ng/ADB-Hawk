# Remove the WebView2/React frontend; the native WPF shell is the only UI

- **Date:** 2026-10-05
- **Supersedes:** the "Primary UI" statement of DESIGN.md §0 (amended in this change with an
  explicit authority action + `docs/DESIGN.sha256` re-hash); historically supersedes the web
  sizing/pinning decisions 0005/0012/0013 (whose mechanisms were web-only).
- **Companion research:** research/2026-10-05-webview-removal.md
- **Owner approval:** requested and granted in the same session ("plan, scope, and execute").

## Problem

Since decision 0015 the product has had a complete native WPF shell behind `--ui=native`. By
2026-10-05 the native shell reached and exceeded feature parity (pairing cards, QR, discovery
health, status bubble, Esc dismiss, self-sizing, themed chrome), while the web path still
carried: a Node/npm toolchain, a WebView2 runtime dependency, a JSON bridge layer, four
Playwright/Vitest suites, and a dual-path AppHost. Every bug class the owner reported in this
project's history traced to the two-renderer runtime.

A full parity audit (surface-by-surface, action-by-action) found exactly four genuine gaps,
all ported to native in the commit preceding this one: state-pill wording now uses the
backend's `DeviceView.StateLabel` (the severity-derived text was wrong for offline/recovery/
bootloader), Esc-to-dismiss, the pairing progress spinner rotation, and the 750 ms success
dwell. Deferred as recorded follow-ups: pinned header drag (I3), forced-colors/high-contrast
tokens, a focus-ring/aria pass, the code-slot cursor pulse.

## Decision

1. **Native is the only UI.** `ui/`, the Node toolchain (`node_tool/`, `.node-version`,
   root `node_modules/`), and every web-only script, gate step, workflow step, and test suite
   are deleted.
2. **WebViewHost and the Bridge JSON layer are deleted** (`BridgeCoordinator`,
   `BridgeSerializer`, `WebActions`, `IBridgeHost`, `BridgeProtocol`, `ContentSizePhase`;
   `FixtureSnapshotProvider` stays — C# tests and `--test-fixture=` use it). AppHost absorbs
   the three behaviours the native path actually consumed: the direct `ApplySnapshot` call,
   the initial snapshot seed in the ctor, and pin reflection with its existing dedup. The
   `SafeRevisionConverter` JSON attribute on `AppSnapshot.Revision` is removed with its
   serializer.
3. **The WinForms flyout host (`FlyoutWindow`) is deleted** — it existed only to host the
   WebView. WinForms itself stays (tray, power/session, interop), per DESIGN.md §0 as amended.
4. **Packaging loses WebView2 and wwwroot**: the installer's WebView2 runtime detection/
   bootstrap, the `wwwroot` install/delete contract, and their packaging assertions.
5. **The gate becomes**: dotnet SDK pin → format → Release build (warnings-as-errors) → full
   C# suite (unit + boundary + native composition) → Release publish smoke. All Node/Playwright
   steps are removed.
6. **DESIGN.md amendment** (the only edit): §0 "Primary UI" now names the native WPF shell.
   The remaining historical web-architecture lines in DESIGN.md (Acrylic-over-WebView2
   rationale, WebView scrolling, the retired web sizing sections) are left untouched — they
   describe superseded history and carry no implementation authority after this note.

## Non-changes

- The backend, StateEngine, reducers, coordinators, Vocabulary, notifications, and tray are
  untouched except where they referenced the deleted layers.
- `--ui=native` remains an accepted no-op flag (stale shortcuts/logs keep working).
- `--test-fixture=` keeps working natively (AppHost seeds from `FixtureSnapshotProvider`); only
  the web's in-app fixture browser dies with the web.
- Fixture-mode C# tests are unaffected.

## Acceptance

- Gate green end to end with no Node/npm/Playwright steps.
- Launch-verified: native flyout, bubble, tray, pairing, fixtures (`--test-fixture=`).
- Boundary tests retain every "no WebView2 / no Bridge" token ban as re-introduction guards.
- R-09 paperwork: the WebView2 row leaves `ENGINEERING.md` §15 and the package pin test in the
  same change.
