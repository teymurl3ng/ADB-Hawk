param([Parameter(Mandatory)][string]$ClickLog, [Parameter(Mandatory)][string]$ReadyFile)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UnderlayDpi {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
public sealed class ProbeInputUnderlay : System.Windows.Forms.Form {
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError=true)] private static extern bool AllowSetForegroundWindow(uint process);
    protected override void WndProc(ref System.Windows.Forms.Message message) {
        if (message.Msg == 0x8000 + 41) {
            uint processId = checked((uint)message.WParam.ToInt64());
            using (var target = System.Diagnostics.Process.GetProcessById(checked((int)processId))) {
                bool ownedProbe = string.Equals(target.ProcessName, "WinUiShellProbe", StringComparison.Ordinal);
                message.Result = ownedProbe && GetForegroundWindow() == Handle && AllowSetForegroundWindow(processId) ? new IntPtr(1) : IntPtr.Zero;
            }
            return;
        }
        base.WndProc(ref message);
    }
}
'@
$underlayDpi = [UnderlayDpi]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($underlayDpi -eq [IntPtr]::Zero) { throw 'Synthetic underlay DPI context unavailable.' }
$underlayForm = New-Object ProbeInputUnderlay
$underlayForm.Text = 'ADB View synthetic input-test underlay'
$underlayForm.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$underlayForm.Location = New-Object System.Drawing.Point(800,200)
$underlayForm.Size = New-Object System.Drawing.Size(1120,850)
$underlayForm.BackColor = [System.Drawing.Color]::Magenta
$underlayForm.Add_MouseDown({ [System.IO.File]::AppendAllText($ClickLog, 'underlay-click' + [Environment]::NewLine) })
$underlayForm.Add_Shown({
    $controlPoint = $underlayForm.PointToScreen((New-Object System.Drawing.Point(100,100)))
    $readyData = @{Hwnd=$underlayForm.Handle.ToInt64();ControlX=$controlPoint.X;ControlY=$controlPoint.Y} | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($ReadyFile, $readyData)
})
try { [System.Windows.Forms.Application]::Run($underlayForm) }
finally { $underlayForm.Dispose(); [void][UnderlayDpi]::SetThreadDpiAwarenessContext($underlayDpi) }
