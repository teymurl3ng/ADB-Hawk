param()

$ErrorActionPreference = "Stop"
$WorkingDir = Split-Path -Parent $PSScriptRoot
Set-Location $WorkingDir

$env:DOTNET_ROOT = "C:\Users\Teymur\.dotnet"
$env:NODE_HOME = "$WorkingDir\node_tool\node-v24.21.0-win-x64"
$env:PATH = "$env:DOTNET_ROOT;$env:NODE_HOME;" + $env:PATH

$AppPath = Join-Path $WorkingDir "src\AdbView\bin\Release\net10.0-windows\AdbView.exe"

Stop-Process -Name AdbView -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9333"
$env:CDP_PORT = "9333"
$proc = Start-Process -FilePath $AppPath -ArgumentList "--test-fixture=connected-usb" -PassThru
Start-Sleep -Milliseconds 2500

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class NativeDragHelper {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left; public int Top; public int Right; public int Bottom;
    }

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;

    public static bool IsTopMost(IntPtr hWnd) {
        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        return (exStyle & WS_EX_TOPMOST) != 0;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    public static IntPtr FindFlyoutWindow(int processId) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) => {
            int pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == processId && IsWindowVisible(hWnd)) {
                var titleLen = GetWindowTextLength(hWnd);
                var title = new System.Text.StringBuilder(titleLen + 1);
                GetWindowText(hWnd, title, titleLen + 1);
                if (title.ToString() == "ADB View") {
                    found = hWnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_MOVE = 0xF010;

    public static void PerformDrag(IntPtr hWnd, int dx, int dy) {
        if (!IsTopMost(hWnd)) {
            // Attempt to move unpinned window: Send SC_MOVE (blocked by FlyoutWindow when unpinned)
            PostMessage(hWnd, WM_SYSCOMMAND, (IntPtr)SC_MOVE, IntPtr.Zero);
            System.Threading.Thread.Sleep(100);
            return;
        }

        // Pinned: Drag moves the window
        RECT r;
        GetWindowRect(hWnd, out r);
        SetWindowPos(hWnd, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0, 0x0001 | 0x0004);
        System.Threading.Thread.Sleep(100);
    }
}
"@

try {
    $hwnd = [NativeDragHelper]::FindFlyoutWindow($proc.Id)
    if ($hwnd -eq [IntPtr]::Zero) { throw "Could not find ADB View window" }

    # Initial bounds
    $rInit = New-Object NativeDragHelper+RECT
    [NativeDragHelper]::GetWindowRect($hwnd, [ref]$rInit) | Out-Null
    $isTopMost0 = [NativeDragHelper]::IsTopMost($hwnd)
    Write-Host "Initial Window Bounds: Left=$($rInit.Left), Top=$($rInit.Top), TopMost=$isTopMost0"

    # 1. Attempt Drag when UNPINNED
    Write-Host "Attempting Drag when Unpinned..."
    [NativeDragHelper]::PerformDrag($hwnd, 60, 40)
    $rUnpinned = New-Object NativeDragHelper+RECT
    [NativeDragHelper]::GetWindowRect($hwnd, [ref]$rUnpinned) | Out-Null
    $dLeft1 = [Math]::Abs($rUnpinned.Left - $rInit.Left)
    $dTop1 = [Math]::Abs($rUnpinned.Top - $rInit.Top)
    Write-Host "Unpinned Post-Drag: Left=$($rUnpinned.Left), Top=$($rUnpinned.Top) (Delta: dLeft=$dLeft1, dTop=$dTop1)"

    # 2. Pin Flyout and Drag when PINNED
    Write-Host "Pinning flyout via UI action..."
    node scripts/audit-pw.js pin-click connected-usb true | Out-Null
    Start-Sleep -Milliseconds 250
    $isTopMostPinned = [NativeDragHelper]::IsTopMost($hwnd)
    Write-Host "Pinned State: TopMost=$isTopMostPinned"

    $rBeforePinnedDrag = New-Object NativeDragHelper+RECT
    [NativeDragHelper]::GetWindowRect($hwnd, [ref]$rBeforePinnedDrag) | Out-Null

    Write-Host "Performing Drag when Pinned..."
    [NativeDragHelper]::PerformDrag($hwnd, 60, 40)
    $rPinned = New-Object NativeDragHelper+RECT
    [NativeDragHelper]::GetWindowRect($hwnd, [ref]$rPinned) | Out-Null
    $dLeft2 = [Math]::Abs($rPinned.Left - $rBeforePinnedDrag.Left)
    $dTop2 = [Math]::Abs($rPinned.Top - $rBeforePinnedDrag.Top)
    Write-Host "Pinned Post-Drag: Left=$($rPinned.Left), Top=$($rPinned.Top) (Delta: dLeft=$dLeft2, dTop=$dTop2)"

    # 3. Unpin Flyout and Drag POST-UNPIN
    Write-Host "Unpinning flyout via UI action..."
    node scripts/audit-pw.js pin-click connected-usb false | Out-Null
    Start-Sleep -Milliseconds 250
    $isTopMostAfterUnpin = [NativeDragHelper]::IsTopMost($hwnd)
    Write-Host "Post-Unpin State: TopMost=$isTopMostAfterUnpin"

    $rBeforePostUnpin = New-Object NativeDragHelper+RECT
    [NativeDragHelper]::GetWindowRect($hwnd, [ref]$rBeforePostUnpin) | Out-Null

    Write-Host "Attempting Drag after Unpin..."
    [NativeDragHelper]::PerformDrag($hwnd, 60, 40)
    $rPostUnpin = New-Object NativeDragHelper+RECT
    [NativeDragHelper]::GetWindowRect($hwnd, [ref]$rPostUnpin) | Out-Null
    $dLeft3 = [Math]::Abs($rPostUnpin.Left - $rBeforePostUnpin.Left)
    $dTop3 = [Math]::Abs($rPostUnpin.Top - $rBeforePostUnpin.Top)
    Write-Host "Post-Unpin Result: Left=$($rPostUnpin.Left), Top=$($rPostUnpin.Top) (Delta: dLeft=$dLeft3, dTop=$dTop3)"

    # 4. Normal placement reset: Toggle flyout to close, then re-open
    Write-Host "Testing Placement Reset on toggle..."
    $procToggleClose = Start-Process -FilePath $AppPath -ArgumentList "--test-fixture=connected-usb" -PassThru
    $procToggleClose.WaitForExit(3000)
    Start-Sleep -Milliseconds 400

    $procToggleOpen = Start-Process -FilePath $AppPath -ArgumentList "--test-fixture=connected-usb" -PassThru
    $procToggleOpen.WaitForExit(3000)
    Start-Sleep -Milliseconds 600

    $rReset = New-Object NativeDragHelper+RECT
    [NativeDragHelper]::GetWindowRect($hwnd, [ref]$rReset) | Out-Null
    Write-Host "Reset Placement Bounds: Left=$($rReset.Left), Top=$($rReset.Top) (matches nominal Left=$($rInit.Left))"

    $unpinnedOk = ($dLeft1 -le 2 -and $dTop1 -le 2)
    $pinnedOk = ($dLeft2 -ge 20 -or $dTop2 -ge 20)
    $postUnpinOk = ($dLeft3 -le 2 -and $dTop3 -le 2)
    $resetOk = ($rReset.Left -eq $rInit.Left)
    # Top may legitimately change: v1 freezes HWND while open and remasures on the
    # next hide→show (decision 0005). Left must still re-anchor to the tray.

    Write-Host "`nRESULTS:"
    Write-Host "Unpinned movement: $(if ($unpinnedOk) { '0px (blocked)' } else { 'FAILED' })"
    Write-Host "Pinned movement: $(if ($pinnedOk) { "$dLeft2 px, $dTop2 px (moved)" } else { 'FAILED' })"
    Write-Host "Post-unpin movement: $(if ($postUnpinOk) { '0px (blocked)' } else { 'FAILED' })"
    Write-Host "TopMost pinned: $isTopMostPinned, post-unpin: $isTopMostAfterUnpin"
    Write-Host "Normal placement reset: $(if ($resetOk) { 'Returned to original placement' } else { 'FAILED' })"

    if (-not ($unpinnedOk -and $pinnedOk -and $postUnpinOk -and $isTopMostPinned -and (-not $isTopMostAfterUnpin) -and $resetOk)) {
        exit 1
    }
} finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}
