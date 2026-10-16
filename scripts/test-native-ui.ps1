# M04+ headed gate. Requires PowerShell 7 (ProcessStartInfo.ArgumentList).
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [string[]]$Fixtures = @('empty', 'list', 'details', 'pairing', 'adb-unavailable', 'adb-restoring', 'wireless-unavailable', 'wireless-restoring',
        'two-devices', 'multi-device', 'many-devices', 'long-names', 'missing-metadata', 'unknown', 'unauthorized', 'offline', 'connected-wifi', 'text-200', 'list-scenarios',
        'pairing-scenarios', 'pairing-offer', 'pairing-progress', 'pairing-progress-reduced', 'pairing-success', 'pairing-failure', 'pairing-qr', 'discovery-health', 'motion-scenarios', 'native-scenarios'),
    [string]$ResultDirectory = (Join-Path $PSScriptRoot '../artifacts/winui-rebuild/m08/ui'),
    [ValidateRange(1, 120)][int]$TimeoutSeconds = 20
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Headed verification requires PowerShell 7.' }
if (-not [Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
    throw 'Headed verification NOT EXERCISED: interactive desktop unavailable.'
}
$candidateExe = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([IO.Path]::GetFileName($candidateExe) -cne 'AdbView.WinUiMigration.exe') { throw 'Explicit migration candidate executable required; production executable is not allowed.' }
$resultRoot = [IO.Path]::GetFullPath($ResultDirectory)
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$results = @()
foreach ($fixture in $Fixtures) {
    $suffix = [guid]::NewGuid().ToString('N')
    $resultPath = Join-Path $resultRoot ($suffix + '.json')
    $start = [Diagnostics.ProcessStartInfo]::new($candidateExe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path -Parent $candidateExe
    $start.ArgumentList.Add('--fixture=' + $fixture)
    $start.ArgumentList.Add('--instance=' + $suffix)
    $start.ArgumentList.Add('--verify=' + $resultPath)
    $ownedProcess = [Diagnostics.Process]::Start($start)
    $inputProcess = $null
    try {
        if ($fixture -eq 'native-scenarios') {
            $inputStart = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
            $inputStart.UseShellExecute = $false; $inputStart.CreateNoWindow = $true
            foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'test-native-bubble-input.ps1'), '-ResultPath', $resultPath, '-ExpectedProcessId', [string]$ownedProcess.Id)) {
                $inputStart.ArgumentList.Add($argument)
            }
            $inputProcess = [Diagnostics.Process]::Start($inputStart)
        }
        # The new M07 fixture includes 500 native navigation cycles; prior fixtures keep their bound.
        $fixtureTimeout = if ($fixture -eq 'motion-scenarios') { [Math]::Max($TimeoutSeconds, 60) } else { $TimeoutSeconds }
        if (-not $ownedProcess.WaitForExit($fixtureTimeout * 1000)) { throw "Candidate timed out: $fixture (PID $($ownedProcess.Id))." }
        if ($null -ne $inputProcess) {
            if (-not $inputProcess.WaitForExit(2000) -or $inputProcess.ExitCode -ne 0) { throw 'Owned bubble input helper failed.' }
            $inputResult = Get-Content -LiteralPath ($resultPath + '.input.json') -Raw | ConvertFrom-Json
            if ($inputResult.Passed -ne $true -or $inputResult.ProcessId -ne $ownedProcess.Id) { throw 'Owned bubble input result correlation failed.' }
            $nativeLog = $resultPath + '.native.log'
            if (-not (Test-Path -LiteralPath $nativeLog)) { throw 'Missing isolated native failure log.' }
            if (Select-String -LiteralPath $nativeLog -Pattern '\b(WRN|ERR|FTL)\]' -Quiet) { throw "Native adapter reported a failure: $nativeLog" }
        }
        if (-not (Test-Path -LiteralPath $resultPath)) { throw "Missing candidate result: $fixture, exit $($ownedProcess.ExitCode)." }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($result.SchemaVersion -ne 1 -or $result.ProcessId -ne $ownedProcess.Id -or $result.Fixture -cne $fixture -or $result.Instance -cne ('AdbView.WinUiMigration.Fixture.' + $suffix)) {
            throw "Candidate result correlation failed: $fixture."
        }
        if ($ownedProcess.ExitCode -ne 0 -or $result.Passed -ne $true -or @($result.Assertions).Count -lt 10 -or @($result.Assertions | Where-Object Passed -ne $true).Count -gt 0) {
            throw "Candidate assertions/exit failed: $fixture, exit $($ownedProcess.ExitCode), result $resultPath."
        }
        $results += [pscustomobject]@{ Fixture=$fixture; ProcessId=$ownedProcess.Id; ExitCode=$ownedProcess.ExitCode; Assertions=@($result.Assertions).Count; ResultPath=$resultPath }
    }
    finally {
        if ($null -ne $inputProcess) {
            if (-not $inputProcess.HasExited) { $inputProcess.Kill($true); $inputProcess.WaitForExit() }
            $inputProcess.Dispose()
        }
        # Only the exact process created above; no process-name cleanup or installed IPC.
        if (-not $ownedProcess.HasExited) { $ownedProcess.Kill(); $ownedProcess.WaitForExit() }
        $ownedProcess.Dispose()
    }
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resultRoot 'summary.json') -Encoding utf8
$results | Format-Table -AutoSize
Write-Host 'test-native-ui: headed candidate fixtures passed.'
