[CmdletBinding()]
param([switch]$NativeUiaClient, [ValidateSet('Unchanged','On','Off')][string]$NativeAutoSetFocus = 'Unchanged', [switch]$GrantProbeForeground)

$ErrorActionPreference = 'Stop'
if ($NativeAutoSetFocus -ne 'Unchanged' -and -not $NativeUiaClient) { throw 'Explicit focus policy requires the native comparison client.' }
$probeRepo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$probeEvidence = Join-Path $probeRepo 'artifacts\winui-rebuild\m02'
$casePath = Join-Path $probeEvidence ('native-input-' + [guid]::NewGuid().ToString('N'))
$underlayReady = $casePath + '.underlay.json'
$clickLog = $casePath + '.clicks.txt'
$resultPath = $casePath + '.json'
$probeExe = Join-Path $probeRepo 'artifacts\winui-rebuild\probe-publish\WinUiShellProbe.exe'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeProbeInput {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Explicit, Size=40)] private struct INPUT {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MOUSEINPUT Mouse;
        [FieldOffset(8)] public KEYBDINPUT Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int X; public int Y; public uint Data; public uint Flags; public uint Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public UIntPtr Extra; }
    public static void Mouse(uint flag) => Send(new INPUT { Mouse = new MOUSEINPUT { Flags = flag } });
    public static void Key(ushort key) {
        Send(new INPUT { Type = 1, Keyboard = new KEYBDINPUT { Key = key } });
        Send(new INPUT { Type = 1, Keyboard = new KEYBDINPUT { Key = key, Flags = 2 } });
    }
    private static void Send(INPUT input) {
        if (SendInput(1, new[] { input }, 40) != 1) throw new InvalidOperationException("Input injection failed");
    }
}
'@

function Wait-ProbeFile([string]$Path, [System.Diagnostics.Process]$Process) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path)) {
        if ($Process.HasExited -or $deadline.Elapsed.TotalSeconds -gt 15) { throw "Owned helper failed to become ready: $Path" }
        Start-Sleep -Milliseconds 50
    }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
function Move-OwnedPointer([int]$X, [int]$Y, [int]$ExpectedProcess) {
    $point = New-Object NativeProbeInput+POINT
    $point.X = $X; $point.Y = $Y
    [uint32]$actualProcess = 0
    [void][NativeProbeInput]::GetWindowThreadProcessId([NativeProbeInput]::WindowFromPoint($point), [ref]$actualProcess)
    if ($actualProcess -ne $ExpectedProcess) { throw "Owned target ($X,$Y) expected PID $ExpectedProcess, covered by PID $actualProcess; no input sent." }
    if (-not [NativeProbeInput]::SetCursorPos($X,$Y)) { throw 'Cannot position pointer over owned target.' }
}
function Send-ProbeCommand([uint32]$Command) {
    [NativeProbeInput]::SendMessage($flyoutHandle, [uint32]$ready.TestMessage, [UIntPtr]::new($Command), [IntPtr]::Zero).ToInt64()
}
function Send-ProbeKey([uint16]$Key) {
    if ([NativeProbeInput]::GetForegroundWindow() -ne $flyoutHandle) { throw "Probe does not own foreground (wanted $flyoutHandle, actual $([NativeProbeInput]::GetForegroundWindow())); no keyboard input sent." }
    [NativeProbeInput]::Key($Key)
    Start-Sleep -Milliseconds 100
}
function Focus-ProbeByClick {
    $flyoutBounds = New-Object NativeProbeInput+RECT
    if (-not [NativeProbeInput]::GetWindowRect($flyoutHandle,[ref]$flyoutBounds)) { throw 'Cannot query flyout input bounds.' }
    Move-OwnedPointer ($flyoutBounds.Left + [int](150*$ready.Scale)) ($flyoutBounds.Top + [int](40*$ready.Scale)) $probeProcess.Id
    [NativeProbeInput]::Mouse(2); [NativeProbeInput]::Mouse(4)
    Start-Sleep -Milliseconds 100
    if ([NativeProbeInput]::GetForegroundWindow() -ne $flyoutHandle) { throw 'Real pointer input did not activate the owned flyout.' }
}

$previousDpi = [NativeProbeInput]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($previousDpi -eq [IntPtr]::Zero) { throw 'Input-test DPI context unavailable.' }
$savedPointer = New-Object NativeProbeInput+POINT
if (-not [NativeProbeInput]::GetCursorPos([ref]$savedPointer)) { throw 'Cannot save cursor position.' }
$underlayProcess = $null; $probeProcess = $null
$observations = [ordered]@{}
try {
    $underlayScript = Join-Path $PSScriptRoot 'test-underlay.ps1'
    $underlayArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$underlayScript+'"'),'-ClickLog',('"'+$clickLog+'"'),'-ReadyFile',('"'+$underlayReady+'"'))
    $underlayProcess = Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $underlayArguments -WindowStyle Hidden -PassThru
    $underlay = Wait-ProbeFile $underlayReady $underlayProcess
    $underlayHandle = [IntPtr]::new([long]$underlay.Hwnd)
    [void][NativeProbeInput]::ShowWindow($underlayHandle,1)
    [void][NativeProbeInput]::SetForegroundWindow($underlayHandle)
    if (-not [NativeProbeInput]::SetWindowPos($underlayHandle,[IntPtr]::new(-1),0,0,0,0,0x53)) { throw 'Cannot expose owned positive-control window.' }
    Start-Sleep -Milliseconds 200
    Move-OwnedPointer $underlay.ControlX $underlay.ControlY $underlayProcess.Id
    [NativeProbeInput]::Mouse(2); [NativeProbeInput]::Mouse(4)
    Start-Sleep -Milliseconds 150
    $observations.PositiveInputControl = Test-Path -LiteralPath $clickLog
    if (-not $observations.PositiveInputControl) { throw 'Underlay failed its positive input control.' }
    if (-not [NativeProbeInput]::SetWindowPos($underlayHandle,[IntPtr]::new(-2),0,0,0,0,0x13)) { throw 'Cannot restore owned underlay stacking.' }

    $probeProcess = Start-Process -FilePath $probeExe -ArgumentList @(('"--verify='+$resultPath+'"'),'--native','--hold-ms=20000') -PassThru
    $ready = Wait-ProbeFile ($resultPath+'.ready.json') $probeProcess
    $flyoutHandle = [IntPtr]::new([long]$ready.Hwnd)
    $bubbleHandle = [IntPtr]::new([long]$ready.BubbleHwnd)
    [void](Send-ProbeCommand 1)
    # Isolated input fixture position, away from the notification/calendar area.
    # Move only this process's bubble; do not dismiss or reconfigure shell surfaces.
    if (-not [NativeProbeInput]::SetWindowPos($bubbleHandle,[IntPtr]::new(-1),1100,650,0,0,0x51)) { throw 'Cannot position owned bubble input fixture.' }
    Start-Sleep -Milliseconds 150
    Move-OwnedPointer $underlay.ControlX $underlay.ControlY $underlayProcess.Id
    [NativeProbeInput]::Mouse(2); [NativeProbeInput]::Mouse(4)
    Start-Sleep -Milliseconds 100
    if ([NativeProbeInput]::GetForegroundWindow() -ne $underlayHandle) { throw 'Underlay did not become foreground; bubble test would be inconclusive.' }
    if (-not [NativeProbeInput]::SetWindowPos($bubbleHandle,[IntPtr]::new(-1),1100,650,0,0,0x51)) { throw 'Cannot expose owned bubble input fixture.' }
    $rectangle = New-Object NativeProbeInput+RECT
    if (-not [NativeProbeInput]::GetWindowRect($bubbleHandle,[ref]$rectangle)) { throw 'Cannot query bubble bounds.' }
    Move-OwnedPointer (($rectangle.Left+$rectangle.Right)/2) (($rectangle.Top+$rectangle.Bottom)/2) $probeProcess.Id
    Start-Sleep -Milliseconds 1200
    $observations.BubbleHoverPreservesForeground = [NativeProbeInput]::GetForegroundWindow() -eq $underlayHandle
    [NativeProbeInput]::Mouse(2)
    Start-Sleep -Milliseconds 100
    $observations.BubbleMouseDownPreservesForeground = [NativeProbeInput]::GetForegroundWindow() -eq $underlayHandle
    [NativeProbeInput]::Mouse(4)
    Start-Sleep -Milliseconds 150
    $observations.BubbleClickOpensFlyout = [NativeProbeInput]::GetForegroundWindow() -eq $flyoutHandle -and ((Send-ProbeCommand 0) -band 3) -eq 1

    [void](Send-ProbeCommand 3)
    $beforeDrag = New-Object NativeProbeInput+RECT
    if (-not [NativeProbeInput]::GetWindowRect($flyoutHandle,[ref]$beforeDrag)) { throw 'Cannot read pinned drag bounds.' }
    $dragX = $beforeDrag.Left + [int](150*$ready.Scale)
    $dragY = $beforeDrag.Top + [int](40*$ready.Scale)
    Move-OwnedPointer $dragX $dragY $probeProcess.Id
    [NativeProbeInput]::Mouse(2)
    Start-Sleep -Milliseconds 80
    if (-not [NativeProbeInput]::SetCursorPos(($dragX-30),($dragY-20))) { throw 'Cannot move owned native drag.' }
    Start-Sleep -Milliseconds 100
    [NativeProbeInput]::Mouse(4)
    Start-Sleep -Milliseconds 100
    $afterDrag = New-Object NativeProbeInput+RECT
    if (-not [NativeProbeInput]::GetWindowRect($flyoutHandle,[ref]$afterDrag)) { throw 'Cannot read completed drag bounds.' }
    $observations.NativePinnedDrag = $afterDrag.Left -eq ($beforeDrag.Left-30) -and $afterDrag.Top -eq ($beforeDrag.Top-20) -and
        ($afterDrag.Right-$afterDrag.Left) -eq ($beforeDrag.Right-$beforeDrag.Left) -and ($afterDrag.Bottom-$afterDrag.Top) -eq ($beforeDrag.Bottom-$beforeDrag.Top)
    if (-not [NativeProbeInput]::SetWindowPos($flyoutHandle,[IntPtr]::Zero,$beforeDrag.Left,$beforeDrag.Top,0,0,0x15)) { throw 'Cannot restore owned flyout position after drag.' }
    Move-OwnedPointer $underlay.ControlX $underlay.ControlY $underlayProcess.Id
    [NativeProbeInput]::Mouse(2); [NativeProbeInput]::Mouse(4)
    Start-Sleep -Milliseconds 150
    $observations.PinSurvivesOutsideClick = ((Send-ProbeCommand 0) -band 5) -eq 5
    [void](Send-ProbeCommand 4)
    [void](Send-ProbeCommand 2)
    Start-Sleep -Milliseconds 150
    Focus-ProbeByClick
    Move-OwnedPointer $underlay.ControlX $underlay.ControlY $underlayProcess.Id
    [NativeProbeInput]::Mouse(2); [NativeProbeInput]::Mouse(4)
    Start-Sleep -Milliseconds 150
    $observations.UnpinnedOutsideClickDismisses = ((Send-ProbeCommand 0) -band 3) -eq 2

    $uiaScript = Join-Path $PSScriptRoot 'test-bubble-uia.ps1'
    if ($NativeUiaClient) { $uiaScript = Join-Path $PSScriptRoot 'diagnostics\test-bubble-native-uia.ps1' }
    foreach ($lookupOnly in @($true, $false)) {
        $uiaResultPath = $casePath + $(if ($lookupOnly) { '.lookup.json' } else { '.uia.json' })
        [void](Send-ProbeCommand 1)
        Move-OwnedPointer $underlay.ControlX $underlay.ControlY $underlayProcess.Id
        [NativeProbeInput]::Mouse(2); [NativeProbeInput]::Mouse(4)
        Start-Sleep -Milliseconds 100
        if ([NativeProbeInput]::GetForegroundWindow() -ne $underlayHandle) { throw 'Matched UIA trial did not start with the owned underlay foreground.' }
        if ($GrantProbeForeground) {
            $grant = [NativeProbeInput]::SendMessage($underlayHandle, 0x8000+41, [UIntPtr]::new([uint64]$probeProcess.Id), [IntPtr]::Zero).ToInt64()
            if ($grant -ne 1) { throw 'Owned foreground underlay could not grant permission to the probe.' }
            $observations.OwnedProbeForegroundPermissionGranted = $true
        }
        $activationBefore = Send-ProbeCommand 9
        $uiaStart = [Diagnostics.ProcessStartInfo]::new("$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe")
        $uiaStart.UseShellExecute = $false
        $uiaStart.CreateNoWindow = $true
        foreach ($argument in @('-Mta','-NoProfile','-ExecutionPolicy','Bypass','-File',$uiaScript,'-BubbleHwnd',[string]$ready.BubbleHwnd,'-ResultPath',$uiaResultPath)) {
            $uiaStart.ArgumentList.Add($argument)
        }
        if ($lookupOnly) { $uiaStart.ArgumentList.Add('-LookupOnly') }
        if ($NativeUiaClient) { $uiaStart.ArgumentList.Add('-AutoSetFocus'); $uiaStart.ArgumentList.Add($NativeAutoSetFocus) }
        if ($NativeUiaClient -and $GrantProbeForeground) {
            foreach ($argument in @('-PermissionOwnerHwnd',[string]$underlay.Hwnd,'-ProbePid',[string]$probeProcess.Id)) { $uiaStart.ArgumentList.Add($argument) }
        }
        $uiaProcess = [Diagnostics.Process]::Start($uiaStart)
        try {
            if (-not $uiaProcess.WaitForExit(5000)) { throw 'Bubble UIA helper timed out.' }
            if ($uiaProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $uiaResultPath)) { throw 'Bubble UIA helper failed to produce a successful result.' }
            $uiaResult = Get-Content -LiteralPath $uiaResultPath -Raw | ConvertFrom-Json
            Start-Sleep -Milliseconds 100
            if ($lookupOnly) {
                $observations.BubbleUiaLookupPreservesForeground = [NativeProbeInput]::GetForegroundWindow() -eq $underlayHandle -and (Send-ProbeCommand 9) -eq $activationBefore
                $observations.BubbleUiaLookupKeepsFlyoutHidden = ((Send-ProbeCommand 0) -band 3) -eq 2
                $observations.BubbleUiaLookupClientMta = $uiaResult.Apartment -eq 'MTA'
            }
            else {
                $observations.BubbleUiaInvoke = $uiaResult.Passed -and $uiaResult.Invoked
                $observations.BubbleUiaInvokeClientMta = $uiaResult.Apartment -eq 'MTA'
                $observations.BubbleUiaOpensFlyout = [NativeProbeInput]::GetForegroundWindow() -eq $flyoutHandle
                $observations.BubbleUiaInvokeNeverActivatesBubble = (Send-ProbeCommand 9) -eq $activationBefore
            }
        }
        finally {
            if (-not $uiaProcess.HasExited) { Stop-Process -Id $uiaProcess.Id -Force }
            $uiaProcess.Dispose()
        }
    }

    [void](Send-ProbeCommand 2)
    Start-Sleep -Milliseconds 150
    Focus-ProbeByClick
    [void](Send-ProbeCommand 5)
    Send-ProbeKey 27
    $observations.EscapeFromDetailsReturnsToList = ((Send-ProbeCommand 0) -band 99) -eq 1
    [void](Send-ProbeCommand 3)
    Send-ProbeKey 27
    $observations.EscapeHidesPinnedFlyout = ((Send-ProbeCommand 0) -band 7) -eq 6
    [void](Send-ProbeCommand 4)
    [void](Send-ProbeCommand 2)
    Start-Sleep -Milliseconds 150
    Focus-ProbeByClick
    # Exercise the real native menu loop, then send a guarded Escape to cancel it.
    $menuLocation = [long]($ready.X + 80) -bor ([long]($ready.Y + 80) -shl 16)
    if (-not [NativeProbeInput]::PostMessage($flyoutHandle, [uint32]$ready.TrayCallback,
        [UIntPtr]::new([uint64]$menuLocation), [IntPtr]::new((1 -shl 16) -bor 0x7b))) { throw 'Cannot enqueue owned menu.' }
    Start-Sleep -Milliseconds 300
    $observations.NativeMenuOpened = ((Send-ProbeCommand 0) -band 8) -eq 8
    Send-ProbeKey 27
    $observations.MenuCancelPreservesFlyout = ((Send-ProbeCommand 0) -band 11) -eq 1
    if (-not $probeProcess.WaitForExit(25000)) { throw 'Probe did not complete its held input test.' }
    $probeResult = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    # WS_EX_NOACTIVATE may prevent WM_MOUSEACTIVATE delivery entirely. The
    # real foreground observation and WM_ACTIVATE count are the pass criteria.
    $observations.BubbleNoActivateStyleConfigured = $probeResult.BubbleNoActivateStyle
    $observations.BubbleNeverBecameForeground = $probeResult.BubbleForegroundActivationCount -eq 0
    $observations.ProbeExitPassed = $probeProcess.ExitCode -eq 0 -and $probeResult.Passed
    $allPassed = @($observations.Values | Where-Object { $_ -ne $true }).Count -eq 0
    [pscustomobject]@{Passed=$allPassed;Observations=$observations;Result=$resultPath} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($casePath+'.input.json') -Encoding utf8
    $observations | ConvertTo-Json
    if (-not $allPassed) { throw 'A native input assertion failed; see the recorded observations.' }
}
catch {
    [pscustomobject]@{Passed=$false;Error=$_.Exception.Message;Observations=$observations;Result=$resultPath} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath ($casePath+'.input.json') -Encoding utf8
    $observations | ConvertTo-Json
    throw
}
finally {
    [void][NativeProbeInput]::SetCursorPos($savedPointer.X,$savedPointer.Y)
    [void][NativeProbeInput]::SetThreadDpiAwarenessContext($previousDpi)
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
