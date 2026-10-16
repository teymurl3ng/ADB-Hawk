# Isolated WinUI candidate and headed verification

- **Date:** 2026-10-16
- **Author:** Codex
- **Phase:** M04, decision 0029
- **Status:** current

## Problem

Establish a real compiled-XAML test path against Core without mixing WPF build tasks, starting ADB, or forwarding fixture launches to the installed application. M02 selected stationary opaque fallback B; this package establishes the test foundation, not M05/M06 feature parity.

## Sources consulted

Official sources accessed 2026-10-16; generic versioned API pages are checked against the exact restored stable package.

| Source | URL | Version / scope |
|---|---|---|
| Application.Start | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.start?view=windows-app-sdk-2.0 | WinUI Application entry point |
| FocusManager.GetFocusedElement | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.input.focusmanager.getfocusedelement?view=windows-app-sdk-2.0 | XamlRoot-scoped focus |
| ProcessStartInfo.ArgumentList | https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-10.0 | .NET 10, separate argument escaping |
| Self-contained deployment | https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps | Existing M02 tested settings; no framework bootstrap added |
| Test Windows App SDK applications | https://learn.microsoft.com/en-us/windows/apps/develop/testing/ | Existing M02 research: XAML tests need the application UI thread |

## Version investigated

.NET SDK 10.0.401; Microsoft.WindowsAppSDK 2.5.1; CsWin32 0.3.333; net10.0-windows10.0.26100.0, minimum Windows 11 22000, x64; host build 26100. All existing Core dependencies and central pins retained. No new package version.

## Findings

Application.Start owns the WinUI pump. Focus checks use the actual loaded XamlRoot. ArgumentList escapes separate arguments; a result path with spaces must survive unchanged. Documentation does not prove this application's fixture isolation or failure propagation: those require executable tests. The M02 self-contained startup sequence is the source for candidate initialization; do not add a dynamic-dependency bootstrap to it.

Observed on the exact compiler: `x:ClassModifier` is unsupported (WMC0011). The generated XamlTypeInfo supplies a public partial Application; using that generated class directly avoids conflicting accessibility and needs no analyzer suppression. The window code-behind remains internal. The empty/health ScrollViewer must explicitly enable its native tab stop for keyboard focus. Actual visible/foreground HWND and XamlRoot focus checks then pass. These are application observations, not universal focus claims. No new API was needed beyond M02's GetDpiForWindow/GetForegroundWindow/IsWindowVisible foundation.

Negative-gate observation: a temporary failed healthy-empty assertion produced result Passed=false and exit 1; the complete gate rejected it after xUnit passed. Restoring the source through Copy-Item preserved its old timestamp, so an incremental build retained the negative binary. An explicit Rebuild removed that test mutation; the positive eight-fixture suite then passed. Source hashes match the pre-negative backup. Negative-test binaries are never acceptance evidence.

Formatter observation: its design-time WinUI loader requested the Core Debug implementation assembly even when Release builds existed. Diagnostic formatting still included the candidate's source files, but reported that missing reference. The gate now builds Core Debug metadata before format, retaining full Release builds afterward. This is a build/test integration fix, not a dependency or product change.

## Alternatives considered

| Option | Pros | Cons |
|---|---|---|
| Separate candidate referencing Core | Independent XAML pipeline and process identity | Temporary project until M10 |
| Add WinUI tasks to WPF project | Fewer projects | Mixes incompatible UI compilation, rejected by runbook |
| Test only projected C# objects | Fast | Does not execute XAML/focus/dispatcher, insufficient |

## Chosen approach

Create the allowed temporary candidate, reuse M02 initialization and stationary opaque foundation, and run assertions after loaded XAML on its dispatcher. Parse strict fixture/verification options before any instance handling. Fixture runs own a GUID-suffixed candidate mutex and never instantiate BackendRuntime, preferences, startup registration or production IPC/tray. Reserve a distinct candidate tray identity for M08; do not port tray integration early. The PowerShell 7 runner receives an explicit executable, fresh result paths, process identity, timeout, JSON assertions and exit code. Cleanup only its own Process object. Missing desktop/result, crash, timeout, or failed assertions fail the headed gate.

## Why / rejected approaches

Preserves the production WPF checkpoint and installed process while providing a truthful harness. No mock dispatcher, missing-desktop success, process-name cleanup, real credentials, installer edits, custom motion, or M05/M06 full behavior in M04.

## Compatibility concerns

Interactive Windows desktop is required and reported separately from headless smoke. SDK-equipped x64 launch is not clean-machine/ARM64 deployment proof. Existing WPF assertions remain until each equivalent replacement is implemented and verified; partial projections do not close an entire migration-map row.

## Testing implications

Exercise empty/list/details/pairing, actual focus and UI-thread access, Core vocabulary projection, unique identity, malformed options, result correlation and deliberate assertion failure propagation. Run the full gate including the candidate headed check; retain all baseline xUnit assertions. Capture the new candidate appearance explicitly without claiming production cutover.

## Open items

M05 keyed reconciliation/geometry policies; M06 pairing action parity; M07 motion; M08 native integration; M09 hardware/accessibility/performance; M11 deployment; M12 device/soak remain later packages.
