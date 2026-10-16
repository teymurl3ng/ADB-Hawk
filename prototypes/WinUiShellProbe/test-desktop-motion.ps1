[CmdletBinding()]
param([switch]$WarmOpen)
$ErrorActionPreference='Stop'
$probeRepo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$evidence=Join-Path $probeRepo 'artifacts\winui-rebuild\m02'
$prefix=Join-Path $evidence ($(if($WarmOpen) {'warm-open-'} else {'desktop-motion-'})+[guid]::NewGuid().ToString('N'))
$resultPath=$prefix+'.json'
$probeExe=Join-Path $probeRepo 'artifacts\winui-rebuild\probe-publish\WinUiShellProbe.exe'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DesktopMotionInput {
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window,uint message,UIntPtr command,IntPtr data);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
'@
$probeProcess=$null;$captureProcess=$null;$underlayProcess=$null;$underlayHandle=[IntPtr]::Zero
$events=[Collections.Generic.List[object]]::new()
$segments=[Collections.Generic.List[object]]::new()
try {
    if($WarmOpen) {
        $underlayReady=$prefix+'.underlay.json'
        $underlayScript=Join-Path $PSScriptRoot 'test-underlay.ps1'
        $underlayProcess=Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-NoProfile','-File',('"'+$underlayScript+'"'),'-ClickLog',('"'+$prefix+'.clicks.txt"'),'-ReadyFile',('"'+$underlayReady+'"')) -WindowStyle Hidden -PassThru
        $underlayWait=[Diagnostics.Stopwatch]::StartNew()
        while(-not (Test-Path -LiteralPath $underlayReady)) {
            if($underlayProcess.HasExited -or $underlayWait.Elapsed.TotalSeconds -gt 15) { throw 'Owned warm-open underlay did not start.' }
            Start-Sleep -Milliseconds 40
        }
        $underlayHandle=[IntPtr]::new([long](Get-Content -LiteralPath $underlayReady -Raw | ConvertFrom-Json).Hwnd)
        if(-not [DesktopMotionInput]::SetWindowPos($underlayHandle,[IntPtr]::new(-1),0,0,0,0,0x53)) { throw 'Cannot place owned backing surface.' }
    }
    $probeProcess=Start-Process -FilePath $probeExe -ArgumentList @(('"--verify='+$resultPath+'"'),'--native','--hold-ms=60000') -PassThru
    $wait=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path -LiteralPath ($resultPath+'.ready.json'))) {
        if($probeProcess.HasExited -or $wait.Elapsed.TotalSeconds -gt 15) { throw 'Probe did not reach its held state.' }
        Start-Sleep -Milliseconds 40
    }
    $ready=Get-Content -LiteralPath ($resultPath+'.ready.json') -Raw | ConvertFrom-Json
    $hwnd=[IntPtr]::new([long]$ready.Hwnd)
    [void][DesktopMotionInput]::SendMessage($hwnd,[uint32]$ready.TestMessage,[UIntPtr]::new(3),[IntPtr]::Zero)
    [void][DesktopMotionInput]::SendMessage($hwnd,[uint32]$ready.TestMessage,[UIntPtr]::new(2),[IntPtr]::Zero)
    if(-not [DesktopMotionInput]::SetWindowPos($hwnd,[IntPtr]::new(-1),1100,250,0,0,0x51)) { throw 'Cannot position owned motion fixture.' }
    # Warm all three bounded pages before recording; cold preparation is a separate metric.
    foreach($command in @(5,6,8)) {
        [void][DesktopMotionInput]::SendMessage($hwnd,[uint32]$ready.TestMessage,[UIntPtr]::new([uint32]$command),[IntPtr]::Zero)
        Start-Sleep -Milliseconds 220
    }
    if($WarmOpen) { [void][DesktopMotionInput]::SendMessage($hwnd,[uint32]$ready.TestMessage,[UIntPtr]::new(7),[IntPtr]::Zero) }
    $captureScript=Join-Path $PSScriptRoot 'diagnostics\capture-desktop.ps1'
    for($segment=0;$segment -lt 4;$segment++) {
        $segmentPrefix=$prefix+'.segment'+$segment
        $segmentEvents=[Collections.Generic.List[object]]::new()
        $captureArguments=@('-NoProfile','-File',('"'+$captureScript+'"'),'-Window',$ready.Hwnd,'-TargetProcess',$probeProcess.Id,'-OutputPrefix',('"'+$segmentPrefix+'"'),'-Seconds','10')
        if($WarmOpen) { $captureArguments+=@('-UnderlayWindow',$underlayHandle.ToInt64(),'-UnderlayProcess',$underlayProcess.Id) }
        $captureProcess=Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $captureArguments -WindowStyle Hidden -RedirectStandardOutput ($segmentPrefix+'.stdout.txt') -RedirectStandardError ($segmentPrefix+'.stderr.txt') -PassThru
        $wait.Restart()
        while(-not (Test-Path -LiteralPath ($segmentPrefix+'.ready.json'))) {
            if($captureProcess.HasExited -or $wait.Elapsed.TotalSeconds -gt 10) { throw 'Desktop observer did not start; inspect its error output.' }
            Start-Sleep -Milliseconds 40
        }
        for($localIndex=0;$localIndex -lt 25;$localIndex++) {
            $index=$segment*25+$localIndex
            $command=@(5,6,8)[$index%3]
            if($WarmOpen) {
                [void][DesktopMotionInput]::SendMessage($hwnd,[uint32]$ready.TestMessage,[UIntPtr]::new(1),[IntPtr]::Zero)
                Start-Sleep -Milliseconds 75
                $command=2
            }
            $before=[Diagnostics.Stopwatch]::GetTimestamp()
            [void][DesktopMotionInput]::SendMessage($hwnd,[uint32]$ready.TestMessage,[UIntPtr]::new([uint32]$command),[IntPtr]::Zero)
            $event=[pscustomobject]@{Index=$index;Command=$command;InputQpc=$before;ReturnedQpc=[Diagnostics.Stopwatch]::GetTimestamp()}
            $segmentEvents.Add($event);$events.Add($event)
            Start-Sleep -Milliseconds $(if($WarmOpen) {225} else {300})
        }
        $segmentEvents | ConvertTo-Json | Set-Content -LiteralPath ($segmentPrefix+'.events.json') -Encoding utf8
        if(-not $captureProcess.WaitForExit(6000) -or $captureProcess.ExitCode -ne 0) { throw 'Desktop observer failed or exceeded its capture bound.' }
        $frames=@(Import-Csv -LiteralPath ($segmentPrefix+'.frames.csv'))
        if($frames.Count -lt 30) { throw 'Too few captured frames; not temporal evidence.' }
        $segments.Add([pscustomobject]@{Prefix=$segmentPrefix;Events=$segmentEvents.Count;Frames=$frames.Count})
        $captureProcess.Dispose();$captureProcess=$null
    }
    $events | ConvertTo-Json | Set-Content -LiteralPath ($prefix+'.events.json') -Encoding utf8
    if(-not $probeProcess.WaitForExit(20000) -or $probeProcess.ExitCode -ne 0) { throw 'Probe failed after motion capture.' }
    $summary=[pscustomobject]@{Capture=$prefix;Segments=$segments.ToArray();Events=$events.Count;Frequency=[Diagnostics.Stopwatch]::Frequency;WarmOpen=[bool]$WarmOpen;Meaning='Four serial 25-command recordings; each has its own unchanged size bound and integrity checks. Analyze each separately; never compute intervals across segment gaps. Warm-open mode records only the owned probe and synthetic backing surface.'}
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($prefix+'.segments.json') -Encoding utf8
    $summary | ConvertTo-Json -Depth 5
}
finally {
    foreach($owned in @($captureProcess,$probeProcess,$underlayProcess)) {
        if($null -ne $owned) {
            if(-not $owned.HasExited) {
                [void]$owned.CloseMainWindow()
                if(-not $owned.WaitForExit(1500)) { Stop-Process -Id $owned.Id -Force }
            }
            $owned.Dispose()
        }
    }
}
