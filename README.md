# ADB View

ADB View is a small Windows 11 system-tray utility for observing, explaining, notifying, and pairing ADB connections.

## Repository status

**Phase 2 complete: frozen UI implemented from fixtures in WebView2 with native shell integration and autonomous audit suite.** All 16 frozen design fixtures rendered, themed (dark/light/forced-colors), accessible, and interaction-tested; native Acrylic backdrop, taskbar placement, light dismiss, and pinned drag verified. The next step is Phase 3 (The bridge) from `PHASES.md`.

## Governing documents

Read these before changing the project:

1. `DESIGN.md` — frozen product and visual authority (integrity-checked by `docs/DESIGN.sha256`)
2. `ENGINEERING.md` — frozen greenfield engineering architecture
3. `PHASES.md` — ordered implementation plan
4. `AGENTS.md` — mandatory rules for coding agents

Supporting current research lives in `research/`. The complete Stitch design source/reference archive lives in `design/reference/stitch_adb_view_frozen_design.zip`.

## Prerequisites (pinned)

| Tool | Pin | Where |
|---|---|---|
| .NET SDK | 10.0.401 | `global.json` |
| Node.js | 24.21.0 LTS | `.node-version` |
| npm | 11.19.0 (bundled with Node 24.21.0) | `ui/package.json` (`packageManager`) |

NuGet versions are pinned in `Directory.Packages.props`; npm versions in `ui/package.json` + `ui/package-lock.json`. Every direct dependency is exact — no ranges, no wildcards. The boundary tests fail the build if any pin drifts from `ENGINEERING.md §15`.

## Build and verify

```powershell
# everything (format, build, both test suites, boundaries, publish smoke):
.\scripts\verify.ps1

# pieces:
.\scripts\build.ps1        # ui bundle -> wwwroot -> Release solution build
.\scripts\test.ps1         # C# (xUnit v3 / MTP) + Vitest, after a build
.\scripts\package.ps1      # self-contained publish + portable zip (Phase 10 completes this)
```

`scripts/verify.ps1` is the gate (`AGENTS.md` R-12). It must pass before any phase is declared complete. CI runs the same gate on `windows-latest` (`.github/workflows/ci.yml`).

Note: `npm` is invoked as `npm.cmd` in the scripts so they work under restricted PowerShell execution policies.

## Product boundary

**Observe → explain → notify → pair**

ADB View is not an ADB toolbox. Do not add shell, logcat, APK installation, file transfer, screen mirroring, reboot controls, port management, dashboards, analytics, or history unless the governing specifications are explicitly revised first.
