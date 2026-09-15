# Phase 0 Research — Toolchain and Gates

**Date:** 2026-09-16  
**Phase:** 0 — Repository, gates, and guard rails  
**Status:** Decision record for initial repository pins

## Problem

Confirm the current supported toolchain and exact dependency pins before creating the greenfield repository skeleton. The Phase 0 plan specifically requires verification of the .NET 10 SDK patch, xUnit v3 / Microsoft Testing Platform behavior under `dotnet test`, Node LTS for Vite 8, and exact versions for every dependency in `ENGINEERING.md §15`.

## Sources checked

### .NET / testing

- .NET 10 download page: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- `dotnet test` with Microsoft Testing Platform: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test
- xUnit v3 Microsoft Testing Platform guide: https://xunit.net/docs/getting-started/v3/microsoft-testing-platform
- xUnit v3 4.0.0 release notes: https://xunit.net/releases/v3/4.0.0
- NuGet package pages for WebView2, Serilog, Serilog.Sinks.File, CsWin32, and xUnit

### Node / frontend

- Node release index: https://nodejs.org/en/about/previous-releases
- Node download page: https://nodejs.org/en/download
- Vite guide: https://vite.dev/guide/
- Vite 8 announcement: https://v8.vite.dev/blog/announcing-vite8
- Vitest guide: https://vitest.dev/guide/
- typescript-eslint dependency support: https://typescript-eslint.io/users/dependency-versions/
- npm package pages for the exact frontend packages listed below

## Findings

### .NET

- Current .NET 10 runtime patch: **10.0.12** (2026-09-08 security patch).
- Current .NET 10 SDK: **10.0.401**.
- Decision: pin SDK **10.0.401** in `global.json` with prereleases disabled and no SDK roll-forward for deterministic local/CI behavior.

### xUnit / Microsoft Testing Platform

- xUnit v3 **4.0.0** is current stable and defaults to Microsoft Testing Platform v2.
- .NET 10 can select Microsoft Testing Platform directly via `global.json`:

```json
{
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

- xUnit's own MTP documentation states that when relying solely on MTP, `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` can be removed.
- Because ADB View is a greenfield .NET 10-only repository, retaining VSTest compatibility adds dependencies without a requirement.
- Decision: use **`xunit.v3.mtp-v2` 4.0.0 only** for the test framework and omit `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk`.

### Node / npm

- Current Node Current is v26, but the current LTS line is **Node 24.21.0 (Krypton)**.
- Node 24.21.0 bundles **npm 11.19.0**.
- Decision: pin **Node 24.21.0 LTS + npm 11.19.0**, not Node Current.
- Vite 8 requires Node 20.19+ or 22.12+; Vitest 5 requires Node >=22.12. Node 24.21.0 satisfies both.

### TypeScript compatibility

- npm currently marks TypeScript **7.0.2** as latest.
- Current `typescript-eslint` **8.70.0** officially supports TypeScript `>=4.8.4 <6.1.0`.
- Therefore using `typescript@latest` would deliberately place the lint stack outside its supported range.
- Decision: pin **TypeScript 6.0.3** until `typescript-eslint` officially supports TypeScript 7. This is an intentional compatibility pin, not stale dependency drift.

### Testing Library peer dependency

- React Testing Library v16 requires `@testing-library/dom` as a peer dependency.
- The previous architecture list omitted it.
- Decision: add **`@testing-library/dom` 10.4.2** explicitly.

### React type declarations

- React packages do not ship the DefinitelyTyped declarations used by this TypeScript setup.
- Decision: explicitly add **`@types/react` 19.3.0** and **`@types/react-dom` 19.3.0**.

## Exact initial pins

### SDK/runtime tooling

| Tool | Pin |
|---|---:|
| .NET SDK | **10.0.401** |
| .NET runtime family | **10.0.12** |
| Node.js | **24.21.0 LTS** |
| npm | **11.19.0** |

### NuGet — app

| Package | Pin |
|---|---:|
| Microsoft.Web.WebView2 | **1.0.4191.47** |
| Serilog | **4.4.0** |
| Serilog.Sinks.File | **7.0.0** |
| Microsoft.Windows.CsWin32 | **0.3.333** |

`Microsoft.CodeAnalysis.NetAnalyzers` is supplied by the SDK and should not be added as a redundant package.

### NuGet — tests

| Package | Pin |
|---|---:|
| xunit.v3.mtp-v2 | **4.0.0** |

Intentionally omitted: `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`.

### npm — runtime

| Package | Pin |
|---|---:|
| react | **19.3.0** |
| react-dom | **19.3.0** |

### npm — dev

| Package | Pin |
|---|---:|
| @types/react | **19.3.0** |
| @types/react-dom | **19.3.0** |
| typescript | **6.0.3** |
| vite | **8.2.2** |
| @vitejs/plugin-react | **6.1.1** |
| tailwindcss | **4.3.3** |
| @tailwindcss/vite | **4.3.3** |
| vitest | **5.0.1** |
| @testing-library/react | **16.3.3** |
| @testing-library/dom | **10.4.2** |
| @testing-library/jest-dom | **7.0.1** |
| jsdom | **30.0.1** |
| @playwright/test | **1.63.0** |
| eslint | **10.10.0** |
| typescript-eslint | **8.70.0** |
| eslint-plugin-react-hooks | **7.1.1** |
| prettier | **3.9.6** |

## Chosen approach

1. Use .NET 10 SDK 10.0.401 with MTP selected in `global.json`.
2. Use xUnit 4 through `xunit.v3.mtp-v2`, with no VSTest compatibility packages.
3. Use Node 24 LTS, not Node Current.
4. Pin every direct npm dependency exactly and commit `package-lock.json`.
5. Pin TypeScript 6.0.3 until the chosen lint toolchain supports TypeScript 7.
6. Keep the dependency list minimal; do not add packages merely because a default Vite template includes them.

## Rejected approaches

### `typescript@latest`
Rejected because TypeScript 7.0.2 is outside the current official `typescript-eslint` supported range.

### Node 26 Current
Rejected because Node 24 is the current LTS line and satisfies all Vite/Vitest requirements with lower churn.

### VSTest compatibility packages
Rejected because this is a new .NET 10-only repository and native MTP is already the intended runner.

### Floating package ranges
Rejected. Direct dependencies use exact versions; `package-lock.json` pins the transitive npm graph and `Directory.Packages.props` centralizes NuGet versions.

## Compatibility / test implications

- `global.json` must include both SDK 10.0.401 and `test.runner = Microsoft.Testing.Platform`.
- CI must install the exact .NET SDK and Node version rather than relying only on whatever happens to be preinstalled on `windows-latest`.
- `package.json` should use exact dependency versions, set `packageManager` to `npm@11.19.0`, and record the intended Node version (`.node-version` recommended).
- The test project should reference `xunit.v3.mtp-v2` 4.0.0 and prove `dotnet test` succeeds through MTP on .NET 10.
- Add a gate that rejects `^`/`~` ranges in direct dependencies.
- Add a gate that rejects accidental `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio` references unless the architecture is deliberately revised.

## Validation still required during Phase 0

- Run `dotnet test` on the actual Windows development machine and CI with the selected MTP-only configuration.
- Confirm Visual Studio/VS Code tooling used by the developer can discover tests sufficiently for this project; command-line `dotnet test` remains authoritative.
- Run the complete frontend lint/type/test/build stack with TypeScript 6.0.3 and the exact package set above.
- Deliberately violate each source-boundary gate once and record the expected failure in the Phase 0 PR/commit notes; do not merge those deliberate violations.

## Phase 0 validation results (2026-09-16)

Executed on the Windows development machine; CI still pending (requires a push).

- `dotnet test` works through Microsoft Testing Platform v2 with `xunit.v3.mtp-v2` 4.0.0 only,
  selected via the `test.runner` key in `global.json`. No VSTest packages needed.
- **`xunit.v3.mtp-v2` generates its own entry point** (`XunitAutoGeneratedEntryPoint`). The test
  project must set `<GenerateTestingPlatformEntryPoint>false</GenerateTestingPlatformEntryPoint>`
  or the build fails with CS8892 (duplicate entry point).
- .NET 10 SDK `dotnet new sln` defaults to the new `.slnx` format; `--format sln` is required to
  produce the classic `ADBView.sln` the plan names.
- A `WinExe` is GUI-subsystem: PowerShell's call operator does **not** wait for it, so the publish
  smoke run must use `Start-Process -Wait` (otherwise the log assertion races the process).
- A framework-dependent smoke exe does not resolve a per-user SDK install (`%USERPROFILE%\.dotnet`)
  on its own; `scripts/verify.ps1` sets `DOTNET_ROOT` from the `dotnet` on PATH before running it.
- `npm` must be invoked as `npm.cmd` in repository scripts so they work under restricted PowerShell
  execution policies (`npm.ps1` is blocked; the `.cmd` shim is not).
- Production bundles legitimately embed three kinds of **inert, never-fetched** URLs: W3C namespace
  identifiers (`http://www.w3.org/2000/svg` etc., used by React's renderer), React error-code
  constants (`https://react.dev/errors/…`), and Tailwind's license comment (`https://tailwindcss.com`).
  The `ui/dist` no-absolute-URL boundary test allowlists exactly these three, each documented;
  anything else absolute fails the gate.
- `Directory.Packages.props` policy comments that *name* forbidden packages (`System.Net.Http`,
  `Microsoft.NET.Test.Sdk`) trip naive raw-text scans, so the corresponding boundary tests parse
  XML elements structurally instead of scanning text.
- Deliberate-violation pass: all ten boundary invariants plus format/build/lint/typecheck/vitest/
  prettier/toolchain-pin gates were each violated once and observed failing; all violations were
  reverted (record in the Phase 0 PR description).
