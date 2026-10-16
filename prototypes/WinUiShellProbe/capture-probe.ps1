# Invoke from Windows PowerShell 5.1 for the existing System.Drawing capture helper.
$ErrorActionPreference = 'Stop'
$probeRepo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location -LiteralPath $probeRepo
$helperSource = Get-Content -LiteralPath (Join-Path $probeRepo 'scripts\capture-native.ps1') -Raw -Encoding utf8
$captureMatch = [regex]::Match($helperSource, '\$code\s*=\s*@"\r?\n(.*?)\r?\n"@', [Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $captureMatch.Success) { throw 'The native capture helper changed; recheck its C# source before running.' }
$captureCode = $captureMatch.Groups[1].Value
$probeTitlePredicate = 'title.ToString() == "ADB View ' + [char]0x2014 + ' isolated WinUI feasibility probe"'
if (-not $captureCode.Contains('title.ToString() == "ADB View"')) { throw 'Unexpected capture window predicate.' }
$captureCode = $captureCode.Replace('title.ToString() == "ADB View"', $probeTitlePredicate)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition $captureCode -ReferencedAssemblies System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ProbeCaptureDpi {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
$previousDpi = [ProbeCaptureDpi]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($previousDpi -eq [IntPtr]::Zero) { throw 'Cannot enter the capture DPI context.' }
$captureProcess = $null
try {
    $captureExe = Join-Path $probeRepo 'artifacts\winui-rebuild\probe-publish\WinUiShellProbe.exe'
    $captureProcess = Start-Process -FilePath $captureExe -ArgumentList '--native --pinned' -PassThru
    Start-Sleep -Seconds 5
    $captureWindow = [NativeCapture]::FindFlyoutWindow($captureProcess.Id)
    if ($captureWindow -eq [IntPtr]::Zero) { throw 'The isolated probe flyout is not visible.' }
    $bitmap = [NativeCapture]::CaptureWindow($captureWindow)
    if ($null -eq $bitmap) { throw 'The isolated native capture failed.' }
    try {
        $outputPath = Join-Path $probeRepo 'artifacts\winui-rebuild\m02\native-winui-native-lifetime-probe.png'
        $bitmap.Save($outputPath, [Drawing.Imaging.ImageFormat]::Png)
        Write-Output $outputPath
    }
    finally { $bitmap.Dispose() }
}
finally {
    if ($null -ne $captureProcess) {
        if (-not $captureProcess.HasExited) {
            [void]$captureProcess.CloseMainWindow()
            if (-not $captureProcess.WaitForExit(4000)) { Stop-Process -Id $captureProcess.Id -Force }
        }
        $captureProcess.Dispose()
    }
    [void][ProbeCaptureDpi]::SetThreadDpiAwarenessContext($previousDpi)
}
