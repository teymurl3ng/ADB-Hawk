# Phase 10A — Packaging research / frozen package contract

- **Date:** 2026-09-20
- **Phase:** 10A (research + plan only — no installer implementation)
- **Sources:** `PHASES.md` Phase 10, `ENGINEERING.md` §2.11 / §9 / §17, `AGENTS.md` R-09–R-14, `DESIGN.md` identity, current `scripts/package.ps1`, `Directory.Build.props`, `AdbView.csproj`, `AdbExecutableLocator`, `LogConfig`, `SingleInstance`, decisions 0004 / 0005
- **Status:** Frozen planning contract for 10B–10D. Does not implement installer code.

## Frozen Phase 10 scope (from repository)

From `PHASES.md` Phase 10:

- Reproducible builds, installer, portable zip, fresh-machine verification
- Files: `scripts/{build,package}.ps1`, `installer/AdbView.iss`, `docs/RELEASE.md`, `.github/workflows/release.yml`
- Tests: build-fails-without-`wwwroot`; no-absolute-URL assets; installer smoke (silent install → binary → optional Run key → clean uninstall)
- Acceptance: fresh Win11 VM, no .NET / Node / platform-tools → install without admin → tray + `ADB unavailable` → install platform-tools → self-heal → device + wireless pair → reboot with optional start-at-sign-in → clean uninstall; repeat arm64
- Out of scope: code signing purchase, Store, auto-update, MSIX

From `ENGINEERING.md` §17.3 / §2.11:

- Self-contained, `win-x64` and `win-arm64`, single-file, ReadyToRun, **no trimming**
- Inno Setup 6, per-user `%LOCALAPPDATA%\Programs\ADBView`, no admin
- Evergreen WebView2 check + official bootstrapper if absent
- Optional installer checkbox → `HKCU\...\Run` (installer-only; app never writes it)
- Portable versioned zip also published
- Version source: `Directory.Build.props` `<Version>`
- No auto-update in v1 (`docs/decisions` / §17.5)

## Measured publish inventory (2026-09-20, win-x64)

### Framework-dependent (`--self-contained false`, multi-file)

- **~5.4 MB / 18 files**
- Major: `AdbView.dll` (~2.5 MB), WebView2 managed + loader, `AdbView.exe`, `wwwroot` assets, Serilog, XML docs, PDB
- **Requires** installed .NET 10 Desktop Runtime — not acceptable for clean-machine Phase 10 acceptance

### Self-contained single-file R2R (`PublishSingleFile=true`, `PublishReadyToRun=true`, `PublishTrimmed=false`)

- **~130 MB / 8 files** (dominated by `AdbView.exe` ~129 MB)
- Also emits: `wwwroot/**`, WebView2 XML docs, `AdbView.pdb`
- **Recommendation for v1 ship:** self-contained single-file R2R, **exclude PDBs and `*.xml` docs from package staging**

### Classification for ship payload

| Item | Class |
|------|--------|
| `AdbView.exe` (SC single-file) | REQUIRED RUNTIME |
| `wwwroot/index.html` + hashed CSS/JS | REQUIRED RUNTIME |
| Embedded tray/app icons | REQUIRED RUNTIME (embedded) |
| WebView2Loader (inside single-file / native extract) | REQUIRED RUNTIME |
| Evergreen WebView2 Runtime (system) | REQUIRED PREREQUISITE (not bundled) |
| `adb.exe` / platform-tools | USER-SUPPLIED (never ship) |
| `*.pdb`, `*.xml` API docs | FORBIDDEN TO SHIP (public package) |
| tests, fixtures, research, docs, artifacts, node_modules, source | FORBIDDEN TO SHIP |

## ADB distribution

**Contract (Phase 4 / R-14):** user supplies platform-tools; ADB View never bundles, kills, or reconfigures adb.

Discovery precedence (`AdbExecutableLocator`) — do not change casually:

1. `ANDROID_SDK_ROOT\platform-tools\adb.exe`
2. `ANDROID_HOME\platform-tools\adb.exe`
3. PATH entries (empty PATH entries skipped — hostile CWD cannot win)
4. `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`
5. `%ProgramFiles%\Android\android-sdk\platform-tools\adb.exe`

**Recommendation:** Strategy A — user-supplied only. Bundling rejected (licensing/size/update ownership + contradicts R-14 / Phase 10 acceptance which explicitly installs platform-tools after first launch).

## WebView2 deployment

- NuGet: `Microsoft.Web.WebView2` **1.0.4191.47**
- Runtime: **Evergreen** (not fixed, not bundled)
- Missing runtime: tray + tooltip continue; balloon explains window cannot show; retry on next left-click / resume (`ENGINEERING.md` §9.7)
- User data: `%LOCALAPPDATA%\ADBView\WebView2`

**Recommendation:** detect Evergreen; offer/run official bootstrapper from installer if absent. Do not reopen Acrylic/composition (decision 0004 — opaque v1).

## .NET deployment

| Mode | Measured size (x64) | Clean machine |
|------|---------------------|---------------|
| Framework-dependent | ~5.4 MB app | Needs .NET 10 Desktop Runtime |
| Self-contained single-file R2R | ~130 MB | No .NET SDK/runtime install |

**Recommendation:** self-contained single-file R2R, no trimming (WinForms unsupported). Validate `IncludeNativeLibrariesForSelfExtract=true` on clean VMs (V-6). Single-file is already the `scripts/package.ps1` contract.

## Target architecture

- **v1:** `win-x64` and `win-arm64` (PHASES + ENGINEERING)
- Current `AdbView.csproj` has `<PlatformTarget>x64</PlatformTarget>` — 10B must publish both RIDs without casually dropping arm64 from the phase contract
- Do not add extra architectures

## Installer technology

| Option | Verdict for ADB View |
|--------|----------------------|
| **Inno Setup 6** | **Chosen** — already frozen in ENGINEERING/PHASES; per-user, no admin, optional Run key, bootstrapper hook, simple CI |
| MSIX | Rejected by Phase 10 out-of-scope; packager identity friction for unpackaged tray/Win32 |
| WiX/MSI | Heavier; typically admin/machine scope; overkill |
| NSIS | Viable but not the frozen choice |
| Portable ZIP only | Insufficient alone; ZIP remains a **second** artifact |

## Install scope

- Path: `%LOCALAPPDATA%\Programs\ADBView`
- Start Menu: **ADB View** → `AdbView.exe`
- Desktop shortcut: **no** (unless product later requires)
- Uninstall: Inno entry; remove files, Start Menu, optional Run value, WebView2 profile, logs (per ENGINEERING §17.3)

## Auto-start

- **In scope as installer-optional only** (Phase 10 acceptance + ENGINEERING)
- App must **not** self-register Run
- `--startup` remains tray-only launch semantic for the Run value / future explicit config

## Product identity (current gaps for 10B)

| Surface | Current | Target |
|---------|---------|--------|
| Product name | ADB View | ADB View |
| Exe | `AdbView.exe` | keep |
| AssemblyName | `AdbView` | keep |
| Version | `0.1.0` in `Directory.Build.props` | bump toward release in Phase 11; packaging uses this single source |
| FileDescription / ProductName / Company / Copyright | **not set in csproj** | set in 10B from Directory.Build.props / props |
| Icon | `assets/icons/aperture-link-idle.ico` | keep |
| Mutex | `Local\AdbView.SingleInstance` | keep (internal) |
| Pipe | `AdbView.Control.<sessionId>` | keep (internal) |
| Repo folder "ADB Hawk" | developer path only | must not appear in user-facing metadata |

## Versioning plan

- **Single source:** `Directory.Build.props` `<Version>`
- Propagate to: AssemblyVersion / FileVersion / InformationalVersion, Inno `AppVersion`, artifact names `AdbView-<version>-<rid>.exe` / `.zip`, SHA-256 list
- Do **not** tag `v1.0.0` in Phase 10
- Prepare path to `1.0.0` for Phase 11

## Logging

- Path: `%LOCALAPPDATA%\ADBView\logs\adbview-.log`
- Rolling daily, 4 MiB/file, retain 7, shared, unbuffered
- Independent of CWD / install dir writability
- No telemetry

## CWD independence

- Locator skips empty PATH entries (no CWD adb)
- wwwroot resolved relative to app base / published layout, not repo
- Second instance: mutex + named pipe toggle

## Package denylist (gate in package.ps1)

Fail packaging if staging contains: tests, fixtures, research, docs (except intentionally shipped none), artifacts/, diagnosis, logs, screenshots/videos, Playwright, node_modules, .git, source trees, FakeAdbServer, mutation artifacts, secrets, absolute developer paths, PDBs, source maps (default exclude).

## Frontend assets

- Production Vite build only (`scripts/build.ps1`)
- No dev server in Release (`--dev` compiled out)
- Virtual host mapping to local wwwroot
- Gate: `wwwroot/index.html` required; no absolute http(s) asset URLs; hashed assets present

## Clean-machine / install / upgrade / uninstall

Prefer **Windows Sandbox or fresh VM** with no VS / SDK / Node / repo / Cursor.

**Install:** Start Menu → one tray → flyout → opaque surface → no native caption → truthful no-ADB state → no console → no blank WebView.

**Upgrade N→N+1:** same Inno AppId; replace exe + wwwroot; remove stale hashed assets; one process; single-instance unchanged. No auto-updater.

**Running during upgrade:** Inno close-applications / prompt / retry; prefer graceful exit (StateEngine, trackers, tray, WebView, notifications). Avoid silent force-kill without cleanup.

**Uninstall:** remove program files, shortcuts, uninstall entry, Run value, WebView2 profile, logs. No other persisted user data exists.

## Signing / SmartScreen

- Phase 10 may test **unsigned** packages
- Unsigned/new binaries can trigger SmartScreen; installer tech does not create reputation
- Code signing is a **business / Phase 11** concern before public v1 — not purchased in 10A
- SHA-256 is integrity only, not trust

## Offline / online

- Self-contained app: offline after install **if** WebView2 Evergreen already present
- First install may need network for WebView2 bootstrapper when runtime missing
- Distinguish online bootstrapper path vs full offline (only claim offline after tested)

## Proposed `scripts/package.ps1` contract (10B)

Existing script already publishes SC zip per RID. Extend conceptually:

```
scripts/package.ps1 [-Runtime win-x64|win-arm64|all] [-SkipVerify]
  → verify expected repo/tool pins
  → build frontend + publish SC single-file both RIDs
  → stage without PDB/XML
  → denylist scan
  → ISCC installer/AdbView.iss
  → portable zips
  → SHA-256 for every artifact
  → write manifest (version, rid, file list, sizes)
```

Do not auto-publish GitHub Releases from normal CI; add `release.yml` workflow for package artifacts without skipping `verify`.

## Wave plan

| Wave | Scope | Stop |
|------|--------|------|
| **10A** | This research + frozen contract | Plan accepted; no installer code |
| **10B** | `installer/AdbView.iss`, package.ps1 hardening, identity metadata, denylist, SHA-256, RELEASE.md draft | Artifacts build locally |
| **10C** | Install/upgrade/uninstall + clean VM/Sandbox acceptance (x64; arm64 as available) | PHASES acceptance rows green |
| **10D** | CI package workflow, docs closure, completion record / `phase-10-complete` tag if PHASES style requires | Tag + green CI |

## Explicit non-goals

No product features; no native structural resize resurrection; no Acrylic rearchitecture; no updater; no telemetry; no service/driver; no new ADB commands; no WinAppSDK migration; no Phase 11 polish / signing purchase / 12 h soak.
