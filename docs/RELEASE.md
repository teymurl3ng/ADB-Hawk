# ADB View — Release packaging (internal)

Internal release-engineering notes for Phase 10. Not marketing copy.

## Prerequisites

- Windows 11 x64 development machine (arm64 publish is cross-compile; arm64 runtime proof is Phase 10C)
- Pinned .NET SDK from `global.json` (10.0.401)
- Pinned Node/npm from `.node-version` / `ui/package.json` (or `node_tool/`)
- **Inno Setup 6** with `ISCC.exe` on disk  
  Default search paths:  
  `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`  
  `%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe`  
  Or pass `-InnoCompiler <path>`  
  `scripts/package.ps1` does **not** install Inno Setup.
- Network access once per machine to download the Microsoft WebView2 Evergreen Bootstrapper into `installer/redist/` (Authenticode-validated; gitignored)

## Package command

From the repository root:

```powershell
.\scripts\package.ps1
```

Defaults:

- Version = `Directory.Build.props` `<Version>` (currently `0.1.0`)
- Both RIDs: `win-x64`, `win-arm64`
- Runs `scripts/verify.ps1` unless `-SkipVerify` (developer-only; do not use for release evidence)

Optional:

```powershell
.\scripts\package.ps1 -Rid win-x64
.\scripts\package.ps1 -Version 0.1.0   # must match Directory.Build.props or fail
.\scripts\package.ps1 -InnoCompiler "C:\Path\ISCC.exe"
.\scripts\package.ps1 -OutputDirectory artifacts\release
```

Validate artifacts:

```powershell
.\scripts\test-package.ps1
```

## Output artifacts

Under `artifacts/release/<version>/` (gitignored):

| Artifact | Purpose |
|---|---|
| `ADBView-<ver>-win-x64.zip` | Portable extract-and-run |
| `ADBView-<ver>-win-arm64.zip` | Portable extract-and-run |
| `ADBView-<ver>-win-x64-setup.exe` | Per-user Inno installer |
| `ADBView-<ver>-win-arm64-setup.exe` | Per-user Inno installer |
| `ADBView-<ver>-manifest.json` | Machine-readable package metadata |
| `ADBView-<ver>-SHA256SUMS.txt` | SHA-256 lines for each public artifact + manifest |

Staging (not redistributed as-is):

- `win-x64/publish`, `win-x64/package`
- `win-arm64/publish`, `win-arm64/package`

## Publish mode

- Self-contained
- Single-file
- ReadyToRun
- **Not** trimmed
- `IncludeNativeLibrariesForSelfExtract=true` (see `research/2026-09-20-phase10b-single-file-webview2.md`)

Public package contents after staging cleanup: `AdbView.exe` + `wwwroot/**` (plus only files proven required). No PDBs, no docs XML, no `adb.exe`.

## Checksums

`ADBView-<ver>-SHA256SUMS.txt` uses:

```text
<sha256>  <filename>
```

Integrity only — not a substitute for Authenticode signing.

## Architecture

- Separate artifacts per RID (no dual-arch mega-installer)
- PE machine of `AdbView.exe` must match RID (`x64` / `arm64`) or packaging fails

## WebView2 behavior

- Evergreen Runtime (not fixed, not bundled in the app package)
- Installer detects HKLM/HKCU EdgeUpdate client `{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` `pv`
- If missing: runs official Evergreen Bootstrapper (`/silent /install`) from staging `{tmp}`
- Bootstrap failure aborts setup (no “success” install without a renderable UI host)
- Uninstall does **not** remove the system Evergreen Runtime

## ADB

- **Not bundled**
- Discovery unchanged (`AdbExecutableLocator`)
- Fresh install without platform-tools is valid; UI shows existing ADB-unavailable truth
- Phase 10C installs platform-tools and proves recovery

## Installer scope

| Item | Value |
|---|---|
| Technology | Inno Setup 6 |
| AppId (frozen) | `{7C9E2A14-8B3F-4D61-9E5A-2F1C0B8D6A47}` |
| Privileges | `lowest` (no admin) |
| Path | `%LOCALAPPDATA%\Programs\ADBView` |
| Start Menu | `ADB View` → `AdbView.exe` (**no** `--startup`) |
| Desktop shortcut | none |
| Optional task | “Start ADB View when I sign in” (**unchecked**) → `HKCU\...\Run` with `--startup` |
| Startup modify | Reinstall/upgrade with startup unchecked clears any prior `HKCU\...\Run` value (no orphan) |
| Frontend upgrade | `[InstallDelete]` purges `{app}\wwwroot` so obsolete Vite-hashed assets do not accumulate |
| Running app | `AppMutex=Local\AdbView.SingleInstance`; `CloseApplications=no` (prompt/wait, no silent force-kill) |
| Uninstall removes | app files, Start Menu, uninstall entry, optional Run value, `%LOCALAPPDATA%\ADBView\WebView2`, `%LOCALAPPDATA%\ADBView\logs` |

## Portable usage

1. Extract ZIP
2. Run `AdbView.exe`
3. No registry / auto-start changes
4. Still requires Evergreen WebView2 Runtime on the machine
5. Still requires user-supplied platform-tools for ADB features

## Unsigned build warning

Phase 10 may produce **unsigned** EXE/installer artifacts. SmartScreen may warn on new or unsigned binaries. Installer technology does not create reputation. Code signing is a Phase 11 / business decision before public v1.

## Phase 10C status

Phase 10C package-lifecycle acceptance is **COMPLETE** on the development host (installer, portable, no-ADB, recovery, upgrade, AppMutex, wwwroot purge, uninstall, WebView2-present, denylist, dual-RID package construction).

## Phase 11 / pre-v1 deferred validation

Remain **NOT EXERCISED** (not PASS) until release hardening:

| ID | Item |
|---|---|
| A | Pristine Windows 11 x64 VM (no SDK/repo/dev tools; preferably no .NET 10 Desktop) — installer, portable, first launch, upgrade, uninstall |
| B | Windows 11 ARM64 **runtime** — install/launch/WebView2/tray/flyout/single-instance/uninstall (or remove ARM64 from public v1 support) |
| C | Genuinely WebView2-free machine — bootstrapper detect/install/continue-or-fail |
| D | Real reboot/sign-in auto-start with `--startup` (tray only, no flyout) |
| E | SmartScreen / Mark-of-the-Web on a real downloaded artifact |

## Version source

Only `Directory.Build.props` `<Version>`. Do not set `1.0.0` until Phase 11. Do not tag `v1.0.0` in Phase 10.
