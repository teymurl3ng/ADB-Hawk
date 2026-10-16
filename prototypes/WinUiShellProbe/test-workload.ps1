[CmdletBinding()]
param([switch]$CapturePresentation, [switch]$ElevateProfiler)

$ErrorActionPreference = 'Stop'
if ($ElevateProfiler -and -not $CapturePresentation) { throw 'Elevation is only valid with an explicit presentation capture.' }
$probeRepo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$evidence = Join-Path $probeRepo 'artifacts\winui-rebuild\m02'
$caseId = 'workload-' + [guid]::NewGuid().ToString('N')
$resultPath = Join-Path $evidence ($caseId+'.json')
$driverStartPath = $resultPath + '.start'
$probeExe = Join-Path $probeRepo 'artifacts\winui-rebuild\probe-publish\WinUiShellProbe.exe'
$profilerExe = Join-Path $probeRepo 'artifacts\winui-rebuild\tools\PresentMon-2.6.0-x64.exe'
$profilerProcess = $null; $probeProcess = $null
$samples = [Collections.Generic.List[object]]::new()
try {
    $probeProcess = Start-Process -FilePath $probeExe -ArgumentList @(('"--verify='+$resultPath+'"'),('"--driver-start='+$driverStartPath+'"'),'--native','--workload','--hold-ms=35000') -PassThru
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath ($resultPath+'.workload-ready.json'))) {
        if ($probeProcess.HasExited -or $deadline.Elapsed.TotalSeconds -gt 15) { throw 'Probe workload did not become ready.' }
        Start-Sleep -Milliseconds 50
    }
    if ($CapturePresentation) {
        $actualHash = (Get-FileHash -LiteralPath $profilerExe -Algorithm SHA256).Hash
        if ($actualHash -ne 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF') { throw 'Profiler checksum mismatch.' }
        $capturePath = Join-Path $evidence ($caseId+'.csv')
        $profilerArguments = @('--process_id', $probeProcess.Id, '--session_name', ('ADBViewProbe-'+[guid]::NewGuid().ToString('N')),
            '--timed','40','--terminate_after_timed','--no_track_input','--no_console_stats','--v2_metrics','--qpc_time_ms',
            '--output_file',('"'+$capturePath+'"'))
        # Elevates only the verified portable Intel profiler. No installation, account,
        # service/group changes, broad target or stop-existing-session argument.
        if ($ElevateProfiler) {
            $profilerProcess = Start-Process -FilePath $profilerExe -ArgumentList $profilerArguments -Verb RunAs -WindowStyle Hidden -PassThru
        }
        else {
            $profilerProcess = Start-Process -FilePath $profilerExe -ArgumentList $profilerArguments -WindowStyle Hidden -PassThru
        }
    }
    # Release only after profiler startup/UAC has returned; consent time is not
    # part of the workload. The file is unique to this owned probe process.
    [IO.File]::WriteAllText($driverStartPath, 'start')
    $deadline.Restart()
    while (-not $probeProcess.HasExited) {
        if ($deadline.Elapsed.TotalSeconds -gt 85) { throw 'Workload exceeded its bounded run.' }
        $probeProcess.Refresh()
        $samples.Add([pscustomobject]@{
            Seconds = $deadline.Elapsed.TotalSeconds
            WorkingSet = $probeProcess.WorkingSet64
            PrivateBytes = $probeProcess.PrivateMemorySize64
            Handles = $probeProcess.HandleCount
            CpuSeconds = $probeProcess.TotalProcessorTime.TotalSeconds
        })
        Start-Sleep -Milliseconds 1000
    }
    if ($probeProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) { throw "Workload exited $($probeProcess.ExitCode) without a passing result." }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $result.Passed -or -not $result.Workload.Passed) { throw 'Workload assertion failed.' }
    if ($null -ne $profilerProcess) {
        if (-not $profilerProcess.WaitForExit(10000)) { throw 'Scoped profiler did not terminate after its timed capture.' }
        if ($profilerProcess.ExitCode -ne 0) { throw "Profiler failed with exit $($profilerProcess.ExitCode)." }
        if (-not (Test-Path -LiteralPath $capturePath)) { throw 'Profiler produced no CSV.' }
        $frameCount = @(Import-Csv -LiteralPath $capturePath).Count
        if ($frameCount -eq 0) { throw 'Profiler CSV has no attributed frames; this is not presentation proof.' }
        [pscustomobject]@{ProcessId=$probeProcess.Id;ProfilerExit=$profilerProcess.ExitCode;Rows=$frameCount;Csv=$capturePath;Meaning='Capture only; attribution and compositor continuity still require analysis.'} |
            ConvertTo-Json | Set-Content -LiteralPath ($resultPath+'.capture.json') -Encoding utf8
    }
    $result.Workload | ConvertTo-Json
    $peakWorkingSet = ($samples | Measure-Object -Property WorkingSet -Maximum).Maximum
    $resourceResult = [pscustomobject]@{
        WithinCeiling = $peakWorkingSet -le 209715200
        CeilingBytes = 209715200
        PeakSampledWorkingSet = $peakWorkingSet
        FinalSample = $samples[$samples.Count-1]
        VisibleAtCompletion = $result.VisibleAtCompletion
        AnimationsEnabled = $result.AnimationsEnabled
        Meaning = 'Prototype only; the integrated app must meet the same complete-app ceiling.'
    }
    $resourceResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ($resultPath+'.resources.json') -Encoding utf8
    $resourceResult | ConvertTo-Json -Depth 4
    "Resource samples: $resultPath.samples.json"
    if (-not $resourceResult.WithinCeiling) { throw "Prototype working-set ceiling exceeded: $peakWorkingSet bytes sampled; limit 209715200." }
}
finally {
    $samples | ConvertTo-Json | Set-Content -LiteralPath ($resultPath+'.samples.json') -Encoding utf8
    foreach ($ownedProcess in @($profilerProcess,$probeProcess)) {
        if ($null -ne $ownedProcess) {
            if (-not $ownedProcess.HasExited) { Stop-Process -Id $ownedProcess.Id -Force }
            $ownedProcess.Dispose()
        }
    }
}
