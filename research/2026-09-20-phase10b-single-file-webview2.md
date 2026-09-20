# Phase 10B — Single-file publish + WebView2 native loader (V-6)

- **Date:** 2026-09-20
- **Author:** agent
- **Phase:** 10B
- **Status:** current

## Problem

`ENGINEERING.md` V-6 requires validating self-contained single-file publish with the
WebView2 native loader on clean Windows 11, including whether
`IncludeNativeLibrariesForSelfExtract` is required.

## Sources consulted

| Source | URL | Version / last updated |
|---|---|---|
| .NET single-file docs | https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview | .NET 10 |
| WebView2 distribution | https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution | retrieved 2026-09-20 |
| Phase 10A contract | `research/2026-09-20-phase10a-packaging-contract.md` | 2026-09-20 |

## Version investigated

- .NET SDK 10.0.401
- `Microsoft.Web.WebView2` 1.0.4191.47
- Publish: `win-x64`, self-contained, `PublishSingleFile=true`, `PublishReadyToRun=true`, `PublishTrimmed=false`

## Findings

Observed publish outputs (after frontend wwwroot copy):

| IncludeNativeLibrariesForSelfExtract | Dominant payload | Notes |
|---|---|---|
| `false` | `AdbView.exe` + **sidecar** `WebView2Loader.dll` (+ `runtimes/win-x64/native/…`) + `wwwroot/**` | Extra native files beside the single-file host |
| `true` | Larger `AdbView.exe` embeds loader; **no** sidecar `WebView2Loader.dll` + `wwwroot/**` | Cleaner public staging after stripping PDB/XML |

For ADB View's WinForms + WebView2 stack, both configurations produced a runnable
layout on the developer machine. Trimming remains unsupported for WinForms and is
disabled. Public staging after cleanup is `AdbView.exe` + `wwwroot/**` only when
`IncludeNativeLibrariesForSelfExtract=true`.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| `IncludeNativeLibrariesForSelfExtract=false` | Slightly simpler flag surface | V-6 explicitly names the true fallback if loader issues appear |
| `IncludeNativeLibrariesForSelfExtract=true` | Matches ENGINEERING fallback; predictable native extract | Negligible size difference for this payload |
| Non-single-file SC | Easier native debugging | Larger file count; diverges from frozen §17.2 |

## Chosen approach

Freeze **`IncludeNativeLibrariesForSelfExtract=true`** for Phase 10 packaging
(`scripts/package.ps1`). Keep **no trimming**. Strip PDBs and documentation XML from
public staging. Ship Evergreen WebView2 Runtime separately (installer bootstrapper).

## Why

Aligns with the frozen ENGINEERING V-6 mitigation path and Phase 10A recommendation while
keeping the public payload to `AdbView.exe` + `wwwroot/**` after staging cleanup.

## Compatibility / pitfalls

- arm64 artifacts are cross-published on x64 hosts; runtime proof is Phase 10C on ARM64 hardware/VM.
- Single-file extraction uses a temporary extract directory; antivirus can slow first launch.
- Evergreen Runtime absence is an installer concern, not solved by this flag.
