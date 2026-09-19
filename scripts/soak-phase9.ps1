# Phase 9 soak — scripted churn + resource budget (PHASES.md)

param(
    [double]$Hours = 12,
    [int]$Minutes = 0,
    [int]$SampleSeconds = 60,
    [string]$LogPath = "$env:LOCALAPPDATA\ADBView\logs\soak-phase9.log"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$env:DOTNET_ROOT = if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { "$env:USERPROFILE\.dotnet" } else { $env:DOTNET_ROOT }
if ($env:DOTNET_ROOT) { $env:PATH = "$env:DOTNET_ROOT;$env:PATH" }

$duration = if ($Minutes -gt 0) { [TimeSpan]::FromMinutes($Minutes) } else { [TimeSpan]::FromHours($Hours) }

Write-Host "Phase 9 soak: hermetic churn for $($duration.TotalMinutes) min (sample every $SampleSeconds s)"
Write-Host "Log: $LogPath"
New-Item -ItemType Directory -Force -Path (Split-Path $LogPath) | Out-Null

dotnet build tests/AdbView.Tests/AdbView.Tests.csproj -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed: $LASTEXITCODE" }

$deadline = (Get-Date) + $duration
$samples = [System.Collections.Generic.List[object]]::new()
$iteration = 0
$exe = Join-Path $root "tests\AdbView.Tests\bin\Release\net10.0-windows\AdbView.Tests.exe"

function Get-ProcessSample {
    $p = Get-Process -Id $PID
    [pscustomobject]@{
        At           = Get-Date -Format o
        WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 2)
        PrivateMB    = [math]::Round($p.PrivateMemorySize64 / 1MB, 2)
        Handles      = $p.HandleCount
        Threads      = $p.Threads.Count
    }
}

Add-Content -Path $LogPath -Value "$(Get-Date -Format o) SOAK_START duration=$($duration.TotalMinutes)m sample=$SampleSeconds"
while ((Get-Date) -lt $deadline) {
    $iteration++
    $sample = Get-ProcessSample
    $samples.Add($sample)
    $line = "iter=$iteration ws=$($sample.WorkingSetMB) private=$($sample.PrivateMB) handles=$($sample.Handles) threads=$($sample.Threads)"
    Add-Content -Path $LogPath -Value "$(Get-Date -Format o) $line"
    Write-Host $line

    if ($samples.Count -ge 4) {
        $warm = $samples | Select-Object -First 3
        $medWs = ($warm | Measure-Object WorkingSetMB -Average).Average
        $medHandles = ($warm | Measure-Object Handles -Average).Average
        $cur = $samples[$samples.Count - 1]
        if ($medWs -gt 0 -and $cur.WorkingSetMB -gt ($medWs * 1.25) -and $cur.WorkingSetMB -gt 120) {
            throw "Soak failed: working set growth $($cur.WorkingSetMB) MB vs warm avg $medWs (budget 120 MB / +25%)"
        }
        if ($medHandles -gt 0 -and $cur.Handles -gt ($medHandles * 1.25) -and ($cur.Handles - $medHandles) -gt 500) {
            throw "Soak failed: handle growth $($cur.Handles) vs warm avg $medHandles"
        }
    }

    & $exe -method "*Hermetic_Churn_Soak_TwoThousand_List_Cycles" -nologo 2>&1 | Tee-Object -FilePath $LogPath -Append | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Soak churn test failed at iteration $iteration (exit $LASTEXITCODE)"
    }

    $remaining = ($deadline - (Get-Date)).TotalSeconds
    if ($remaining -le 0) { break }
    Start-Sleep -Seconds ([Math]::Min($SampleSeconds, [Math]::Max(1, [int]$remaining)))
}

Write-Host "Soak completed OK ($iteration iterations)."
Add-Content -Path $LogPath -Value "$(Get-Date -Format o) SOAK_OK iterations=$iteration"
exit 0
