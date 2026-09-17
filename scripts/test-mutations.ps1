param()

$ErrorActionPreference = "Stop"
$WorkingDir = $PSScriptRoot | Split-Path -Parent
Set-Location $WorkingDir

$env:DOTNET_ROOT = "C:\Users\Teymur\.dotnet"
$env:NODE_HOME = "$WorkingDir\node_tool\node-v24.21.0-win-x64"
$env:PATH = "$env:DOTNET_ROOT;$env:NODE_HOME;" + $env:PATH

$Mutations = @(
    @{
        Id = "A"
        Name = "USB expansion disabled"
        TargetFixture = "connected-usb"
        TargetFile = "ui/src/App.tsx"
        ExpectedFailure = "USB expand test (detailHeight > 0, height increases)"
    },
    @{
        Id = "B"
        Name = "Chevron/details mismatch"
        TargetFixture = "connected-usb"
        TargetFile = "ui/src/components/DeviceCard.tsx"
        ExpectedFailure = "visible detail invariant (expanded chevron with 0 height)"
    },
    @{
        Id = "C"
        Name = "Shared expansion state"
        TargetFixture = "multi-device"
        TargetFile = "ui/src/App.tsx"
        ExpectedFailure = "multi-device independence test"
    },
    @{
        Id = "D"
        Name = "Light scrollbar in dark mode"
        TargetFixture = "multi-device"
        TargetFile = "ui/src/App.tsx"
        ExpectedFailure = "dark scrollbar visual check"
    },
    @{
        Id = "E"
        Name = "Pin TopMost broken"
        TargetFixture = "connected-usb"
        TargetFile = "src/AdbView/Host/FlyoutWindow.cs"
        ExpectedFailure = "native pin TopMost synchronization"
    },
    @{
        Id = "F"
        Name = "connected-usb reporting Wi-Fi"
        TargetFixture = "connected-usb"
        TargetFile = "ui/src/App.tsx"
        ExpectedFailure = "fixture semantic invariant (must be USB)"
    },
    @{
        Id = "G"
        Name = "Opaque body in Acrylic"
        TargetFixture = "connected-usb"
        TargetFile = "ui/src/styles/app.css"
        ExpectedFailure = "Acrylic transparency assertion"
    },
    @{
        Id = "H"
        Name = "Scrolling header"
        TargetFixture = "multi-device"
        TargetFile = "ui/src/App.tsx"
        ExpectedFailure = "fixed-header check during scroll"
    },
    @{
        Id = "I"
        Name = "Invisible expanded details"
        TargetFixture = "connected-usb"
        TargetFile = "ui/src/components/DeviceCard.tsx"
        ExpectedFailure = "visible detail invariant (expanded chevron with 0 height)"
    },
    @{
        Id = "J"
        Name = "Broken copy feedback"
        TargetFixture = "connected-usb"
        TargetFile = "ui/src/components/DeviceCard.tsx"
        ExpectedFailure = "missing visible copy feedback badge"
    }
)

$MutationResults = @()

function Rebuild-Product {
    & "$WorkingDir\scripts\build.ps1" | Out-Null
}

foreach ($m in $Mutations) {
    Write-Host "`n=========================================================="
    Write-Host "TESTING MUTATION $($m.Id): $($m.Name)"
    Write-Host "Target Fixture:   $($m.TargetFixture)"
    Write-Host "Target File:      $($m.TargetFile)"
    Write-Host "Expected Failure: $($m.ExpectedFailure)"
    Write-Host "=========================================================="

    $filePath = Join-Path $WorkingDir $m.TargetFile
    $bakPath = "$filePath.mutbak"
    Copy-Item $filePath $bakPath -Force

    try {
        $content = Get-Content $filePath -Raw

        # Apply mutation
        switch ($m.Id) {
            "A" {
                # Disable expansion in App.tsx
                $mutated = $content -replace "setFixture\('expanded'\);", "// disabled expansion"
                $mutated = $mutated -replace "setExpandedIds\(.*?\);", "// disabled expansion"
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
            "B" {
                # Force details hidden while chevron is expanded
                $mutated = $content -replace '<div className="mt-2.5 pt-2.5 border-t border-win-borderSubtle space-y-2"', '<div className="mt-2.5 pt-2.5 border-t border-win-borderSubtle space-y-2" style={{ display: "none" }}'
                $mutated = $mutated -replace '<div className="mt-2.5 pt-2.5 border-t border-white/\[0.06\] space-y-2"', '<div className="mt-2.5 pt-2.5 border-t border-white/[0.06] space-y-2" style={{ display: "none" }}'
                $mutated = $mutated -replace '<div data-testid="device-card-details"', '<div data-testid="device-card-details" style={{ display: "none" }}'
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
            "C" {
                # Make all cards share expansion in multi-device
                $mutated = $content -replace "onToggleExpand=\{\(\) => handleToggleExpand\(d\.id\)\}", "onToggleExpand={() => { setExpandedIds(new Set(['d1','d2','d3','d4','d5','d6'])); }}"
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
            "D" {
                # Force light scrollbar on container
                $mutated = $content -replace 'className="flex-1 overflow-y-auto space-y-3 pb-2"', 'className="flex-1 overflow-y-auto space-y-3 pb-2" style={{ colorScheme: "light" }}'
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
            "E" {
                # Break native TopMost synchronization
                $mutated = $content -replace "TopMost = value;", "// TopMost = value;"
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
            "F" {
                # Make connected-usb report Wi-Fi
                $mutated = $content -replace "transport: 'usb',", "transport: 'wifi',"
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
            "G" {
                # Make body opaque in Acrylic
                $mutated = $content + "`nbody { background-color: #202329 !important; }`n"
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
            "H" {
                # Move header inside scroll container so it scrolls
                $mutated = $content.Replace("<Header isPinned={isPinned} />", "")
                $mutated = $mutated.Replace("{fixture === 'empty' && <EmptyState />}", "<Header isPinned={isPinned} />`n        {fixture === 'empty' && <EmptyState />}")
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
            "I" {
                # Force expanded details region to display: none while keeping chevron expanded
                $mutated = $content -replace '<div\s+data-testid="device-card-details"', '<div data-testid="device-card-details" style={{ display: "none" }}'
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
            "J" {
                # Suppress "Copied" feedback badge from appearing
                $mutated = $content -replace 'data-testid="copied-badge"', 'data-testid="disabled-copied-badge" style={{ display: "none" }}'
                Set-Content $filePath -Value $mutated -Encoding UTF8
            }
        }

        Rebuild-Product

        # Run audit on the target fixture
        $exitCode = 0
        powershell -ExecutionPolicy Bypass -File scripts/audit-phase2.ps1 -FixtureFilter $m.TargetFixture -SkipLongPin
        $exitCode = $LASTEXITCODE

        $detected = ($exitCode -ne 0)

        Write-Host "`n>>> Mutation $($m.Id) Detection: ExitCode = $exitCode, Detected = $detected <<<" -ForegroundColor $(if ($detected) { "Green" } else { "Red" })

        $MutationResults += [PSCustomObject]@{
            Mutation = $m.Name
            TargetFixture = $m.TargetFixture
            ExpectedDetection = $m.ExpectedFailure
            Detected = $(if ($detected) { "YES" } else { "NO" })
            NonZeroExit = $(if ($exitCode -ne 0) { "YES" } else { "NO" })
            Reverted = "YES"
            ExitCode = $exitCode
        }
    }
    finally {
        # Always restore backup
        Copy-Item $bakPath $filePath -Force
        Remove-Item $bakPath -Force -ErrorAction SilentlyContinue
        Rebuild-Product
    }
}

# ==========================================================
# WRITE MUTATION REPORT (artifacts/phase2-audit/mutation-report.md)
# ==========================================================
$ReportPath = Join-Path $WorkingDir "artifacts\phase2-audit\mutation-report.md"
$md = @"
# Phase 2 Mutation Testing Report

| Mutation | Target fixture | Expected detection | Detected | Non-zero exit | Reverted |
|---|---|---|---|---|---|
"@

foreach ($r in $MutationResults) {
    $md += "`n| $($r.Mutation) | $($r.TargetFixture) | $($r.ExpectedDetection) | $($r.Detected) | $($r.NonZeroExit) | $($r.Reverted) |"
}

$md += "`n"
Set-Content -Path $ReportPath -Value $md -Encoding UTF8
Write-Host "`nMutation report written to $ReportPath" -ForegroundColor Green

$allDetected = ($MutationResults | Where-Object { $_.Detected -ne "YES" }).Count -eq 0
if ($allDetected) {
    Write-Host "ALL MUTATIONS DETECTED ($($MutationResults.Count)/$($MutationResults.Count)). Hardened audit suite successfully verified!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "SOME MUTATIONS WERE NOT DETECTED!" -ForegroundColor Red
    exit 1
}
