[CmdletBinding()]
param([switch]$Acrylic, [switch]$Stationary)

$ErrorActionPreference = 'Stop'
$probeRepo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$probeEvidence = Join-Path $probeRepo 'artifacts\winui-rebuild\m02'
$probeCaseId = 'input-' + [guid]::NewGuid().ToString('N')
$probeClickLog = Join-Path $probeEvidence ($probeCaseId + '.clicks.txt')
$underlayReady = Join-Path $probeEvidence ($probeCaseId + '.underlay-ready.txt')
$probeResultPath = Join-Path $probeEvidence ($probeCaseId + '.json')
$probeExe = Join-Path $probeRepo 'artifacts\winui-rebuild\probe-publish\WinUiShellProbe.exe'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ProbePointerInput {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT {
        public int X; public int Y; public uint Data; public uint Flags; public uint Time; public UIntPtr Extra;
    }
    public static void Click() {
        var inputs = new[] { new INPUT { Mouse = new MOUSEINPUT { Flags = 2 } }, new INPUT { Mouse = new MOUSEINPUT { Flags = 4 } } };
        if (SendInput(2, inputs, Marshal.SizeOf<INPUT>()) != 2) throw new InvalidOperationException("Input injection failed");
    }
}
'@
$previousDpi = [ProbePointerInput]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($previousDpi -eq [IntPtr]::Zero) { throw 'Input-test DPI context unavailable.' }
$savedPointer = New-Object ProbePointerInput+POINT
if (-not [ProbePointerInput]::GetCursorPos([ref]$savedPointer)) { throw 'Cannot save cursor position.' }
$underlayProcess = $null
$probeProcess = $null
try {
    $underlayScript = Join-Path $PSScriptRoot 'test-underlay.ps1'
    $underlayArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$underlayScript+'"'),'-ClickLog',('"'+$probeClickLog+'"'),'-ReadyFile',('"'+$underlayReady+'"'))
    $underlayProcess = Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $underlayArguments -WindowStyle Hidden -PassThru
    $readyDeadline = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $underlayReady)) {
        if ($underlayProcess.HasExited -or $readyDeadline.Elapsed.TotalSeconds -gt 10) { throw 'Synthetic underlay failed to start.' }
        Start-Sleep -Milliseconds 50
    }
    $underlayReadyData = Get-Content -LiteralPath $underlayReady -Raw | ConvertFrom-Json
    [void][ProbePointerInput]::ShowWindow([IntPtr]::new([long]$underlayReadyData.Hwnd),1)
    [void][ProbePointerInput]::SetForegroundWindow([IntPtr]::new([long]$underlayReadyData.Hwnd))
    Start-Sleep -Milliseconds 200
    $controlPoint = New-Object ProbePointerInput+POINT
    $controlPoint.X = [int]$underlayReadyData.ControlX
    $controlPoint.Y = [int]$underlayReadyData.ControlY
    [uint32]$controlProcess = 0
    [void][ProbePointerInput]::GetWindowThreadProcessId([ProbePointerInput]::WindowFromPoint($controlPoint),[ref]$controlProcess)
    if ($controlProcess -ne $underlayProcess.Id) { throw "Underlay control point is covered (point PID $controlProcess, expected $($underlayProcess.Id), X=$($controlPoint.X), Y=$($controlPoint.Y)); no input was sent." }
    if (-not [ProbePointerInput]::SetCursorPos($controlPoint.X,$controlPoint.Y)) { throw 'Cannot position pointer over underlay control point.' }
    [ProbePointerInput]::Click()
    Start-Sleep -Milliseconds 300
    if (-not (Test-Path -LiteralPath $probeClickLog)) { throw 'Underlay failed its positive input control; margin result would be inconclusive.' }
    Move-Item -LiteralPath $probeClickLog -Destination ($probeClickLog+'.positive-control.txt')
    $probeArguments = @(('"--verify='+$probeResultPath+'"'),'--hold-ms=8000')
    if (-not $Stationary) { $probeArguments += '--surface' }
    if ($Acrylic) { $probeArguments += '--acrylic' }
    $probeProcess = Start-Process -FilePath $probeExe -ArgumentList $probeArguments -PassThru
    $readyDeadline.Restart()
    while (-not (Test-Path -LiteralPath ($probeResultPath+'.ready.json'))) {
        if ($probeProcess.HasExited -or $readyDeadline.Elapsed.TotalSeconds -gt 15) { throw 'Probe failed to reach held visible state.' }
        Start-Sleep -Milliseconds 50
    }
    $ready = Get-Content -LiteralPath ($probeResultPath+'.ready.json') -Raw | ConvertFrom-Json
    $clickPoint = New-Object ProbePointerInput+POINT
    $clickPoint.X = [int]$ready.X + 8
    $clickPoint.Y = [int]$ready.Y + 8
    if ($Stationary) {
        $clickPoint.X = [int]$ready.X - 8
        $clickPoint.Y = [int]$ready.Y + 50
    }
    $pointWindow = [ProbePointerInput]::WindowFromPoint($clickPoint)
    [uint32]$pointProcess = 0
    [void][ProbePointerInput]::GetWindowThreadProcessId($pointWindow, [ref]$pointProcess)
    if ($pointProcess -notin @($probeProcess.Id,$underlayProcess.Id)) { throw 'Test point is covered by a foreign window; no input was sent.' }
    if (-not [ProbePointerInput]::SetCursorPos($clickPoint.X,$clickPoint.Y)) { throw 'Cannot position pointer over owned test windows.' }
    [ProbePointerInput]::Click()
    Start-Sleep -Milliseconds 300
    $inputPassed = Test-Path -LiteralPath $probeClickLog
    $inputResult = [pscustomobject]@{Passed=$inputPassed;PositiveControlPassed=$true;Acrylic=[bool]$Acrylic;Stationary=[bool]$Stationary;PointX=$clickPoint.X;PointY=$clickPoint.Y;PointProcessId=$pointProcess;UnderlayProcessId=$underlayProcess.Id;ProbeProcessId=$probeProcess.Id;Meaning='Real pointer click outside visible panel must reach the separate underlay process.'}
    $inputResult | ConvertTo-Json | Set-Content -LiteralPath ($probeResultPath+'.input.json') -Encoding utf8
    $inputResult | ConvertTo-Json
    if (-not $probeProcess.WaitForExit(12000)) { throw 'Probe did not complete its held verification.' }
    if ($probeProcess.ExitCode -ne 0) { throw "Probe exited $($probeProcess.ExitCode)." }
    if (-not $inputPassed) { throw 'Transparent margin intercepted cross-process pointer input.' }
}
finally {
    [void][ProbePointerInput]::SetCursorPos($savedPointer.X,$savedPointer.Y)
    [void][ProbePointerInput]::SetThreadDpiAwarenessContext($previousDpi)
    foreach ($ownedProcess in @($probeProcess,$underlayProcess)) {
        if ($null -ne $ownedProcess) {
            if (-not $ownedProcess.HasExited) {
                [void]$ownedProcess.CloseMainWindow()
                if (-not $ownedProcess.WaitForExit(2000)) { Stop-Process -Id $ownedProcess.Id -Force }
            }
            $ownedProcess.Dispose()
        }
    }
}
