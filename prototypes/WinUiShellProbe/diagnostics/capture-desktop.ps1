param([Parameter(Mandatory)][long]$Window, [Parameter(Mandatory)][int]$TargetProcess,
    [Parameter(Mandatory)][string]$OutputPrefix, [int]$Seconds=12, [long]$UnderlayWindow=0, [int]$UnderlayProcess=0)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'DesktopFrames.cs') -CompilerOptions '/unsafe'
[DesktopFrames]::Capture($Window,$TargetProcess,$OutputPrefix,$Seconds,$UnderlayWindow,$UnderlayProcess)
