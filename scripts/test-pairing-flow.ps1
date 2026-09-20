# Pairing flow gate — ONE process per suite, repeated scenarios.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Users\Teymur\.dotnet' }
$nodeDir = Join-Path $repo 'node_tool\node-v24.21.0-win-x64'
if (Test-Path $nodeDir) { $env:PATH = "$env:DOTNET_ROOT;$nodeDir;$env:PATH" }

function Assert-Ok([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "Pairing flow '$step' failed ($LASTEXITCODE)." }
}

Write-Host 'PAIRING_FLOW: build...'
dotnet build (Join-Path $repo 'ADBView.sln') -c Release -v q
Assert-Ok 'build'

Write-Host 'PAIRING_FLOW: C# pairing session / coordinator / header / freeze...'
dotnet test (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') `
    -c Release --no-build `
    --filter 'FullyQualifiedName~PairingSessionUserActionTests|FullyQualifiedName~PairingCoordinatorTests|FullyQualifiedName~HeaderDragNativeProbeTests|FullyQualifiedName~FrozenVisibleBoundsTests' `
    -v q
Assert-Ok 'csharp'

Write-Host 'PAIRING_FLOW: vitest presentation + PairingCard...'
Push-Location (Join-Path $repo 'ui')
try {
    npm.cmd test -- --run `
        src/bridge/pairingPresentation.test.ts `
        src/components/PairingCard.test.tsx
    Assert-Ok 'vitest'
}
finally {
    Pop-Location
}

Write-Host 'PAIRING_FLOW: Playwright pairing project (button + motion)...'
Push-Location (Join-Path $repo 'ui')
try {
    npx.cmd playwright test --project=pairing
    Assert-Ok 'playwright-pairing'
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'PAIRING_FLOW: repeated scenarios covered by suites above:'
Write-Host '  offer→code / code→Pairing / success dwell / digit geometry / Pair feedback / silent rejects'
Write-Host '  (v1: no native HWND resize during pairing phases)'
Write-Host 'PAIRING_FLOW: ALL GATES GREEN.'
