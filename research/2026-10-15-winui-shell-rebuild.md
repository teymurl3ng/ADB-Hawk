# Stable WinUI shell and motion architecture

- **Date:** 2026-10-15
- **Author:** Codex
- **Phase:** Proposed shell migration track; existing release acceptance remains open.
- **Status:** current; planning research, no WinUI prototype executed.

## Problem

The user requests a research-based implementation plan that another model can execute with little architectural improvisation. The preceding WPF audit found competing window/layout animation mechanisms and reproducible reduced-motion failures. The user subsequently allowed a fresh interaction and technology design. The question is whether a simpler, stable-size WinUI shell is a sound replacement for this small Windows tray utility, and what must be proven before migrating.

## Sources consulted

All sources below were accessed on 2026-10-15. Versioned API documentation can still carry generic prerelease warnings; successful compilation against the selected stable package is required before treating a sample as usable. Source IDs are referenced by the runbook.

| ID | Official source | Version / scope investigated |
| --- | --- | --- |
| S01 | [Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels) | Stable 2.5.1, released 2026-09-16; page updated 2026-09-29 |
| S02 | [2.0 family release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0) | Includes stable 2.5.1 and the newer semantic version scheme |
| S03 | [Microsoft.WindowsAppSDK package](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1) | Exact NuGet pin 2.5.1; includes component dependencies |
| S04 | [Motion in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion) | Windows motion roles, durations and curves |
| S05 | [Optimize animations, media and images](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-animations-and-media) | WinUI independent versus dependent animations |
| S06 | [XAML and Composition interoperability](https://learn.microsoft.com/en-us/windows/apps/develop/composition/xaml-comp-interop) | Microsoft.UI.Xaml / Microsoft.UI.Composition integration |
| S07 | [System backdrops](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops) | Window-level backdrop and SystemBackdropElement, requiring SDK 2.0+ |
| S08 | [SystemBackdropElement API](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.systembackdropelement?view=windows-app-sdk-2.0) | Microsoft.UI.Xaml.Controls, not Controls.Primitives |
| S09 | [WinUI 3 Gallery 2.9 announcement](https://devblogs.microsoft.com/ifdef-windows/announcing-winui-3-gallery-2-9/) | Official examples of bounded material and transparent windows |
| S10 | [Self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) | Updated 2026-10-02; .NET and Windows App SDK deployment |
| S11 | [AppWindow.Show](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.show?view=windows-app-sdk-2.0) | Show(bool activateWindow) |
| S12 | [AppWindow.IsShownInSwitchers](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.isshowninswitchers?view=windows-app-sdk-2.0) | Taskbar / Alt-Tab visibility |
| S13 | [Notification area](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area) | Shell_NotifyIcon, icon callbacks and Explorer recreation |
| S14 | [Test Windows App SDK applications](https://learn.microsoft.com/en-us/windows/apps/develop/testing/) | Non-XAML unit tests versus tests inside a XAML application UI thread |
| S15 | [UISettings.AnimationsEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.animationsenabled?view=winrt-26100) and [change event](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.animationsenabledchanged?view=winrt-26100) | Windows SDK 26100 API view; event introduced in Windows 10 2004 |
| S16 | [Composition animations](https://learn.microsoft.com/en-us/windows/apps/develop/composition/composition-animation) | Platform animation facilities |

## Version investigated

- Existing repository: .NET SDK **10.0.401**, pinned with roll-forward disabled; Windows Desktop runtime **10.0.12** recorded in the preceding audit. Do not upgrade these during migration by default.
- Proposed new direct UI dependency: **Microsoft.WindowsAppSDK 2.5.1**, stable. The NuGet version is exactly 2.5.1, not an invented date-suffixed version. S02/S03 confirm the numbering change.
- Existing pinned packages: QRCoder 1.8.0, Serilog 4.4.0, Serilog.Sinks.File 7.0.0, Microsoft.Windows.CsWin32 0.3.333, xunit.v3.mtp-v2 4.0.0.
- Proposed UI target: `net10.0-windows10.0.26100.0`, minimum Windows 11 `10.0.22000.0`; x64 and ARM64 outputs. An API's presence in the compile SDK does not establish availability on the minimum OS.
- Observed host OS build: **26100**. Mixed-DPI, ARM64 and clean-machine runtime behavior have not been verified for the new shell.
- Any additional Windows SDK build-tools package must be resolved and pinned from a working official template in M02. This research has not tested a build-tools version or compiled WinUI on this machine. Do not invent a pin or substitute a preview SDK.
- ADB integrations are retained. This note does not claim new validation of platform-tools, phones, pairing or wireless reliability.

## Findings

### Documented facts

1. Stable 2.5.1 is available; prerelease versions exist separately. The top-level package depends on multiple component packages, including AI/ML components. Restoring it is not proof of an acceptable published footprint. Record the resolved graph and actual publish inventory. [S01–S03]
2. Windows guidance distinguishes entrance, exit, movement and feedback. It supplies examples such as 83 ms feedback and 167 ms transitions; it does not mandate a universal duration for every action or prescribe this app's dimensions. [S04]
3. Opacity and suitable render transforms can run independently of the UI thread. Layout-affecting animations and per-frame application rendering callbacks can remain UI-thread dependent. A fast timer is not a compositor. [S05/S06/S16]
4. `Window.SystemBackdrop` covers the window. `SystemBackdropElement` permits bounded material inside the XAML tree. Acrylic is suitable for transient surfaces. This supports investigating a material-backed moving panel, but does not prove correct transparency, shadow, hit testing or presentation cadence in this app. [S07–S09]
5. Self-contained Windows App SDK deployment and self-contained .NET deployment are separate settings. WinUI publishing does not yield a truly standalone single EXE: native dependencies remain beside it. An unpackaged folder, zipped or installed, is the relevant distribution shape. [S10]
6. AppWindow exposes activation choice on Show and visibility in switchers. These APIs do not, by themselves, specify complete tray light-dismiss behavior or prevent the bubble activating on a later mouse click. [S11/S12]
7. A native notification icon needs lifecycle, mouse and keyboard callbacks, context menu behavior, and recreation after Explorer restart. Replacing NotifyIcon means accepting ownership of those behaviors. [S13]
8. XAML object tests require a XAML application UI thread. Ordinary xUnit tests remain suitable for non-XAML code. Microsoft recommends extracting testable logic into a library. [S14]
9. System animation preference has a readable value and a change event; a periodic settings probe is unnecessary. Marshaling and subscription lifetime remain application responsibilities. [S15]

### Repository observations

- The app currently combines WPF windows with a WinForms tray/message loop in one application project. Tests reference that executable project. `scripts/package.ps1` requests single-file publishing. These are migration work, not settings that can safely remain untouched.
- `Adb/`, `State/` and `Diagnostics/` did not reference WPF/WinUI or `AdbView.Host` in the inspected imports/search. Backend lifecycle classes in `Host/` are mostly separable from window integration; extraction still requires compiling and checking all dependencies.
- `BoundaryTests.cs` contains WPF-era source rules, including a broad bare-control-name scan. A WinUI Button is not a forbidden WinForms Button. Replace obsolete implementation checks with precise dependency/type checks and preserve the underlying boundary.
- Recorded WPF reveal callback gaps included 141 ms and 328 ms during a nominal 167 ms transition. These are callback observations, not compositor presentation measurements or proof that WPF intrinsically cannot animate smoothly.
- The prior control probe reproduced surviving local durations after reduced motion, an active spinner after its flag cleared, and acceptance of an older UI snapshot. Detailed evidence remains in [the earlier audit](../docs/MOTION-SYSTEM-PLAN.md).

### Engineering judgments

For a status utility, reducing geometry changes is more valuable than animating every geometry change. A stable panel with a list/detail navigation model removes the repeated resize problem. WinUI is the preferred prototype because platform controls and Composition fit the proposed interaction model. It is not a guaranteed performance improvement. The retained backend and the new shell must still meet measured resource and latency budgets.

## Alternatives considered

| Option | Benefit | Cost / decision |
| --- | --- | --- |
| Stable-size WPF shell | Smallest framework migration; can remove much jitter | Credible fallback, but retains custom control/material work; do not claim it cannot work |
| Stable-size WinUI 3 shell | Native controls, accessibility, Composition, bounded material API | Preferred, conditional on prototype; bootstrap, tray and deployment need work |
| General custom animation/layout engine | Maximum visual flexibility | Rejected: large lifetime, geometry and accessibility burden for a small utility |
| Web-rendered desktop shell | Familiar web motion tools | Rejected for this direction: extra renderer/runtime and integration cost |
| C++/Win32/DirectComposition rewrite | Fine control and potential footprint savings | Rejected now: rewrites working managed backend and increases implementation burden |

## Chosen approach

Execute [WINUI-REBUILD-RUNBOOK.md](../docs/WINUI-REBUILD-RUNBOOK.md) in order. Prove WinUI windowing, rendering, publishing and testing first. Then extract a UI-independent core without changing backend behavior and replace the shell. Use a short list of named motion recipes, current-value retargeting, immediate semantic updates and fixed outer dimensions. Keep an opaque, stationary-surface fallback if bounded Acrylic fails; never compensate with per-frame native resizing.

## Rejected approaches

No rewrite of ADB for animation work; no timer/frame-loop animation engine; no spring on every control; no expanding bubble; no crossfade retaining disconnected-device text or an invalid QR; no preview package to rescue a prototype; no deletion of published native libraries by guesswork; no claim that a screenshot proves animation quality.

## Compatibility concerns

Minimum-OS API availability; reduced motion and contrast themes; transparent margins intercepting other apps; pinned versus transient focus; Explorer restart; unclipped shadows at taskbar/work-area edges; small displays and text scaling; self-contained native dependencies; custom harness startup without an installed SDK runtime; 120/144 Hz presentation; installed versus repository executable identity. Each is an explicit runbook gate.

## Testing implications

Keep backend/fake-server tests in the current xUnit suite. Move XAML-dependent assertions to a fixture verification mode hosted by the actual WinUI application, invoked by a script and returning structured results. Add real presentation traces and recordings, keyboard/UI Automation checks, clean-machine packaging and physical-device regression rows. Test migration requires an old-to-new assertion map; a changed test count is not sufficient evidence.

## Open items

M02 must establish exact build prerequisites, a working stable transparency implementation, repeatable presented-frame measurement, safe interruption from the current displayed value, and real runtime footprint. M03–M04 must validate the extraction/test harness. Unavailable clean machines, ARM64 or mixed-DPI hardware remain NOT EXERCISED. The runbook's dimensions, timing and performance thresholds are proposed product decisions, not results established by these sources.

## M02 implementation investigation

Started 2026-10-15 after M00/M01 passed. The NuGet v3 package index was queried directly and confirms 2.5.1 is available; no preview is selected. The local toolchain is SDK 10.0.401, MSBuild 18.9.11, .NET runtime 10.0.12; no .NET workloads or installed Windows SDK bin directories were found in the inspected locations. Restore/build, not a guessed installer prerequisite, will establish what is missing.

The minimal project is adapted from Microsoft's [Windowing C# sample](https://github.com/microsoft/WindowsAppSDK-Samples/blob/f93310028e8f904ee6b3fb133d413e13f4f859f4/Samples/Windowing/cs/cs-winui/Windowing.csproj), exact sample-changing commit **f93310028e8f904ee6b3fb133d413e13f4f859f4**, dated 2026-06-02. Its UseWinUI, manifest and package structure are used; no unrelated Gallery toolkit/dependency set is copied. Bounded material APIs were independently checked against the [Gallery sample](https://github.com/microsoft/WinUI-Gallery/blob/14061eeac21c38ae99f302edf3d9136d4f652c4c/WinUIGallery/Samples/SystemBackdropElement/SystemBackdropElementPage.xaml), repository commit **14061eeac21c38ae99f302edf3d9136d4f652c4c**. The sample imports Microsoft.UI.Xaml.Controls and uses SystemBackdropElement with DesktopAcrylicBackdrop. Compilation against the selected package remains required.

Windows SDK BuildTools [10.0.26100.6584](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.26100.6584) and the newer stable 26100-series metadata were inspected as alternatives; no extra direct build-tools dependency is introduced until restore/build proves it necessary. No system-wide SDK, workload, runtime or App SDK package registration is authorized merely by this investigation.

`wpr.exe -profiles` confirms GPU and XAMLActivity profiles exist. WPA/PresentMon were not found on PATH. This establishes trace capture capability, not a validated presentation-analysis result.

The first stable-package restore completed without installing a system SDK/workload. XAML compiled, including SystemBackdropElement. Build then reported application analyzer issues and CA5392 on the package-injected Foundation 2.3.12 `UndockedRegFreeWinRT-AutoInitializer.cs` P/Invoke. The prototype keeps the official auto-initializer and sets an assembly-wide [DefaultDllImportSearchPaths](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.defaultdllimportsearchpathsattribute?view=net-10.0) policy to **SafeDirectories**, one of the [CA5393-recommended choices](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca5393). It excludes current-directory/PATH lookup without editing cached SDK source or suppressing a diagnostic. An initial ApplicationDirectory choice was rejected by CA5393 and was not retained. UI awaits explicitly preserve the UI context.

The compiler's generated `XamlTypeInfo.g.cs` forces a public partial Application type, conflicting with CA1515 on a handwritten public partial declaration; WinUI rejects the WPF `x:ClassModifier` attribute. The retained solution lets the markup compiler generate the entire Application type from App.xaml and uses a small explicit entry point (`DISABLE_XAML_GENERATED_MAIN`) with the same ComWrappers/dispatcher initialization as the SDK-generated entry. It initializes generated resources and queues window creation after startup. No handwritten public App type, analyzer exception or reflection metadata adapter remains. The intermediate internal-Application/reflection experiment was rejected and removed.

The first self-contained launch failed natively during compiled window XAML loading (0xC000027B). Reduced XAML still failed; programmatic and trusted inline-XAML controls worked. Publish inventory then showed **no application PRI**. Setting **EnableMsixTooling=true** imports the official resource-generation targets while **WindowsPackageType=None** keeps the application unpackaged. The output now contains `WinUiShellProbe.pri`; the compiled XAML startup assertion passes with exit 0. The exact restored build-tools dependency is **Microsoft.Windows.SDK.BuildTools.MSIX 1.7.251221100**, transitive through the pinned App SDK and recorded in packages.lock.json; no extra direct package or system SDK/workload installation was needed. This is a verified build/publish fix, not evidence of full shell/motion quality.

The transparent-envelope experiment uses the documented [ICompositionSupportsSystemBackdrop](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.icompositionsupportssystembackdrop?view=windows-app-sdk-2.0) target with a transparent system Composition brush. A Windows.System dispatcher is required alongside the WinUI dispatcher; [the official backdrop guide](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops) specifies `DispatcherQueue.EnsureSystemDispatcherQueue()`. The probe explicitly ensures it before creating the system compositor. Its motion uses [UIElement.StartAnimation](https://learn.microsoft.com/en-us/windows/apps/develop/composition/xaml-comp-interop) with named Opacity and Translation targets, so it does not overwrite XAML's layout Offset. Runtime transparency, current-value continuity and cross-process input remain separate gates.

Runtime confirmation: Windows.System.DispatcherQueue was initially absent on this WinUI thread. Ensuring it before creating the system compositor eliminated the native backdrop initialization failure. Compiled XAML, ordinary and forced-failure verification paths, bounded Acrylic creation, transparent brush installation and the 25/50/90-percent interruption driver now execute. The driver ends with generation 8 / completed generation 8. This is lifecycle/API evidence, not a claim of visually continuous reversal or correct transparent input.

For presentation analysis, [Intel's official PresentMon release](https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0), **2.6.0**, published 2026-09-21, exposes a portable `PresentMon-2.6.0-x64.exe` (980,320 bytes). M02 may use this local diagnostic tool under artifacts; no MSI/service/overlay installation is needed or requested. Source, checksum and actual execution are recorded with the tool artifact. It is not a product dependency.

The transparent-margin input gate uses a separate synthetic underlay process and a real [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) pointer click. The test first checks [WindowFromPoint](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint) ownership and sends no input if the point is covered by any foreign process. It restores the cursor/DPI context and closes only its own two processes. A direct Invoke/SendMessage to the underlay would bypass OS hit testing and is not accepted as evidence. This is nonshipping test equipment, not another product surface.

**Observed renderer limitation:** positive-controlled cross-process clicks in the transparent envelope were intercepted by the probe, with both bounded Acrylic and an opaque panel visual. At 125% DPI, the tested margin was X=1384, Y=339, inside the native envelope but outside the visible panel. The separate underlay received its positive-control click, then did not receive the margin click. Both preferred and fallback-A variants therefore fail this input gate. The runbook's fallback B, a tightly bounded stationary opaque window with internal navigation motion only, is now the candidate. No HTTRANSPARENT assertion or layered-window workaround is substituted for the failed real input test.

Portable PresentMon 2.6.0 downloaded with the upstream SHA-256 verified: `B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF`. Its one-second capability attempt returned **exit 6: access denied** creating an ETW session. It requires an administrator token or Performance Log Users membership. Sandbox escalation alone did not supply that Windows permission. No UAC launch, group-membership change, service installation or claim of presented-frame verification was made at this point.
