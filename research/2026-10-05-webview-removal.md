# WebView2/React frontend removal: blast radius and sequencing

- **Date:** 2026-10-05
- **Author:** coding agent (owner-directed Phase I4 cutover, decision 0022)
- **Phase:** I4
- **Status:** current

## Problem

Decision 0022 removes the web frontend entirely. This note maps everything the removal
touches (the audit found ~60 files across src, tests, scripts, packaging, workflows, docs)
and fixes the order of operations so the tree is never broken between gated commits.

## Sources consulted

| Source | URL | Version |
|---|---|---|
| WebView2 distribution model (evergreen runtime, fixed version) | https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution | current docs |
| Project parity audit (this session, agent report) | in-session | 2026-10-05 |
| Project blast-radius audit (this session, agent report) | in-session | 2026-10-05 |
| ENGINEERING.md §15 dependency authority | ENGINEERING.md | 2026-10-04 |

## Version investigated

app main @ `fc05760`; WebView2 SDK 1.0.4191.47 (pinned); Node 24.21.0 (vendored `node_tool/`);
Windows 11 26100.

## Findings

- The native path uses `BridgeCoordinator` only as a relay; three behaviours must be absorbed
  by AppHost when it dies: the direct `ApplySnapshot` call, the initial snapshot seed in the
  ctor (previously `UpdateSnapshot` + `MarkReady`), and window pin reflection
  (`UpdateSnapshot` → `_host.SetPinned`) which keeps TopMost/light-dismiss/metadata in step
  after the reducer accepts a `SetPinnedEvent`. Missing the pin absorption leaves the window
  unpinned while the icon claims otherwise; missing the seed reproduces the decision-0015
  "flyout renders nothing until the next event" bug.
- `FlyoutWindow` (WinForms) exists only to host the WebView; it dies with it. WinForms itself
  stays (NotifyIcon, PowerAndSession, interop `Screen`, `Graphics` DPI fallbacks).
- The packaging contract is compiler-coupled: `PackageCommon.Assert-InnoScriptContract`
  demands `{app}\wwwroot` in the installer's `[InstallDelete]`; `BoundaryTests`
  `NuGetPinsMatchEngineeringAuthority` hard-pins the WebView2 package and fails on extra rows.
  Each such pair must change in one commit.
- `verify.ps1`'s contract-generation step WRITES into `ui/src/contract` — it must be removed
  before (or with) the `ui/` deletion.
- The web's Esc-to-dismiss, per-state pill labels, spinner, and success dwell were
  web-only behaviours; all four were ported to native in `fc05760` BEFORE this removal, so
  deletion is feature-neutral (audited, not assumed).
- `FixtureSnapshotProvider` is C#-side and used by `StateReducerTests`,
  `FixtureSnapshotProviderTests`, and the `--test-fixture=` startup path; it survives (the
  folder keeps its name; relocating it is cosmetic churn with no benefit).

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Full removal, native-only (chosen) | One renderer; deletes the two-renderer bug class; big dependency loss | Large one-time diff; loses the web fixture browser (dev-only) |
| Keep web as compiled-in fallback | Reversibility | Keeps both toolchains alive forever; the fallback has no bundle after `ui/` deletion anyway |
| Keep BridgeCoordinator as internal transport | Smaller AppHost diff | Keeps a JSON envelope + handshake for a same-process call — dead abstraction |
| Relocate FixtureSnapshotProvider out of `Bridge/` | The Bridge folder could vanish | Pure churn; two `using` updates; folder of one file is honest |

## Chosen approach (gated increments, each green + committed)

0. Parity fixes land FIRST (done: `fc05760`), so deletion is feature-neutral.
1. Flip the default UI to native (`--ui=native` becomes accept-and-ignore).
2. Delete `ui/`, the Node toolchain, web-only scripts, the web gate steps, `wwwroot`, and
   amend the web-only boundary tests + README/CI in the same change.
3. Delete `WebViewHost` + the Bridge JSON layer; rewire AppHost (the three absorptions);
   delete/amend the coupled tests; drop the WebView2 package + pin + `SafeRevisionConverter`.
4. Delete the WinForms shell path (`FlyoutWindow`, `FormsTimerTickSource`, web branches of
   Run/show/hide/toggle/theme).
5. Packaging: installer + PackageCommon + package.ps1 + acceptance tests, one commit.
6. Docs: AGENTS rules, ENGINEERING sections, README/RELEASE remainders.

## Why

The order exists to satisfy the coupled-pair constraints above (interface↔mocks,
package↔pin-test, iss↔PackageCommon, gate-step↔folder) while keeping every intermediate
commit buildable and green. R-10 requires this note because the removal changes the tray
host stack, packaging, and the runtime dependency set.
