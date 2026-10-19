# Isolated WinUI candidate preferences

- **Date:** 2026-10-17
- **Author:** Codex
- **Phase:** M08 preference slice
- **Status:** preference slice verified; M08 remains in progress

## Problem

Reuse existing preference defaults/parsing/logged failure behavior in executable Host without repeating the archived production Run overwrite or moving registry ownership into Core. A failed write must refresh checks from the actual store, not an optimistic menu toggle.

## Sources consulted

| Source | URL / reference | Version checked |
| --- | --- | --- |
| RegistryKey.GetValue / SetValue | https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.registrykey.getvalue?view=net-10.0 ; https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.registrykey.setvalue?view=net-10.0 | .NET 10, consulted 2026-10-17 |
| Run and RunOnce keys | https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys | Updated 2026-02-21, consulted 2026-10-17 |
| Application.RequestedTheme | https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.requestedtheme?view=windows-app-sdk-2.0 | Stable pinned API; consulted 2026-10-17 |
| Existing owners | src/AdbView/Host/UserPreferences.cs; TrayIcon.cs; Core/Host/EffectiveTheme.cs; ENGINEERING §23 | Exact existing source inspected |

## Version investigated

SDK 10.0.401; App SDK 2.5.1 / WinUI 2.3.9; CsWin32 0.3.333; Windows 10.0.26100. No package change.

## Findings

Registry values have explicit kinds; writes can fail with access/security/I/O exceptions. Existing UserPreferences already owns conversions, defaults, logging and readback; reuse the file unchanged through a source link in the candidate executable, injecting a separate store. Registry ownership stays outside Core. Run values register programs at logon; they are unnecessary for notification/bubble/theme persistence and must not be touched by those commands. Application.RequestedTheme supplies the startup application theme; live OS color/contrast events remain a separate M08 owner.

## Alternatives considered

| Option | Pros | Cons |
| --- | --- | --- |
| Link unchanged Host parser and inject candidate store | Preserves tested defaults and failures; no Core/WPF behavior change | Temporary source link until cutover |
| Copy parser or move it into Core | Less project linking | Duplicates policy or violates native ownership; rejected |
| Persist every menu field on any command | Few callbacks | Repeats confirmed unrelated-write/Run bug; rejected |

## Chosen approach

HKCU Software/ADBView.WinUiMigration stores only NotificationsEnabled, StatusBubbleEnabled and Theme. A Host controller handles one selected command then reads actual values and updates notification policy/theme. Native menu refresh reads that owner before opening; fixture menus remain memory-only unless a verification-specific in-memory owner is attached. Initialize saved notification state before backend startup. Use existing EffectiveTheme and reducer events, never local device/pin truth. No Run adapter in this item: live Start with Windows intent is logged as uncomposed and remains unchecked. No production key writes or new fallback policy.

## Compatibility concerns

Default notifications on, bubble off, theme System and corrupt-value fallback remain the existing contract. Persisted startup registration/forwarding, live system theme/contrast events and physical environment acceptance stay open M08. Original UserPreferences native implementation remains in the executable Host; do not instantiate its production default store in the candidate.

## Testing implications

Exercise actual parser/controller with fake stores, failed writes and reload; actual native menu checks with fake store; existing notification initialization test. Actual Release observation seeds/restores only exact candidate values and compares production executable/Run/preferences read-only. Capture final native fixture and run the full gate. No actual autostart, pairing, repair or OS preference change.

## Open items

Remaining M08 integration and M09/M12 qualification are not passed by this slice.

## Observed result, 2026-10-17

Final resumption at 17:30–17:34 Asia/Karachi: approval review available again; after fresh owner click, unchanged focused helper/candidate exit 0, 293 native assertions and all 11 guarded input observations pass with empty stderr. Full `verify-final-resumption.log` then passes pinned toolchain/format/zero-warning build, 1,090 C# cases, 29 headed fixtures / 954 assertions and publish smoke. No implementation, activation, assertion or threshold change. Recorded source/binary hashes match the prior actual Release probe/captures; installed executable/Run identity rechecked unchanged. Earlier failures below remain historical evidence; current pass does not establish their exact causes. Preference slice verified, M08 incomplete. Startup/forwarding is next separate scope; deferred visual/resource findings stay M09.

Follow-up: `verify-after-codex-click.log` passes build/format/all 1,090 C# cases, reaches native-scenarios and passes final added bubble command/suppression/reload checks (228 assertions pass before stop). Existing external bubble-action assertion still fails; helper exits without result and this run lacks captured stderr. Previous ShellExperienceHost cause cannot be presumed. Another preceding run failed empty foreground; Antigravity was observed afterward, not at the assertion instant. No implementation/threshold changes. Approval-review service usage limit blocked the separate evidence-copy command and further elevated diagnostics; it was not a safety determination. See the status follow-up for current result and exact next diagnostic. Full gate remains incomplete.

Zero-warning final candidate build and 1,090 C# tests pass, including 14 new preference cases. A real isolated Release launch observed saved notification-off initialization before backend start, Light rendering of a current USB device and normal exit/disposal. Temporary candidate presence/types/values/key were restored exactly; production executable/Run/preferences read-only comparisons match. Prescribed native fixture and live Light captures inspected on Windows 26100 at 125%.

The full gate remains failed at the existing guarded external bubble click: ShellExperienceHost PID 13920 covers the target; the helper sends no input. Artifact-only stderr capture establishes that missing prerequisite, not a framework defect. Earlier 100 new native menu checks passed before the stop; final extra bubble-binding coverage is compiled but awaits headed execution. No assertions, thresholds or activation mechanics were relaxed. Owner desktop response remains pending. Separate observed low-contrast device outline in Light mode is categorized for M09; cause unproven, renderer untouched. Evidence and exact resume scope are in status and `artifacts/winui-rebuild/m08/preferences/`.
