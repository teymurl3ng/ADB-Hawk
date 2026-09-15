# Run both test suites: C# (xUnit v3 / Microsoft Testing Platform) and Vitest.
# scripts/verify.ps1 is the authoritative gate; this script assumes a prior build.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "Test step '$step' failed with exit code $LASTEXITCODE."
    }
}

# The boundary tests inspect the built frontend output.
if (-not (Test-Path (Join-Path $repo 'ui\dist\index.html'))) {
    throw 'ui/dist is missing; run scripts\build.ps1 first (the boundary tests inspect the built frontend).'
}

dotnet test (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release
Assert-LastExitCode 'dotnet test'

Push-Location (Join-Path $repo 'ui')
try {
    npm.cmd run test
    Assert-LastExitCode 'vitest'
}
finally {
    Pop-Location
}

Write-Host 'test: C# and frontend test suites passed.'
