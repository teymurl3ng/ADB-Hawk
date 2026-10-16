# Isolated WinUI shell feasibility probe

Nonshipping M02 project, outside `ADBView.sln`, with synthetic data and no reference to the production executable or backend. It does not acquire the production mutex, access ADB, register an installed App SDK runtime or write preferences. Close its window to exit.

Foundation: Microsoft's Windowing C# sample at commit `f93310028e8f904ee6b3fb133d413e13f4f859f4`, adapted for the repository's .NET 10 and exact Windows App SDK 2.5.1 pin. See `research/2026-10-15-winui-shell-rebuild.md`. No full-shell feasibility claim follows from merely compiling this first API/startup probe.

Run commands from the repository root using the pinned SDK executable:

```powershell
& C:\Users\Teymur\.dotnet\dotnet.exe build .\prototypes\WinUiShellProbe\WinUiShellProbe.csproj -c Release
& C:\Users\Teymur\.dotnet\dotnet.exe publish .\prototypes\WinUiShellProbe\WinUiShellProbe.csproj -c Release -o .\artifacts\winui-rebuild\probe-publish
```

For the UI-thread startup assertion, launch the published executable with `--verify=<absolute-result-path>` and wait for its process exit. `--force-failure` must produce a nonzero result and `Passed=false`. These initial checks cover XAML startup and the selected stable API only; the rest of M02 remains pending.

Repeatable checks:

```powershell
& .\prototypes\WinUiShellProbe\test-probe.ps1 -Publish
& .\prototypes\WinUiShellProbe\test-margin-input.ps1 -Stationary
& .\prototypes\WinUiShellProbe\test-native-input.ps1
& .\prototypes\WinUiShellProbe\test-workload.ps1
```

`test-margin-input.ps1` without Stationary tests the rejected transparent envelope; `-Acrylic` also adds bounded material. Both currently fail after their positive control succeeds. These tests temporarily show a synthetic underlay, send a guarded click only to the two owned test processes, restore the cursor and close both processes. They are not unattended background product behavior.

Default candidate: tightly bounded stationary opaque window, with shared platform motion on incoming content only. `--surface` retains the nonshipping transparent/moving experiment. `--acrylic` tests the bounded material API; `--hold-ms=<0..60000>` with a verification result path pauses after a ready-file signal for external measurement. `--native` enables the isolated tray, pin and bubble; `--pinned` is a synthetic capture option. No real preference is written.

The workload targets 100 snapshots/s for ten seconds, highest-revision delivery and 100 navigations using three reusable pages; its UI-thread times are not frame measurements. The approved **200 MiB complete-app** ceiling is enforced by the driver. Visible repeats have sampled peaks around 194–202 MiB before the backend; the latest fits narrowly, but integrated memory and repeatability remain unverified. See `docs/WINUI-PROTOTYPE-RESULTS.md`.

`test-workload.ps1 -CapturePresentation` attempts a bounded process-specific PresentMon capture. `-ElevateProfiler` additionally requests Windows UAC for the verified portable profiler; **do not use it without explicit go-ahead after the previous request was canceled**. No installation or system configuration is needed. A trace must be analyzed before claiming presentation quality. M02 and production migration remain gated.

`capture-probe.ps1` wraps the existing `scripts/capture-native.ps1` in memory with an isolated executable/title/output and DPI context; run it with Windows PowerShell 5.1. Never run the production capture helper unchanged against this prototype: its fixture process can reach the live production instance.
