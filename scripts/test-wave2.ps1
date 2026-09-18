# Phase 3 Wave 2 Real WebView2 Integration Test
param()

$ErrorActionPreference = "Stop"
$WorkingDir = Split-Path -Parent $PSScriptRoot
Set-Location $WorkingDir

$env:DOTNET_ROOT = "C:\Users\Teymur\.dotnet"
$env:NODE_HOME = "$WorkingDir\node_tool\node-v24.21.0-win-x64"
$env:PATH = "$env:DOTNET_ROOT;$env:NODE_HOME;" + $env:PATH

$AppPath = Join-Path $WorkingDir "src\AdbView\bin\Release\net10.0-windows\AdbView.exe"

Stop-Process -Name AdbView -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9333"
$env:CDP_PORT = "9333"

$sw = [System.Diagnostics.Stopwatch]::StartNew()

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class NativeWave2Helper {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left; public int Top; public int Right; public int Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
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
            if (pid == processId) {
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
}
"@

$proc = Start-Process -FilePath $AppPath -ArgumentList "--test-fixture=connected-usb" -PassThru
Start-Sleep -Milliseconds 2500

$passed = 0
$total = 13

function Run-Check([string]$name, [scriptblock]$body) {
    Write-Host -NoNewline "[$name] ... "
    try {
        & $body
        Write-Host "PASSED" -ForegroundColor Green
        $global:passed++
    }
    catch {
        Write-Host "FAILED: $_" -ForegroundColor Red
        throw
    }
}

try {
    $hwnd = [NativeWave2Helper]::FindFlyoutWindow($proc.Id)
    if ($hwnd -eq [IntPtr]::Zero) { throw "Could not find ADB View window for process $($proc.Id)" }

    # A. ready -> snapshot
    Run-Check "A. Ready Handshake -> Snapshot" {
        $raw = node scripts/test-wave2.js wait-ready
        $res = $raw | ConvertFrom-Json
        if (-not $res.success -or -not $res.snapshot) { throw "No snapshot received" }
        if ($res.snapshot.protocol -ne 1) { throw "Protocol $($res.snapshot.protocol) != 1" }
        if ($res.snapshot.revision -ne 1) { throw "Initial revision $($res.snapshot.revision) != 1" }
        if ($res.snapshot.shell.pinned -ne $false) { throw "Initial pin state is not false" }
    }

    # B. duplicate ready harmless & protocol mismatch rejected
    Run-Check "B. Duplicate Ready Idempotent & Protocol Mismatch" {
        $raw = node scripts/test-wave2.js send-ready
        $res = $raw | ConvertFrom-Json
        if (-not $res.success) { throw "Duplicate ready failed" }
        if ($res.data.snapshot.revision -ne 1) { throw "Revision mutated on duplicate ready" }

        $rawInvalid = node scripts/test-wave2.js send-invalid-ready
        $resInvalid = $rawInvalid | ConvertFrom-Json
        if (-not $resInvalid.rejected) { throw "Invalid protocol ready was not rejected" }
    }

    # C. pin round trip
    Run-Check "C. Pin Round Trip (UI -> HWND TopMost -> Snapshot Rev -> UI State)" {
        $topMostBefore = [NativeWave2Helper]::IsTopMost($hwnd)
        if ($topMostBefore) { throw "HWND was unexpectedly TopMost before pin" }

        $raw = node scripts/test-wave2.js click-pin
        $res = $raw | ConvertFrom-Json
        if (-not $res.success) { throw "Pin click failed" }

        $topMostAfter = [NativeWave2Helper]::IsTopMost($hwnd)
        if (-not $topMostAfter) { throw "HWND did not become TopMost after pin" }

        if ($res.data.snapshot.revision -ne 2) { throw "Revision is $($res.data.snapshot.revision), expected 2" }
        if ($res.data.snapshot.shell.pinned -ne $true) { throw "Snapshot shell.pinned is not true" }
        if ($res.data.buttonTitle -ne "Unpin flyout") { throw "Button title is '$($res.data.buttonTitle)', expected 'Unpin flyout'" }
        if ($res.data.actionCount.pin -ne 1) { throw "Action count for pin is not 1" }
    }

    # D. unpin round trip
    Run-Check "D. Unpin Round Trip (UI -> HWND Normal -> Snapshot Rev -> UI State)" {
        $raw = node scripts/test-wave2.js click-pin
        $res = $raw | ConvertFrom-Json
        if (-not $res.success) { throw "Unpin click failed" }

        $topMostAfter = [NativeWave2Helper]::IsTopMost($hwnd)
        if ($topMostAfter) { throw "HWND remained TopMost after unpin" }

        if ($res.data.snapshot.revision -ne 3) { throw "Revision is $($res.data.snapshot.revision), expected 3" }
        if ($res.data.snapshot.shell.pinned -ne $false) { throw "Snapshot shell.pinned is not false" }
        if ($res.data.buttonTitle -ne "Pin flyout") { throw "Button title is '$($res.data.buttonTitle)', expected 'Pin flyout'" }
        if ($res.data.actionCount.unpin -ne 1) { throw "Action count for unpin is not 1" }
    }

    # E. stale snapshot rejected
    # F. duplicate revision rejected
    # G. newer revision accepted
    # H. malformed snapshot rejected
    Run-Check "E-H. Ordering & Malformed Rejection" {
        $raw = node scripts/test-wave2.js test-ordering
        $res = $raw | ConvertFrom-Json
        if (-not $res.success) { throw "Ordering test failed" }
        if (-not $res.results.staleRejected) { throw "Stale snapshot was not rejected" }
        if (-not $res.results.duplicateRejected) { throw "Duplicate revision was not rejected" }
        if (-not $res.results.malformedRejected) { throw "Malformed snapshot was not rejected" }
        if (-not $res.results.newerAccepted) { throw "Newer snapshot was not accepted" }
    }

    # I. requestDismiss semantics
    Run-Check "I. RequestDismiss Semantics (Pinned Ignored / Unpinned Hides)" {
        # 1. When pinned, Escape must NOT hide the window
        node scripts/test-wave2.js click-pin | Out-Null
        $isPinnedTopMost = [NativeWave2Helper]::IsTopMost($hwnd)
        if (-not $isPinnedTopMost) { throw "Window is not TopMost before pinned dismiss test" }

        node scripts/test-wave2.js press-escape | Out-Null
        Start-Sleep -Milliseconds 200
        $visibleWhenPinned = [NativeWave2Helper]::IsWindowVisible($hwnd)
        if (-not $visibleWhenPinned) { throw "Pinned window hid on requestDismiss!" }

        # 2. When unpinned, Escape MUST hide the window
        node scripts/test-wave2.js click-pin | Out-Null
        node scripts/test-wave2.js press-escape | Out-Null
        Start-Sleep -Milliseconds 300
        $visibleWhenUnpinned = [NativeWave2Helper]::IsWindowVisible($hwnd)
        if ($visibleWhenUnpinned) { throw "Unpinned window remained visible after requestDismiss!" }

        # Reopen window for remaining tests
        $reopenProc = Start-Process -FilePath $AppPath -ArgumentList "--test-fixture=connected-usb" -PassThru
        $reopenProc.WaitForExit(3000)
        Start-Sleep -Milliseconds 500
        $visibleAfterReopen = [NativeWave2Helper]::IsWindowVisible($hwnd)
        if (-not $visibleAfterReopen) { throw "Window failed to reopen" }
    }

    # J. content-size clamp + duplicate suppression
    Run-Check "J. Content-Size Native Clamping & Duplicate Suppression" {
        $rBefore = New-Object NativeWave2Helper+RECT
        [NativeWave2Helper]::GetWindowRect($hwnd, [ref]$rBefore) | Out-Null

        # Report 50 (below 120): should clamp to 120 effective px
        node scripts/test-wave2.js report-size 50 | Out-Null
        Start-Sleep -Milliseconds 200
        $rMin = New-Object NativeWave2Helper+RECT
        [NativeWave2Helper]::GetWindowRect($hwnd, [ref]$rMin) | Out-Null
        if ($rMin.Height -lt 118) { throw "Height $($rMin.Height) clamped below minimum 120" }

        # Report 900 (above 560): should clamp to 560 effective px
        node scripts/test-wave2.js report-size 900 | Out-Null
        Start-Sleep -Milliseconds 200
        $rMax = New-Object NativeWave2Helper+RECT
        [NativeWave2Helper]::GetWindowRect($hwnd, [ref]$rMax) | Out-Null
        if ($rMax.Height -gt 570) { throw "Height $($rMax.Height) exceeded maximum 560" }

        # Duplicate suppression: report 350 twice, bounds should remain exact
        node scripts/test-wave2.js report-size 350 | Out-Null
        Start-Sleep -Milliseconds 200
        $rMid1 = New-Object NativeWave2Helper+RECT
        [NativeWave2Helper]::GetWindowRect($hwnd, [ref]$rMid1) | Out-Null

        node scripts/test-wave2.js report-size 350 | Out-Null
        Start-Sleep -Milliseconds 200
        $rMid2 = New-Object NativeWave2Helper+RECT
        [NativeWave2Helper]::GetWindowRect($hwnd, [ref]$rMid2) | Out-Null
        if ($rMid1.Height -ne $rMid2.Height -or $rMid1.Top -ne $rMid2.Top) { throw "Duplicate size reports caused bounds drift" }
    }

    # K. reload/recovery restores current snapshot
    Run-Check "K. Reload Recovery Restores Authoritative Native Snapshot" {
        # Ensure pinned first
        $state = node scripts/test-wave2.js get-state | ConvertFrom-Json
        if (-not $state.data.snapshot.shell.pinned) {
            node scripts/test-wave2.js click-pin | Out-Null
        }

        $revBefore = (node scripts/test-wave2.js get-state | ConvertFrom-Json).data.snapshot.revision

        # Reload WebView
        $rawReload = node scripts/test-wave2.js reload-page
        $resReload = $rawReload | ConvertFrom-Json
        if (-not $resReload.success) { throw "Reload failed" }

        if (-not $resReload.data.snapshot.shell.pinned) { throw "Pin state lost after reload" }
        if ($resReload.data.snapshot.revision -ne $revBefore) { throw "Revision lost after reload ($($resReload.data.snapshot.revision) != $revBefore)" }
        if ($resReload.data.buttonTitle -ne "Unpin flyout") { throw "UI button title not pinned after reload" }
        $topMostAfterReload = [NativeWave2Helper]::IsTopMost($hwnd)
        if (-not $topMostAfterReload) { throw "HWND lost TopMost after reload" }
    }

    # L. listener/action registration does not duplicate after reload/remount
    Run-Check "L. No Listener or Action Duplication After Reload" {
        $rawUnpin = node scripts/test-wave2.js click-pin
        $resUnpin = $rawUnpin | ConvertFrom-Json
        if (-not $resUnpin.success) { throw "Post-reload pin click failed" }

        # Action count on the newly reloaded page must be exactly 1
        if ($resUnpin.data.actionCount.unpin -ne 1) {
            throw "Duplicate unpin actions detected: $($resUnpin.data.actionCount.unpin)"
        }

        $topMostPost = [NativeWave2Helper]::IsTopMost($hwnd)
        if ($topMostPost) { throw "HWND remained TopMost after single post-reload unpin click" }
    }

    # M. test-only JS action counter unavailable on normal Release launch, active in test fixture
    Run-Check "M. Test Instrumentation Gating (Production Clean / Fixture Active)" {
        $raw = node scripts/test-wave2.js test-instrumentation-gating
        $res = $raw | ConvertFrom-Json
        if (-not $res.success) { throw "Instrumentation gating test failed" }
        if (-not $res.fixtureMode.hasActionCount) { throw "Action counter was unavailable in test fixture mode" }
        if ($res.normalMode.hasActionCount) { throw "Action counter leaked into normal production launch" }
        if ($res.normalMode.hasSnapshot) { throw "Snapshot global leaked into normal production launch" }
        if ($res.normalMode.hasSnapshotCount) { throw "Snapshot count global leaked into normal production launch" }
    }

    $sw.Stop()
    Write-Host "`nAll Wave 2 real WebView2 integration tests PASSED ($($sw.Elapsed.TotalSeconds.ToString('F2')) s)." -ForegroundColor Green
}
finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}
