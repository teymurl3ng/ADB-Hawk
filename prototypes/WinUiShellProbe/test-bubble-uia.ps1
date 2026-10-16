param([Parameter(Mandatory)][long]$BubbleHwnd, [Parameter(Mandatory)][string]$ResultPath, [switch]$LookupOnly)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BubbleUiaForeground {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$stages = New-Object 'System.Collections.Generic.List[object]'
function Record-UiaStage([string]$Name) {
    $marker = [pscustomobject]@{Stage=$Name;Qpc=[Diagnostics.Stopwatch]::GetTimestamp();Foreground=[BubbleUiaForeground]::GetForegroundWindow().ToInt64()}
    $stages.Add($marker)
    $marker | ConvertTo-Json -Compress | Add-Content -LiteralPath ($ResultPath+'.stages.jsonl') -Encoding utf8
}
$foregroundBefore = [BubbleUiaForeground]::GetForegroundWindow().ToInt64()
Record-UiaStage 'LookupStart'
$bubbleRoot = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]::new($BubbleHwnd))
Record-UiaStage 'RootFound'
$condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Open ADB View probe')
$bubbleButton = $bubbleRoot.FindFirst([Windows.Automation.TreeScope]::Subtree, $condition)
if ($null -eq $bubbleButton) { throw 'The visible bubble exposes no named UIA action.' }
Record-UiaStage 'NamedElementFound'
$cachedName = $bubbleButton.Current.Name
$cachedControlType = $bubbleButton.Current.ControlType.ProgrammaticName
Record-UiaStage 'PropertiesCached'
$invokePattern = $bubbleButton.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
Record-UiaStage 'PatternAcquired'
$foregroundBeforeInvoke = [BubbleUiaForeground]::GetForegroundWindow().ToInt64()
if ($null -eq $invokePattern) { throw 'The bubble exposes no Invoke pattern.' }
if (-not $LookupOnly) {
    Record-UiaStage 'BeforeInvoke'
    $invokePattern.Invoke()
    Record-UiaStage 'InvokeReturned'
}
Record-UiaStage 'ClientComplete'
@{Passed=$true;Name=$cachedName;ControlType=$cachedControlType;Invoked=(-not $LookupOnly);ForegroundBefore=$foregroundBefore;ForegroundBeforeInvoke=$foregroundBeforeInvoke;Apartment=[Threading.Thread]::CurrentThread.GetApartmentState().ToString();ClientPid=$PID;Frequency=[Diagnostics.Stopwatch]::Frequency;Stages=@($stages.ToArray())} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultPath -Encoding utf8
