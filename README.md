# ADB View

ADB View is a small Windows 11 system-tray utility for observing, explaining, notifying, and pairing ADB connections. Its UI is a native WPF shell hosted by the native Windows process — no web content, no browser runtime (decision 0022).

## Repository status

**Phase 10 complete: packaging.** Self-contained x64/arm64 single-file publishes, per-user Inno installer, portable zip, manifest + SHA-256 sums, and machine-checked package contracts (`scripts/package.ps1`, `scripts/test-package.ps1`, `scripts/test-install-acceptance.ps1`). The v1 WebView2 frontend was removed after the native shell reached full parity (decision 0022): the UI is now only `src/AdbView/NativeUi`, and the gate is dotnet-only. Deferred Acrylic/typography visuals parked for Phase 11.

## Governing documents

Read these before changing the project:

1. `DESIGN.md` — frozen product and visual authority (integrity-checked by `docs/DESIGN.sha256`)
2. `ENGINEERING.md` — frozen greenfield engineering architecture
3. `PHASES.md` — ordered implementation plan
4. `AGENTS.md` — mandatory rules for coding agents

Supporting current research lives in `research/`. The complete Stitch design source/reference archive lives in `design/reference/stitch_adb_view_frozen_design.zip`.

## Prerequisites (pinned)

| Tool     | Pin      | Where         |
| -------- | -------- | ------------- |
| .NET SDK | 10.0.401 | `global.json` |

NuGet versions are pinned in `Directory.Packages.props`. Every direct dependency is exact — no ranges, no wildcards. The boundary tests fail the build if any pin drifts from `ENGINEERING.md §15`. There is no Node/npm toolchain (decision 0022).

## Build and verify

```powershell
# everything (toolchain pin, format, Release build, C# tests incl. boundaries, publish smoke):
.\scripts\verify.ps1

# pieces:
.\scripts\build.ps1        # Release solution build
.\scripts\test.ps1         # C# test suite (xUnit v3 / MTP), after a build
.\scripts\capture-native.ps1 -Fixture connected-usb   # screenshot the real shell with a fixture
.\scripts\package.ps1      # self-contained publish + installer + portable zip
```

`scripts/verify.ps1` is the gate (`AGENTS.md` R-12). It must pass before any phase is declared complete — and because its smoke run exits before the shell is created, launch the Release exe from a non-"smoke" path before claiming the UI works. CI runs the same gate on `windows-latest` (`.github/workflows/ci.yml`).

## Product boundary

**Observe → explain → notify → pair**

ADB View is not an ADB toolbox. Do not add shell, logcat, APK installation, file transfer, screen mirroring, reboot controls, port management, dashboards, analytics, or history unless the governing specifications are explicitly revised first.
