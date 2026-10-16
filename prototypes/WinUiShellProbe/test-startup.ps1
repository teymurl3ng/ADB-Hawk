$ErrorActionPreference='Stop'
$probeRepo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$evidence=Join-Path $probeRepo 'artifacts\winui-rebuild\m02'
$exe=Join-Path $probeRepo 'artifacts\winui-rebuild\probe-publish\WinUiShellProbe.exe'
$observations=for($index=0;$index -lt 10;$index++) {
    $path=Join-Path $evidence ('startup-'+[guid]::NewGuid().ToString('N')+'.json')
    $before=[Diagnostics.Stopwatch]::GetTimestamp()
    $process=Start-Process -FilePath $exe -ArgumentList @('--startup',('"--verify='+$path+'"')) -PassThru
    try {
        if(-not $process.WaitForExit(10000)) { throw 'Isolated startup did not finish within ten seconds.' }
        if($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $path)) { throw "Startup failed with exit $($process.ExitCode)." }
        $result=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if(-not $result.Passed) { throw 'Startup visibility assertion failed.' }
        [pscustomobject]@{Index=$index;TrayReadyMs=($result.TrayReadyQpc-$before)/[Diagnostics.Stopwatch]::Frequency*1000;FlyoutShown=$result.FlyoutVisible;Result=$path}
    }
    finally {
        if(-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        $process.Dispose()
    }
}
$observations | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'startup-observations.json') -Encoding utf8
$ordered=@($observations.TrayReadyMs | Sort-Object)
[pscustomobject]@{FreshProcesses=10;TrayReadyP95Ms=$ordered[9];Passed=($ordered[9] -le 1000);CacheConditions='Fresh processes on an OS-warm developer machine; no cache flush.'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'startup-summary.json') -Encoding utf8
$observations | Format-Table -AutoSize
if($ordered[9] -gt 1000) { throw 'Fresh-process tray-ready p95 exceeds one second.' }
