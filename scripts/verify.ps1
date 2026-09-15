# THE gate (AGENTS.md R-12). Must pass before any phase is declared complete.
# Runs: toolchain pin check, format check, full build, both test suites,
# frontend lint/format/typecheck, contract regeneration check, visual check,
# and the Release publish smoke run.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "Gate step '$step' failed with exit code $LASTEXITCODE."
    }
}

function Assert-Condition([string]$description, [scriptblock]$condition) {
    if (-not (& $condition)) {
        throw "Gate step failed: $description"
    }
}

# --- 1. Toolchain pins (global.json, .node-version, ui/package.json packageManager) ---
$sdkVersion = (dotnet --version)
Assert-Condition "dotnet SDK must be 10.0.401 (found '$sdkVersion'); see global.json" { $sdkVersion -eq '10.0.401' }

$nodePin = (Get-Content -Raw (Join-Path $repo '.node-version')).Trim()
$nodeVersion = (& node --version)
Assert-Condition "Node $nodePin is required (found '$nodeVersion'); see .node-version" { $nodeVersion -eq "v$nodePin" }

$packageJson = Get-Content -Raw (Join-Path $repo 'ui\package.json') | ConvertFrom-Json
$npmPin = $packageJson.packageManager -replace '^npm@', ''
$npmVersion = (& npm.cmd --version)
Assert-Condition "npm $npmPin is required (found '$npmVersion'); Node $nodePin bundles it" { $npmVersion -eq $npmPin }

Write-Host 'verify: toolchain pins ok (dotnet 10.0.401, node 24.21.0, npm 11.19.0).'

# --- 2. Formatting ---
dotnet format (Join-Path $repo 'ADBView.sln') --verify-no-changes
Assert-LastExitCode 'dotnet format'

# --- 3. Full build (frontend bundle -> wwwroot -> Release solution) ---
& (Join-Path $PSScriptRoot 'build.ps1')

# --- 4. C# tests (includes the repository/source-boundary tests) ---
dotnet test (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release --no-build
Assert-LastExitCode 'dotnet test'

# --- 5. Frontend gates: eslint, prettier, tsc, vitest ---
Push-Location (Join-Path $repo 'ui')
try {
    npm.cmd run lint
    Assert-LastExitCode 'eslint'

    npm.cmd run format:check
    Assert-LastExitCode 'prettier'

    npm.cmd run typecheck
    Assert-LastExitCode 'tsc'

    npm.cmd run test
    Assert-LastExitCode 'vitest'
}
finally {
    Pop-Location
}

# --- 6. Contract regeneration check (no generated contract until Phase 3) ---
& (Join-Path $PSScriptRoot 'gen-contract.ps1')

# --- 7. Visual regression (no specs/baselines until Phase 2) ---
& (Join-Path $PSScriptRoot 'visual.ps1')

# --- 8. Release publish smoke run ---
$smoke = Join-Path $repo 'artifacts\smoke'
if (Test-Path -LiteralPath $smoke) {
    Remove-Item -LiteralPath $smoke -Recurse -Force
}
dotnet publish (Join-Path $repo 'src\AdbView\AdbView.csproj') -c Release -o $smoke
Assert-LastExitCode 'dotnet publish'

$exe = Join-Path $smoke 'AdbView.exe'

# The smoke exe is framework-dependent: make it resolve the same dotnet
# installation that built it (matters when the SDK is installed per-user).
$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$env:DOTNET_ROOT = Split-Path -Parent $dotnetCommand.Source

# WinExe is GUI-subsystem: PowerShell does not wait on the call operator, so
# wait explicitly to avoid racing the log write.
$smokeProcess = Start-Process -FilePath $exe -Wait -PassThru
if ($smokeProcess.ExitCode -ne 0) {
    throw "Gate step 'publish smoke exe' failed with exit code $($smokeProcess.ExitCode)."
}

$logFile = Join-Path $env:LOCALAPPDATA "ADBView\logs\adbview-$(Get-Date -Format yyyyMMdd).log"
Assert-Condition "smoke run must write its startup line to $logFile" {
    (Test-Path $logFile) -and (Select-String -Path $logFile -Pattern 'phase 0 skeleton started' -Quiet)
}

Write-Host 'verify: ALL GATES GREEN.'
