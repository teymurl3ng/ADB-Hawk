# Pairing / interaction stability — one-process gates.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Users\Teymur\.dotnet' }
$nodeDir = Join-Path $repo 'node_tool\node-v24.21.0-win-x64'
if (Test-Path $nodeDir) { $env:PATH = "$env:DOTNET_ROOT;$nodeDir;$env:PATH" }

function Assert-Ok([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "Interaction stability '$step' failed ($LASTEXITCODE)." }
}

Write-Host 'INTERACTION: build...'
dotnet build (Join-Path $repo 'ADBView.sln') -c Release -v q
Assert-Ok 'build'

Write-Host 'INTERACTION: pairing session / coordinator user-action gates...'
dotnet test (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') `
    -c Release --no-build `
    --filter 'FullyQualifiedName~PairingSessionUserActionTests|FullyQualifiedName~PairingCoordinatorTests' `
    -v q
Assert-Ok 'csharp-pairing'

Write-Host 'INTERACTION: PairingCard geometry vitest...'
Push-Location (Join-Path $repo 'ui')
try {
    npm.cmd test -- --run src/components/PairingCard.test.tsx
    Assert-Ok 'vitest-pairing-card'
}
finally {
    Pop-Location
}

Write-Host 'INTERACTION: Pair button Playwright hit-test / 50-cycle feedback...'
Push-Location (Join-Path $repo 'ui')
try {
    node (Join-Path (Get-Location) 'node_modules\@playwright\test\cli.js') test --project=pairing
    Assert-Ok 'playwright-pairing'
}
finally {
    Pop-Location
}

Write-Host 'INTERACTION: ALL GATES GREEN.'
