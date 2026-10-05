# Build the whole product: the .NET solution (decision 0022: no frontend step).
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Users\Teymur\.dotnet' }

function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "Build step '$step' failed with exit code $LASTEXITCODE."
    }
}

# .NET solution, Release, warnings-as-errors (enforced by Directory.Build.props).
dotnet build (Join-Path $repo 'ADBView.sln') -c Release
Assert-LastExitCode 'dotnet build'

Write-Host 'build: Release solution build complete.'
