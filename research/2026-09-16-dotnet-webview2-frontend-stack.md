# Runtime, WebView2 and frontend stack selection

- **Date:** 2026-09-16
- **Phase:** pre-Phase 0 (architecture); refreshed in Phases 0, 2, 3
- **Status:** current

## Problem

Choose the .NET version, the WebView2 SDK, the interop mechanism, the frontend toolchain, the test
frameworks and the packaging approach for a tiny always-running tray utility that must keep working
untouched for years, with the smallest dependency set that survives real Windows.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| .NET and .NET Core official support policy | https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core | 2026-08 |
| Announcing .NET 10 | https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/ | .NET 10 GA, Nov 2025 |
| `Microsoft.Web.WebView2` package page | https://www.nuget.org/packages/microsoft.web.webview2 | 1.0.4191.47, 2026-08-28 |
| WebView2 SDK / runtime release notes | https://learn.microsoft.com/en-us/microsoft-edge/webview2/release-notes/ | 4-week SDK cadence |
| `Microsoft.Windows.CsWin32` package page | https://www.nuget.org/packages/Microsoft.Windows.CsWin32 | 0.3.333, 2026-09-03 |
| Vite 8.0 announcement | https://vite.dev/blog/announcing-vite8 | 2026-03-12, Node 20.19+/22.12+ |
| Tailwind CSS releases / Vite 8 support | https://github.com/tailwindlabs/tailwindcss/releases | v4.3.2 (2026-06-29); Vite 8 support from 4.2.2 |
| xUnit.net v3 4.0.0 release notes | https://xunit.net/releases/v3/4.0.0 | 2026-08-14, MTP v2 |
| Playwright releases (.NET + JS) | https://github.com/microsoft/playwright/releases | 1.6x, 2026 |

## Version investigated

.NET 10.0.12 (LTS, released 2026-09-08, support through 2028-11-14); .NET 8/9 both end of support 2026-11-10; .NET 11 in
preview (STS, ships Nov 2026). WebView2 SDK 1.0.4191.47 with Evergreen runtime. CsWin32 0.3.333.
Vite 8, Tailwind 4.3.x, React 19, xUnit v3 4.0.0, Playwright 1.6x.

## Findings

- .NET 10 is the only sensible target: .NET 8 and .NET 9 both die in November 2026, and .NET 11 is
  an unsupported preview today and only a 2-year STS release when it ships.
- The WebView2 SDK is on a predictable cadence with an Evergreen runtime already present on
  Windows 11; the WinForms control is the ordinary hosting path and supports a transparent
  background, virtual-host mapping for local assets, and process-failure events.
- CsWin32 is a build-time source generator (`PrivateAssets=all`) — correct signatures from official
  Win32 metadata with nothing shipped at runtime, and its allow-list file doubles as an audit of
  every native call the app can make.
- WinForms `NotifyIcon` handles the `TaskbarCreated` broadcast internally, so Explorer-restart
  recovery comes for free rather than being hand-rolled.
- Vite 8 requires Node 20.19+/22.12+; Tailwind's Vite plugin supports Vite 8 from v4.2.2 (current
  stable line 4.3.x). Tailwind has no LTS programme, so versions must be pinned and treated as a
  visual-regression event on upgrade.
- xUnit v3 4.0.0 defaults to Microsoft Testing Platform v2 and drops MTP v1; this affects
  `dotnet test` invocation on the .NET 10 SDK and must be set up once in Phase 0.
- Playwright supports `deviceScaleFactor`, `emulateMedia` (`colorScheme`, `forcedColors`,
  `prefers-reduced-motion`) and `toHaveScreenshot`, which covers the whole DPI/theme matrix the
  design requires.
- Trimming is not supported for WinForms, so self-contained publishing stays untrimmed
  (ReadyToRun + single-file instead).

## Chosen approach

`net10.0-windows` WinForms-as-plumbing host + `Microsoft.Web.WebView2` 1.0.4191.47 +
`Microsoft.Windows.CsWin32` 0.3.333 (build-time) + Serilog/Serilog.Sinks.File. Frontend: React 19 +
TypeScript + Vite 8 + Tailwind 4.3.x, runtime npm dependencies limited to `react` and `react-dom`.
Tests: xUnit v3 4.0.0 with hand-written fakes; Vitest + Testing Library; Playwright for visual
regression at 100/125/150/175/200 %. Packaging: self-contained single-file ReadyToRun (untrimmed)
for win-x64 and win-arm64, per-user Inno Setup installer, no auto-update in v1.

## Why

Longest support runway, native evergreen web host, zero-footprint interop, and a frontend stack
that coding agents handle reliably. Every element is current, supported, and boring; the dependency
count stays at four runtime packages plus two build-time ones.

## Rejected approaches

- **.NET 8/9** — end of support in November 2026.
- **.NET 11 preview** — unsupported today, shorter support window later.
- **WinUI 3 / Windows App SDK** — large dependency and runtime bootstrapper (or ~40 MB self-contained)
  for a 5 MB utility, awkward for a frameless tray flyout, and explicitly not the approved design
  technology.
- **WPF** — heavier than needed once all UI is web.
- **Raw Win32 host with no WinForms** — re-implements the message loop, tray registration and
  Explorer-restart handling for no benefit.
- **Fixed-version WebView2 runtime** — +180 MB and manual security patching.
- **Preact / Lit / vanilla TS** — smaller, but bundle size is irrelevant for local assets and React
  is the most agent-reliable option.
- **Framer Motion, icon packages, state libraries, routers, date libraries** — each adds surface for
  behaviour the frozen design does not ask for.
- **FluentAssertions** — v8 licensing change; plain `Assert` is sufficient.
- **MSIX / WiX / Velopack** — identity friction, admin orientation, or an update system that v1
  deliberately omits.

## Compatibility concerns

Windows 11 22621+ for the DWM backdrop attributes (see the Windows host note); Windows 10 would fall
back to the opaque token set and is not a v1 target. Evergreen WebView2 updates can shift rendering →
baselines record the runtime version. arm64 needs its own publish and install test.

## Testing implications

Phase 0 must prove the whole gate (format, analyzers, both test suites, boundaries) on a clean clone
and on CI (`windows-latest`). Visual baselines must be regenerated only via an explicit, reviewed
command that records the WebView2 runtime version.

## Open items

V-6 (single-file self-contained + WebView2 loader + Segoe UI Variable on clean x64/arm64 SKUs),
V-8 (`0fr→1fr`, `@starting-style`, `transition-behavior` in the shipped runtime at 125–200 %).
