# M07 current-machine temporal proof using the existing M02 cropped desktop observer.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath,
      [string]$ResultDirectory = (Join-Path $PSScriptRoot '../artifacts/winui-rebuild/m07/temporal'))
$ErrorActionPreference = 'Stop'
$taskExe = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([IO.Path]::GetFileName($taskExe) -cne 'AdbView.WinUiMigration.exe') { throw 'Explicit candidate required.' }
$taskRoot = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
$taskPrefix = Join-Path $taskRoot ([guid]::NewGuid().ToString('N'))
$taskResult = $taskPrefix + '.result.json'
$taskSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../prototypes/WinUiShellProbe/diagnostics/DesktopFrames.cs') -Raw
# Retain the observer's strict PID/name/extent guard; substitute only the authorized candidate name.
$taskSource = $taskSource.Replace('WinUiShellProbe','AdbView.WinUiMigration').Replace('DesktopFrames','M07DesktopFrames')
$taskSourcePath = $taskPrefix + '.observer.cs'
Set-Content -LiteralPath $taskSourcePath -Value $taskSource
$taskObserverScript = $taskPrefix + '.observer.ps1'
@'
param([string]$Source, [long]$Window, [int]$TargetProcess, [string]$Prefix)
$ErrorActionPreference = 'Stop'
Add-Type -Path $Source -CompilerOptions '/unsafe'
[M07DesktopFrames]::Capture($Window,$TargetProcess,$Prefix,8)
'@ | Set-Content -LiteralPath $taskObserverScript
$taskCandidate = $null; $taskObserver = $null
try {
    $taskStart = [Diagnostics.ProcessStartInfo]::new($taskExe); $taskStart.UseShellExecute = $false
    $taskStart.ArgumentList.Add('--fixture=motion-recording')
    $taskInstance = [guid]::NewGuid().ToString('N')
    $taskStart.ArgumentList.Add('--instance=' + $taskInstance); $taskStart.ArgumentList.Add('--verify=' + $taskResult)
    $taskCandidate = [Diagnostics.Process]::Start($taskStart)
    $taskWait = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath ($taskResult + '.ready.json'))) {
        if ($taskCandidate.HasExited -or $taskWait.Elapsed.TotalSeconds -gt 15) { throw 'Candidate recorder did not reach ready state.' }
        Start-Sleep -Milliseconds 40
    }
    $taskReady = Get-Content -LiteralPath ($taskResult + '.ready.json') -Raw | ConvertFrom-Json
    if ($taskReady.ProcessId -ne $taskCandidate.Id) { throw 'Recorder ownership mismatch.' }
    $taskObserverStart = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $taskObserverStart.UseShellExecute = $false; $taskObserverStart.CreateNoWindow = $true
    foreach ($taskArgument in @('-NoProfile','-File',$taskObserverScript,'-Source',$taskSourcePath,'-Window',[string]$taskReady.Hwnd,'-TargetProcess',[string]$taskCandidate.Id,'-Prefix',$taskPrefix)) {
        $taskObserverStart.ArgumentList.Add($taskArgument)
    }
    $taskObserver = [Diagnostics.Process]::Start($taskObserverStart)
    $taskWait.Restart()
    while (-not (Test-Path -LiteralPath ($taskPrefix + '.ready.json'))) {
        if ($taskObserver.HasExited -or $taskWait.Elapsed.TotalSeconds -gt 10) { throw 'Desktop observer failed to become ready.' }
        Start-Sleep -Milliseconds 40
    }
    Set-Content -LiteralPath ($taskResult + '.capture-ready') -Value 'ready'
    if (-not $taskObserver.WaitForExit(12000) -or $taskObserver.ExitCode -ne 0) { throw 'Cropped desktop observer failed.' }
    Set-Content -LiteralPath ($taskResult + '.capture-complete') -Value 'complete'
    if (-not $taskCandidate.WaitForExit(10000) -or $taskCandidate.ExitCode -ne 0) { throw 'Candidate recording failed.' }
    $taskVerified = Get-Content -LiteralPath $taskResult -Raw | ConvertFrom-Json
    if ($taskVerified.Passed -ne $true -or $taskVerified.ProcessId -ne $taskCandidate.Id -or $taskVerified.Instance -cne ('AdbView.WinUiMigration.Fixture.' + $taskInstance)) { throw 'Recording assertions/correlation failed.' }
    Copy-Item -LiteralPath ($taskResult + '.events.json') -Destination ($taskPrefix + '.events.json')
    Copy-Item -LiteralPath ($taskResult + '.interruptions.json') -Destination ($taskPrefix + '.interruptions.json')
    [pscustomobject]@{ Prefix=$taskPrefix; CandidateProcessId=$taskCandidate.Id; Width=$taskReady.Width; Height=$taskReady.Height; Events=12; Captured=$true } |
        ConvertTo-Json | Set-Content -LiteralPath ($taskPrefix + '.summary.json')
    Write-Output $taskPrefix
}
finally {
    foreach ($taskOwned in @($taskObserver,$taskCandidate)) {
        if ($null -ne $taskOwned) {
            if (-not $taskOwned.HasExited) { $taskOwned.Kill(); $taskOwned.WaitForExit() }
            $taskOwned.Dispose()
        }
    }
}
