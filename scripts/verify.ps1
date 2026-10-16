# THE gate (AGENTS.md R-12). Must pass before any phase is declared complete.
# Runs: toolchain pin check, format check, full build, the C# test suite
# (unit + boundary + native composition), headed WinUI candidate fixtures,
# and the production Release publish smoke run.
# Decision 0022: the web frontend is removed; the gate is dotnet-only.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Users\Teymur\.dotnet' }
# This machine's PATH dotnet is a bare runtime without an SDK; the SDK lives in DOTNET_ROOT.
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

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

# --- 1. Toolchain pin (global.json) ---
$sdkVersion = (dotnet --version)
Assert-Condition "dotnet SDK must be 10.0.401 (found '$sdkVersion'); see global.json" { $sdkVersion -eq '10.0.401' }

Write-Host 'verify: toolchain pin ok (dotnet 10.0.401).'

# --- 2. Formatting ---
# WinUI's design-time XAML loader resolves the Core Debug implementation assembly.
# Build that metadata first so formatting validates the candidate without a missing-reference warning.
dotnet build (Join-Path $repo 'src\AdbView.Core\AdbView.Core.csproj') -c Debug
Assert-LastExitCode 'Core metadata for WinUI formatting'
dotnet format (Join-Path $repo 'ADBView.sln') --verify-no-changes
Assert-LastExitCode 'dotnet format'

# --- 3. Full build (Release solution) ---
& (Join-Path $PSScriptRoot 'build.ps1')

# --- 4. C# tests (includes the repository/source-boundary tests) ---
dotnet test (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release --no-build
Assert-LastExitCode 'dotnet test'

# --- 4b. Actual WinUI XAML/UI-thread fixtures (M04 candidate, never packaged) ---
# Missing interactive desktop/result is a failed/unexercised headed gate, not success.
$candidateExe = Join-Path $repo 'src\AdbView.WinUiMigration\bin\Release\net10.0-windows10.0.26100.0\win-x64\AdbView.WinUiMigration.exe'
& (Join-Path $PSScriptRoot 'test-native-ui.ps1') -ExecutablePath $candidateExe

# --- 5. Release publish smoke run ---
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
    (Test-Path $logFile) -and (Select-String -Path $logFile -Pattern 'AppHost starting' -Quiet)
}

Write-Host 'verify: ALL GATES GREEN.'
