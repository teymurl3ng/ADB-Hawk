[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultPath, [Parameter(Mandatory)][int]$ExpectedProcessId)
$ErrorActionPreference = 'Stop'
# Conditional M08 experiment: reuse the M02 driver API, no new UIA provider/input mechanism.
$existingDriver = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../prototypes/WinUiShellProbe/test-native-input.ps1') -Raw
$api = [regex]::Match($existingDriver, "(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@").Groups[1].Value
if (-not $api.Contains('public static class NativeProbeInput')) { throw 'Existing native input helper was not found.' }
Add-Type -TypeDefinition $api.Replace('NativeProbeInput', 'CandidateBubbleInput')
$saved = New-Object CandidateBubbleInput+POINT
if (-not [CandidateBubbleInput]::GetCursorPos([ref]$saved)) { throw 'Cannot save cursor position.' }
$previousDpi = [CandidateBubbleInput]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($previousDpi -eq [IntPtr]::Zero) { throw 'Cannot establish per-monitor test input coordinates.' }
$observations = [ordered]@{}
$underlayProcess = $null
function Send-CandidateCommand([uint32]$Command) {
    [CandidateBubbleInput]::SendMessage($flyout, [uint32]$ready.TestMessage, [UIntPtr]::new($Command), [IntPtr]::Zero).ToInt64()
}
function Move-OwnedInput([int]$X, [int]$Y, [int]$Owner) {
    $target = New-Object CandidateBubbleInput+POINT
    $target.X = $X; $target.Y = $Y
    [uint32]$targetOwner = 0
    [void][CandidateBubbleInput]::GetWindowThreadProcessId([CandidateBubbleInput]::WindowFromPoint($target), [ref]$targetOwner)
    if ($targetOwner -ne $Owner) { throw "Owned input target is covered by PID $targetOwner; no input sent." }
    if (-not [CandidateBubbleInput]::SetCursorPos($X, $Y)) { throw 'Cannot position owned test input.' }
}
function Focus-OwnedFlyout {
    $rectangle = New-Object CandidateBubbleInput+RECT
    if (-not [CandidateBubbleInput]::GetWindowRect($flyout, [ref]$rectangle)) { throw 'Cannot read flyout input bounds.' }
    Move-OwnedInput ($rectangle.Left + [int](150 * $ready.Scale)) ($rectangle.Top + [int](40 * $ready.Scale)) $ExpectedProcessId
    [CandidateBubbleInput]::Mouse(2); [CandidateBubbleInput]::Mouse(4)
    Start-Sleep -Milliseconds 100
    if ([CandidateBubbleInput]::GetForegroundWindow() -ne $flyout) { throw 'Owned pointer input failed to activate flyout.' }
}
function Send-OwnedKey([IntPtr]$ExpectedWindow, [uint16]$Key) {
    if ([CandidateBubbleInput]::GetForegroundWindow() -ne $ExpectedWindow) { throw 'Owned window does not have foreground; no key sent.' }
    [CandidateBubbleInput]::Key($Key); Start-Sleep -Milliseconds 100
}
try {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $readyPath = $ResultPath + '.input-ready.json'
    while (-not (Test-Path -LiteralPath $readyPath)) {
        if ($deadline.Elapsed.TotalSeconds -gt 10) { throw 'Owned bubble input readiness timed out.' }
        Start-Sleep -Milliseconds 30
    }
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($ready.ProcessId -ne $ExpectedProcessId) { throw 'Bubble input process correlation failed.' }
    $bubble = [IntPtr]::new([long]$ready.BubbleHwnd)
    $flyout = [IntPtr]::new([long]$ready.FlyoutHwnd)
    [uint32]$actualPid = 0
    [void][CandidateBubbleInput]::GetWindowThreadProcessId($bubble, [ref]$actualPid)
    if ($actualPid -ne $ExpectedProcessId) { throw 'Bubble HWND is not owned by the candidate; no input sent.' }
    $bounds = New-Object CandidateBubbleInput+RECT
    if (-not [CandidateBubbleInput]::GetWindowRect($bubble, [ref]$bounds)) { throw 'Cannot query bubble input bounds.' }
    $point = New-Object CandidateBubbleInput+POINT
    $point.X = [int](($bounds.Left + $bounds.Right) / 2); $point.Y = [int](($bounds.Top + $bounds.Bottom) / 2)
    [void][CandidateBubbleInput]::GetWindowThreadProcessId([CandidateBubbleInput]::WindowFromPoint($point), [ref]$actualPid)
    if ($actualPid -ne $ExpectedProcessId) { throw "Bubble input target is covered by PID $actualPid; no input sent." }
    $foreground = [CandidateBubbleInput]::GetForegroundWindow()
    if (-not [CandidateBubbleInput]::SetCursorPos($point.X, $point.Y)) { throw 'Cannot move pointer to owned bubble.' }
    Start-Sleep -Milliseconds 800
    $observations.HoverPreservesForeground = [CandidateBubbleInput]::GetForegroundWindow() -eq $foreground
    $afterHover = New-Object CandidateBubbleInput+RECT
    if (-not [CandidateBubbleInput]::GetWindowRect($bubble, [ref]$afterHover)) { throw 'Cannot query hovered bubble bounds.' }
    $observations.HoverNeverExpands = $bounds.Left -eq $afterHover.Left -and $bounds.Top -eq $afterHover.Top -and $bounds.Right -eq $afterHover.Right -and $bounds.Bottom -eq $afterHover.Bottom
    [void][CandidateBubbleInput]::GetWindowThreadProcessId([CandidateBubbleInput]::WindowFromPoint($point), [ref]$actualPid)
    if ($actualPid -ne $ExpectedProcessId) { throw 'Owned bubble became covered; no click sent.' }
    [CandidateBubbleInput]::Mouse(2)
    try {
        Start-Sleep -Milliseconds 80
        $observations.MouseDownPreservesForeground = [CandidateBubbleInput]::GetForegroundWindow() -eq $foreground
    }
    finally { [CandidateBubbleInput]::Mouse(4) }
    $keyboardDeadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath ($ResultPath + '.keyboard-ready'))) {
        if ($keyboardDeadline.Elapsed.TotalSeconds -gt 5) { throw 'Owned keyboard fixture readiness timed out.' }
        Start-Sleep -Milliseconds 20
    }
    [void](Send-CandidateCommand 3); Start-Sleep -Milliseconds 100
    $beforeDrag = New-Object CandidateBubbleInput+RECT
    if (-not [CandidateBubbleInput]::GetWindowRect($flyout, [ref]$beforeDrag)) { throw 'Cannot query native drag bounds.' }
    $dragX = $beforeDrag.Left + [int](150 * $ready.Scale); $dragY = $beforeDrag.Top + [int](40 * $ready.Scale)
    Move-OwnedInput $dragX $dragY $ExpectedProcessId
    [CandidateBubbleInput]::Mouse(2)
    try {
        Start-Sleep -Milliseconds 80
        if (-not [CandidateBubbleInput]::SetCursorPos(($dragX-30), ($dragY-20))) { throw 'Cannot move owned native drag.' }
        Start-Sleep -Milliseconds 100
    }
    finally { [CandidateBubbleInput]::Mouse(4) }
    Start-Sleep -Milliseconds 100
    $afterDrag = New-Object CandidateBubbleInput+RECT
    if (-not [CandidateBubbleInput]::GetWindowRect($flyout, [ref]$afterDrag)) { throw 'Cannot query completed native drag.' }
    $observations.NativePinnedDrag = $afterDrag.Left -eq ($beforeDrag.Left-30) -and $afterDrag.Top -eq ($beforeDrag.Top-20) -and
        ($afterDrag.Right-$afterDrag.Left) -eq ($beforeDrag.Right-$beforeDrag.Left) -and ($afterDrag.Bottom-$afterDrag.Top) -eq ($beforeDrag.Bottom-$beforeDrag.Top)
    if (-not [CandidateBubbleInput]::SetWindowPos($flyout, [IntPtr]::Zero, $beforeDrag.Left, $beforeDrag.Top, 0, 0, 0x15)) { throw 'Cannot restore only the owned drag fixture position.' }
    $underlayReady = $ResultPath + '.underlay.json'
    $clickLog = $ResultPath + '.underlay-clicks.txt'
    $underlayStart = [Diagnostics.ProcessStartInfo]::new("$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe")
    $underlayStart.UseShellExecute = $false; $underlayStart.CreateNoWindow = $true
    foreach ($argument in @('-NoProfile','-File',(Join-Path $PSScriptRoot '../prototypes/WinUiShellProbe/test-underlay.ps1'),'-ClickLog',$clickLog,'-ReadyFile',$underlayReady)) { $underlayStart.ArgumentList.Add($argument) }
    $underlayProcess = [Diagnostics.Process]::Start($underlayStart)
    $underlayDeadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $underlayReady)) {
        if ($underlayProcess.HasExited -or $underlayDeadline.Elapsed.TotalSeconds -gt 5) { throw 'Owned outside-input control failed to start.' }
        Start-Sleep -Milliseconds 30
    }
    $underlay = Get-Content -LiteralPath $underlayReady -Raw | ConvertFrom-Json
    Move-OwnedInput $underlay.ControlX $underlay.ControlY $underlayProcess.Id
    [CandidateBubbleInput]::Mouse(2); [CandidateBubbleInput]::Mouse(4); Start-Sleep -Milliseconds 100
    $observations.OwnedOutsidePositiveControl = (Test-Path -LiteralPath $clickLog) -and [CandidateBubbleInput]::GetForegroundWindow() -eq [IntPtr]::new([long]$underlay.Hwnd)
    $observations.PinnedSurvivesOutsideActivation = ((Send-CandidateCommand 0) -band 5) -eq 5
    [void](Send-CandidateCommand 4); Start-Sleep -Milliseconds 100
    [void](Send-CandidateCommand 2); Focus-OwnedFlyout
    Move-OwnedInput $underlay.ControlX $underlay.ControlY $underlayProcess.Id
    [CandidateBubbleInput]::Mouse(2); [CandidateBubbleInput]::Mouse(4); Start-Sleep -Milliseconds 100
    $observations.UnpinnedOutsideActivationDismisses = ((Send-CandidateCommand 0) -band 3) -eq 2
    [void](Send-CandidateCommand 2); Focus-OwnedFlyout
    [void](Send-CandidateCommand 5); Send-OwnedKey $flyout 27
    $observations.EscapeReturnsFromDetails = ((Send-CandidateCommand 0) -band 35) -eq 1
    [void](Send-CandidateCommand 3); Start-Sleep -Milliseconds 100
    Send-OwnedKey $flyout 27
    $observations.EscapeHidesPinnedWithoutUnpinning = ((Send-CandidateCommand 0) -band 7) -eq 6
    [void](Send-CandidateCommand 4); Start-Sleep -Milliseconds 100
    [void](Send-CandidateCommand 2); Focus-OwnedFlyout
    $receiver = [IntPtr]::new([long]$ready.ReceiverHwnd)
    $menuLocation = [long]($beforeDrag.Left+80) -bor ([long]($beforeDrag.Top+80) -shl 16)
    if (-not [CandidateBubbleInput]::PostMessage($receiver, [uint32]$ready.TrayCallback, [UIntPtr]::new([uint64]$menuLocation), [IntPtr]::new((1 -shl 16) -bor 0x7b))) { throw 'Cannot enqueue owned native menu.' }
    Start-Sleep -Milliseconds 150
    $observations.NativeMenuLoopOpened = ((Send-CandidateCommand 0) -band 8) -eq 8
    Send-OwnedKey $receiver 27
    $observations.MenuCancelKeepsFlyoutVisible = ((Send-CandidateCommand 0) -band 11) -eq 1
    [void](Send-CandidateCommand 2); Focus-OwnedFlyout
    $passed = @($observations.Values | Where-Object { $_ -ne $true }).Count -eq 0
    [pscustomobject]@{ Passed=$passed; ProcessId=$ExpectedProcessId; BubbleHwnd=$ready.BubbleHwnd; Observations=$observations; Mechanism='Existing M02 guarded SendInput, candidate and owned underlay only' } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ($ResultPath + '.input.json') -Encoding utf8
    [void](Send-CandidateCommand 99)
    if (-not $passed) { throw 'External bubble input observation failed.' }
}
finally {
    if ($null -ne $underlayProcess) {
        if (-not $underlayProcess.HasExited) {
            if (-not $underlayProcess.CloseMainWindow() -or -not $underlayProcess.WaitForExit(2000)) { $underlayProcess.Kill(); $underlayProcess.WaitForExit(); Write-Warning 'Owned underlay required forced cleanup.' }
        }
        $underlayProcess.Dispose()
    }
    if (-not [CandidateBubbleInput]::SetCursorPos($saved.X, $saved.Y)) { Write-Warning 'Cannot restore pointer after owned test.' }
    if ([CandidateBubbleInput]::SetThreadDpiAwarenessContext($previousDpi) -eq [IntPtr]::Zero) { Write-Warning 'Cannot restore test DPI context.' }
}
