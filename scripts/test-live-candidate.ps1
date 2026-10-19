[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$OutputDirectory,
    [switch]$PreferenceProbe,
    [switch]$StartupForwardingProbe
)
$ErrorActionPreference = 'Stop'
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $PSScriptRoot '../src/AdbView.WinUiMigration/bin/Release/net10.0-windows10.0.26100.0/win-x64/AdbView.WinUiMigration.exe' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '../artifacts/winui-rebuild/m08/live-redo/launch' }
# Bounded M08 composition observation. Reuse the established capture/input APIs.
# The tray callback below is synthetic; this is not a physical tray-input claim.
$captureSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'capture-native.ps1') -Raw
$captureApi = [regex]::Match($captureSource, '(?s)\$code = @"\r?\n(.*?)\r?\n"@').Groups[1].Value
if (-not $captureApi.Contains('public class NativeCapture')) { throw 'Existing capture API was not found.' }
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition $captureApi -ReferencedAssemblies System.Drawing
$inputSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../prototypes/WinUiShellProbe/test-native-input.ps1') -Raw
$inputApi = [regex]::Match($inputSource, "(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@").Groups[1].Value
if (-not $inputApi.Contains('public static class NativeProbeInput')) { throw 'Existing native input API was not found.' }
# Only the unchanged public Win32 declarations are needed. Omit the C# 6
# SendInput wrappers so Windows PowerShell's C# 5 compiler can load this subset.
$inputBoundary = $inputApi.IndexOf('    [DllImport("user32.dll")] private static extern uint SendInput', [StringComparison]::Ordinal)
if ($inputBoundary -lt 0) { throw 'Existing input declaration boundary was not found.' }
$inputApi = $inputApi.Substring(0, $inputBoundary) + '}'
Add-Type -TypeDefinition $inputApi.Replace('NativeProbeInput', 'LiveCandidateInput')
$previousDpi = [LiveCandidateInput]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($previousDpi -eq [IntPtr]::Zero) { throw 'Cannot establish physical capture coordinates.' }
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ($exe.Contains('smoke') -or [IO.Path]::GetFileName($exe) -ne 'AdbView.WinUiMigration.exe') { throw 'Expected a non-smoke candidate Release executable.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$destination = (Resolve-Path -LiteralPath $OutputDirectory).Path
$logPath = Join-Path (Split-Path -Parent $exe) 'candidate-live-redo.log'
$logOffset = if (Test-Path -LiteralPath $logPath) { (Get-Content -LiteralPath $logPath -Raw).Length } else { 0 }
$installed = Join-Path $env:LOCALAPPDATA 'Programs/ADBView/AdbView.exe'
function Read-ProductionIdentity {
    [ordered]@{
        ExecutableHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
        RunValue = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run', 'ADB View', $null)
        CandidateRunValue = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run', 'ADB View WinUI Migration', $null)
        Preferences = Read-PreferenceValues 'Software\ADBView'
    }
}
function Read-PreferenceValues([string]$KeyPath) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($KeyPath, $false)
    try {
        $values = [ordered]@{}
        foreach ($name in @('NotificationsEnabled','StatusBubbleEnabled','Theme')) {
            $present = $null -ne $key -and $key.GetValueNames() -contains $name
            $values[$name] = [ordered]@{
                Present = $present
                Value = if ($present) { $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null }
                Kind = if ($present) { $key.GetValueKind($name).ToString() } else { $null }
            }
        }
        return [ordered]@{ KeyPresent = $null -ne $key; Values = $values }
    }
    finally { if ($null -ne $key) { $key.Dispose() } }
}
$candidateKey = 'Software\ADBView.WinUiMigration'
$candidateBefore = if ($PreferenceProbe) { Read-PreferenceValues $candidateKey } else { $null }
$before = Read-ProductionIdentity
$process = $null
$evidence = [ordered]@{ StartedUtc = [DateTimeOffset]::UtcNow.ToString('O'); Windows = [Environment]::OSVersion.Version.ToString(); Executable = $exe; ExecutableHash = (Get-FileHash -LiteralPath $exe).Hash; ProductionBefore = $before; Stimulus = 'Synthetic version-4 tray selection callback to an enumerated owned receiver; no pointer/key input'; Passed = $false }
function Get-OwnedWindow([string]$Title) {
    foreach ($entry in [NativeCapture]::EnumerateProcessWindows($process.Id)) {
        if ($entry -match ('^HWND=0x([0-9A-F]+) .* title="' + [regex]::Escape($Title) + '" visible=')) {
            return [IntPtr]::new([Convert]::ToInt64($Matches[1], 16))
        }
    }
    return [IntPtr]::Zero
}
function Read-LaunchLog {
    if (-not (Test-Path -LiteralPath $logPath)) { return '' }
    $all = Get-Content -LiteralPath $logPath -Raw
    if ($all.Length -lt $logOffset) { throw 'Candidate log unexpectedly shrank.' }
    return $all.Substring($logOffset)
}
function Run-Secondary([string]$Argument) {
    $secondaryStart = [Diagnostics.ProcessStartInfo]::new($exe, $Argument)
    $secondaryStart.UseShellExecute = $false; $secondaryStart.CreateNoWindow = $true
    $secondary = [Diagnostics.Process]::Start($secondaryStart)
    try {
        if (-not $secondary.WaitForExit(5000)) { $secondary.Kill(); $secondary.WaitForExit(); throw 'Secondary candidate did not exit within 5 seconds.' }
        if ($secondary.ExitCode -ne 0) { throw "Secondary candidate failed: $Argument / $($secondary.ExitCode)" }
        return $secondary.Id
    }
    finally { $secondary.Dispose() }
}
function Wait-FlyoutVisibility([bool]$Expected) {
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while ([NativeCapture]::IsWindowVisible($flyout) -ne $Expected) {
        if ($process.HasExited -or $wait.Elapsed.TotalSeconds -gt 3) { throw "Forwarded visibility did not reach $Expected." }
        Start-Sleep -Milliseconds 20
    }
}
try {
    if ($PreferenceProbe) {
        $evidence.CandidateBefore = $candidateBefore
        # Save the exact restoration record before any native preference write.
        $evidence | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $destination 'result.json') -Encoding UTF8
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($candidateKey)
        try {
            $key.SetValue('NotificationsEnabled', 0, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('StatusBubbleEnabled', 0, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('Theme', 'Light', [Microsoft.Win32.RegistryValueKind]::String)
        }
        finally { $key.Dispose() }
    }
    $start = [Diagnostics.ProcessStartInfo]::new($exe, '--startup')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    $evidence.ProcessId = $process.Id
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($process.HasExited -or $deadline.Elapsed.TotalSeconds -gt 15) { throw 'Live candidate failed to become ready within 15 seconds.' }
        Start-Sleep -Milliseconds 50
        $receiver = Get-OwnedWindow 'ADB View candidate receiver'
        $flyout = Get-OwnedWindow 'ADB View - WinUI candidate'
    } while ($receiver -eq [IntPtr]::Zero -or $flyout -eq [IntPtr]::Zero -or (Read-LaunchLog) -notmatch 'Isolated live candidate ready')
    $evidence.InitialWindows = @([NativeCapture]::EnumerateProcessWindows($process.Id))
    $evidence.InitialFlyoutHidden = -not [NativeCapture]::IsWindowVisible($flyout)
    if (-not $evidence.InitialFlyoutHidden) { throw 'Live startup unexpectedly showed the flyout.' }
    if ($StartupForwardingProbe) {
        $evidence.SilentSecondary = Run-Secondary '--startup'
        if ([NativeCapture]::IsWindowVisible($flyout)) { throw 'Secondary startup disturbed hidden primary.' }
        $evidence.OpenSecondary = Run-Secondary '--live'; Wait-FlyoutVisibility $true
        $evidence.ForwardedOpenHasForeground = [LiveCandidateInput]::GetForegroundWindow() -eq $flyout
        $evidence.HideSecondary = Run-Secondary '--live'; Wait-FlyoutVisibility $false
        $evidence.SecondaryToggleRoundTrip = $true
    }
    [uint32]$ownerId = 0
    [void][LiveCandidateInput]::GetWindowThreadProcessId($receiver, [ref]$ownerId)
    if ($ownerId -ne $process.Id) { throw 'Receiver ownership changed; no callback sent.' }
    [void][LiveCandidateInput]::SendMessage($receiver, 0x8051, [UIntPtr]::Zero, [IntPtr]::new(0x10400))
    Start-Sleep -Milliseconds 700
    $evidence.FlyoutOpened = [NativeCapture]::IsWindowVisible($flyout)
    if (-not $evidence.FlyoutOpened) { throw 'Owned tray selection did not open live flyout.' }
    $bitmap = [NativeCapture]::CaptureWindow($flyout)
    if ($null -eq $bitmap) { throw 'Live capture failed.' }
    try { $bitmap.Save((Join-Path $destination 'live-flyout.png'), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
    $evidence.OpenWindows = @([NativeCapture]::EnumerateProcessWindows($process.Id))
    if (-not [NativeCapture]::CloseCandidate($process.Id) -or -not $process.WaitForExit(5000)) { throw 'Live candidate failed normal owned-window shutdown within 5 seconds.' }
    $evidence.ExitCode = $process.ExitCode
    if ($process.ExitCode -ne 0) { throw "Live candidate exited with $($process.ExitCode)." }
    $log = Read-LaunchLog
    Set-Content -LiteralPath (Join-Path $destination 'live.log') -Value $log -Encoding UTF8
    foreach ($required in @('Candidate live backend started','Candidate backend published revision','Candidate live backend owners disposed')) {
        if ($log -notmatch [regex]::Escape($required)) { throw "Missing live evidence: $required" }
    }
    if ($StartupForwardingProbe) {
        if ([regex]::Matches($log, 'Candidate live backend started').Count -ne 1 -or [regex]::Matches($log, 'Candidate secondary toggle applied').Count -ne 2) { throw 'Secondary launches did not preserve one backend and two accepted toggles.' }
        $evidence.OneBackendAcrossSecondaryLaunches = $true
        # Separate normal primary must open; the startup/secondary primary is already gone.
        $manualStart = [Diagnostics.ProcessStartInfo]::new($exe, '--live')
        $manualStart.UseShellExecute = $false; $manualStart.CreateNoWindow = $true
        $process = [Diagnostics.Process]::Start($manualStart)
        $evidence.ManualPrimaryProcessId = $process.Id
        $manualWait = [Diagnostics.Stopwatch]::StartNew()
        do {
            if ($process.HasExited -or $manualWait.Elapsed.TotalSeconds -gt 10) { throw 'Normal live primary did not open.' }
            Start-Sleep -Milliseconds 30
            $flyout = Get-OwnedWindow 'ADB View - WinUI candidate'
        } while ($flyout -eq [IntPtr]::Zero -or -not [NativeCapture]::IsWindowVisible($flyout))
        $evidence.ManualPrimaryOpened = $true
        $evidence.ManualPrimaryHasForeground = [LiveCandidateInput]::GetForegroundWindow() -eq $flyout
        if (-not [NativeCapture]::CloseCandidate($process.Id) -or -not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'Manual primary failed normal cleanup.' }
        $evidence.ManualPrimaryExitCode = $process.ExitCode
        $log = Read-LaunchLog
    }
    if ($PreferenceProbe) {
        $initialized = $log.IndexOf('Candidate live notification policy initialized: false', [StringComparison]::OrdinalIgnoreCase)
        $started = $log.IndexOf('Candidate live backend started', [StringComparison]::Ordinal)
        if ($initialized -lt 0 -or $initialized -ge $started -or $log -notmatch 'Candidate preferences readback: notifications false, bubble false, theme "?Light"?') {
            throw 'Saved candidate notification/theme preferences were not observed before backend startup.'
        }
        $evidence.SavedPreferencesObserved = $true
    }
    if ($log -match '\[(ERR|FTL)\]|\[WRN\] Native') { throw 'Live launch logged a fatal/error or native warning; inspect live.log.' }
    if ($StartupForwardingProbe -and $log -match '\[WRN\]') { throw 'Startup/forwarding observation logged a warning; inspect live.log.' }
    $evidence.ProductionAfter = Read-ProductionIdentity
    if (($before | ConvertTo-Json -Depth 9 -Compress) -ne ($evidence.ProductionAfter | ConvertTo-Json -Depth 9 -Compress)) { throw 'Production executable/startup/preferences identity changed.' }
    $evidence.Passed = $true
}
catch { $evidence.Failure = $_.Exception.Message; throw }
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $requested = [NativeCapture]::CloseCandidate($process.Id)
        if (-not $requested -or -not $process.WaitForExit(5000)) {
            $evidence.ForcedOwnedCleanup = $true
            $process.Kill(); $process.WaitForExit()
        }
    }
    if (Test-Path -LiteralPath $logPath) { Set-Content -LiteralPath (Join-Path $destination 'live.log') -Value (Read-LaunchLog) -Encoding UTF8 }
    if ($PreferenceProbe) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($candidateKey)
        try {
            foreach ($name in @('NotificationsEnabled','StatusBubbleEnabled','Theme')) {
                $saved = $candidateBefore.Values[$name]
                if ($saved.Present) { $key.SetValue($name, $saved.Value, [Microsoft.Win32.RegistryValueKind]([Enum]::Parse([Microsoft.Win32.RegistryValueKind], $saved.Kind))) }
                else { $key.DeleteValue($name, $false) }
            }
            $empty = $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
        }
        finally { $key.Dispose() }
        if (-not $candidateBefore.KeyPresent -and $empty) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($candidateKey, $false) }
        $evidence.CandidateAfter = Read-PreferenceValues $candidateKey
        $evidence.CandidateRestored = ($candidateBefore | ConvertTo-Json -Depth 6 -Compress) -eq ($evidence.CandidateAfter | ConvertTo-Json -Depth 6 -Compress)
        if (-not $evidence.CandidateRestored) { $evidence.Passed = $false }
    }
    $evidence.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $evidence | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $destination 'result.json') -Encoding UTF8
    if ([LiveCandidateInput]::SetThreadDpiAwarenessContext($previousDpi) -eq [IntPtr]::Zero) { throw 'Cannot restore capture thread DPI awareness.' }
}
if ($PreferenceProbe -and -not $evidence.CandidateRestored) { throw 'Candidate preference restoration did not match the saved values.' }
$evidence | ConvertTo-Json -Depth 9
