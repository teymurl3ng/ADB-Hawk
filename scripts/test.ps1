# Run the C# test suite (xUnit v3 / Microsoft Testing Platform).
# scripts/verify.ps1 is the authoritative gate; this script assumes a prior build.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Users\Teymur\.dotnet' }
# This machine's PATH dotnet is a bare runtime without an SDK; the SDK lives in DOTNET_ROOT.
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "Test step '$step' failed with exit code $LASTEXITCODE."
    }
}

dotnet test (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release
Assert-LastExitCode 'dotnet test'

Write-Host 'test: C# test suite passed.'
