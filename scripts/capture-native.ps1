param(
    [string]$Fixture = "connected-usb",
    [string]$ExecutablePath = "src\AdbView\bin\Release\net10.0-windows\AdbView.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\visual\phase-2"
)

$AppPath = (Resolve-Path -LiteralPath $ExecutablePath).Path
if (-not (Test-Path $AppPath)) {
    Write-Error "Release build not found at $AppPath"
    exit 1
}

Write-Host "Launching ADB View with fixture: $Fixture"
if ([IO.Path]::GetFileName($AppPath) -eq 'AdbView.WinUiMigration.exe') {
    if ($Fixture -notin @('empty','list','details','pairing','adb-unavailable','adb-restoring','wireless-unavailable','wireless-restoring',
        'two-devices','multi-device','many-devices','long-names','missing-metadata','unknown','unauthorized','offline','connected-wifi','text-200','list-scenarios',
        'pairing-scenarios','pairing-offer','pairing-progress','pairing-progress-reduced','pairing-success','pairing-failure','pairing-qr','discovery-health','motion-scenarios','native-scenarios','native-bubble')) { throw 'Unsupported candidate capture fixture.' }
    $captureInstance = [guid]::NewGuid().ToString('N')
    $Process = Start-Process -FilePath $AppPath -ArgumentList @("--fixture=$Fixture", "--instance=$captureInstance") -PassThru
} else {
    $Process = Start-Process -FilePath $AppPath -ArgumentList "--test-fixture=$Fixture" -PassThru
}
Write-Host "PID: $($Process.Id)"

Start-Sleep -Seconds 5

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$code = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

public class NativeCapture {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    const int SRCCOPY = 0x00CC0020;

    public static List<string> EnumerateProcessWindows(int processId) {
        var results = new List<string>();
        EnumWindows((hWnd, lParam) => {
            int pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == processId) {
                var titleLen = GetWindowTextLength(hWnd);
                var title = new StringBuilder(titleLen + 1);
                GetWindowText(hWnd, title, titleLen + 1);

                var className = new StringBuilder(256);
                GetClassName(hWnd, className, 256);

                bool visible = IsWindowVisible(hWnd);

                results.Add("HWND=0x" + hWnd.ToString("X") + " class=" + className + " title=\"" + title + "\" visible=" + visible);
            }
            return true;
        }, IntPtr.Zero);
        return results;
    }

    public static IntPtr FindFlyoutWindow(int processId, bool statusBubble) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, lParam) => {
            int pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == processId && IsWindowVisible(hWnd)) {
                var titleLen = GetWindowTextLength(hWnd);
                var title = new StringBuilder(titleLen + 1);
                GetWindowText(hWnd, title, titleLen + 1);
                if (statusBubble ? title.ToString() == "ADB View status" : title.ToString() == "ADB View" || title.ToString() == "ADB View - WinUI candidate") {
                    found = hWnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool CloseCandidate(int processId) {
        bool posted = false;
        EnumWindows((window, parameter) => {
            int pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid != processId) return true;
            var title = new StringBuilder(128);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "ADB View - WinUI candidate") return true;
            posted = PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
            return false;
        }, IntPtr.Zero);
        return posted;
    }

    public static Bitmap CaptureWindow(IntPtr hWnd) {
        RECT rect;
        GetWindowRect(hWnd, out rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        // Strategy 1: PrintWindow (captures DirectX/Acrylic if supported)
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bmp)) {
            var hdc = graphics.GetHdc();
            PrintWindow(hWnd, hdc, 2);
            graphics.ReleaseHdc(hdc);
        }

        // If PrintWindow returned transparent/black, use screen DC capture
        // This captures the composited desktop output including DWM Acrylic
        if (IsTransparentImage(bmp)) {
            bmp.Dispose();

            // Get screen coordinates of the window
            bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) {
                // GetDC(IntPtr.Zero) gets the screen DC
                var screenDc = GetDC(IntPtr.Zero);
                var memDc = CreateCompatibleDC(screenDc);
                var hBitmap = CreateCompatibleBitmap(screenDc, width, height);
                var oldBitmap = SelectObject(memDc, hBitmap);

                // BitBlt from screen at window's screen coordinates
                BitBlt(memDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SRCCOPY);

                SelectObject(memDc, oldBitmap);
                var hDest = g.GetHdc();
                BitBlt(hDest, 0, 0, width, height, memDc, 0, 0, SRCCOPY);
                g.ReleaseHdc(hDest);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        return bmp;
    }

    static bool IsTransparentImage(Bitmap bmp) {
        if (bmp.Width < 10 || bmp.Height < 10) return false;
        // Sample several points — if all are transparent/black, the capture likely failed
        int transparent = 0;
        int total = 0;
        int[,] points = { {10,10}, {bmp.Width/2, bmp.Height/2}, {bmp.Width-10, 10}, {10, bmp.Height-10} };
        for (int i = 0; i < points.GetLength(0); i++) {
            total++;
            var px = bmp.GetPixel(points[i,0], points[i,1]);
            if (px.A == 0 || (px.R == 0 && px.G == 0 && px.B == 0)) transparent++;
        }
        return transparent == total;
    }
}
"@
Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing

Write-Host "Enumerating windows for PID $($Process.Id)..."
$windowInfo = [NativeCapture]::EnumerateProcessWindows($Process.Id)
foreach ($info in $windowInfo) {
    Write-Host "  $info"
}

$hwnd = [NativeCapture]::FindFlyoutWindow($Process.Id, ($Fixture -eq 'native-bubble'))
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Error "Could not find ADB View flyout window."
    Stop-Process -Id $Process.Id -Force
    exit 1
}

Write-Host "Flyout HWND: 0x$($hwnd.ToString('X'))"

$bmp = [NativeCapture]::CaptureWindow($hwnd)
if ($bmp -eq $null) {
    Write-Error "Capture failed."
    Stop-Process -Id $Process.Id -Force
    exit 1
}

$outDir = $OutputDirectory
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$bmp.Save("$outDir\native-$Fixture.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

if ([IO.Path]::GetFileName($AppPath) -eq 'AdbView.WinUiMigration.exe') {
    if (-not [NativeCapture]::CloseCandidate($Process.Id) -or -not $Process.WaitForExit(2000)) {
        Stop-Process -Id $Process.Id -Force
        throw 'Candidate capture cleanup did not exit gracefully; forced only the owned process.'
    }
    if ($Process.ExitCode -ne 0) { throw "Candidate capture cleanup failed: exit $($Process.ExitCode)." }
} else { Stop-Process -Id $Process.Id -Force }
Write-Host "Captured native-$Fixture.png"
