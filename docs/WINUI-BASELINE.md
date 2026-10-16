# Shell rebuild baseline — M00

Date: 2026-10-15. Baseline HEAD: `4dcc61ed4f5f3f7ab028cbadd8db120d2e4b6be7`, plus the existing uncommitted reliability repairs and planning documents.

## Protected work and tests

- Recoverable copies of all **57** modified/untracked baseline files, SHA-256 inventory, binary working-tree patch, HEAD and complete status are under `artifacts/winui-rebuild/baseline/`. This includes the previously uncommitted reliability repairs. No reset, commit or branch switch was performed.
- `scripts/verify.ps1` passed: **1,010 executed cases**, zero failed/skipped, formatting, Release warnings-as-errors build, boundaries and publish smoke. Log: `artifacts/winui-rebuild/baseline/verify.log`.
- [WINUI-TEST-MIGRATION.csv](WINUI-TEST-MIGRATION.csv) inventories all **824 Fact/Theory methods** (theories produce multiple executed cases). Every method has a planned disposition; replacements remain pending. Existing assertions remain intact. Groups: Adb 324, State 189, Notifications 67, Host 125, NativeUi 53, Boundaries 35, Integration 19, Bridge 7, App 5.
- The 22 canonical fixtures and all tray menu commands are inventoried in `baseline/fixtures.txt` and `baseline/tray-commands.txt`. Preserve each scenario and action through migration.

## Machine and running identity

Windows 11 Enterprise LTSC Evaluation, build **26100**; 1920×1080 display reporting **144 Hz**; AMD Radeon 780M driver **31.0.14070.6001** and NVIDIA RTX 4050 driver **31.0.15.4680**. Full raw details are in `baseline/machine.json`.

Running ADB View: PID **23260**, repository executable `src/AdbView/bin/Release/net10.0-windows/AdbView.exe`; shared adb remains PID **5888** at the user's platform-tools path. The installed app was not replaced. This process identity is an observation at baseline time, not a stable identifier for later tools.

The existing app used approximately **143.9 MiB working set**, **78.3 MiB private bytes**, and **1,094 handles** in the ten-second observation. CPU increased by **0.0 seconds** over **10.023 seconds**. Visibility and workload were not controlled, so this is not a ten-minute hidden-idle benchmark or a leak diagnosis. The current working set already exceeds the product's proposed 120 MB target; the new framework must be measured against both the written target and an honest existing-app baseline.

## Visual and temporal evidence

Existing current-build evidence is retained in `artifacts/motion-audit-2026-10-15/`: callback logs, isolated control probe, and source/build identity. Recorded reveal callback gaps include 141 ms and 328 ms for nominal 167 ms transitions. These are not presented-frame measurements. Complete existing screenshots for global ADB and local wireless failure states are in `artifacts/visual/phase-2/` and were inspected during the preceding repair.

No new baseline cold/warm launch series or motion recording was taken. Code inspection found that the current `--test-fixture` path still constructs the production `SingleInstance` mutex and forwards to the running app. Launching it as an isolated fixture would toggle the user's existing instance; the capture script cannot safely provide process-isolated evidence while that instance remains running. Do not use that helper to manufacture a new baseline capture. M02 uses an isolated prototype identity, and M04 fixes fixture startup isolation in the candidate.

`wpr.exe` is available; WPA/PresentMon and Windows SDK binaries were not found in the inspected locations. Compilation and a validated presentation measurement path are M02 work. Pure callback logs do not satisfy that gate. Physical mixed-DPI/60 Hz conditions, clean machines, ARM64 execution and the release soak remain NOT EXERCISED.

## M00 result

PASS for protected baseline, complete method/fixture/action inventory, existing-app gate and recorded resource/tooling observations. Temporal benchmarking and isolated startup/recording evidence are explicitly incomplete and must be resolved by the M02/M09 gates. This baseline does not claim any WinUI or new motion behavior works.
