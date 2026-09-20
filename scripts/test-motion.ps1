# Rapid-toggle / frozen-HWND / serial stability gates.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Users\Teymur\.dotnet' }
$nodeDir = Join-Path $repo 'node_tool\node-v24.21.0-win-x64'
if (Test-Path $nodeDir) { $env:PATH = "$env:DOTNET_ROOT;$nodeDir;$env:PATH" }

function Assert-Ok([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "Motion stability '$step' failed ($LASTEXITCODE)." }
}

Write-Host 'MOTION: build...'
dotnet build (Join-Path $repo 'ADBView.sln') -c Release -v q
Assert-Ok 'build'

Write-Host 'MOTION: frozen-visible-bounds + serial semantics...'
dotnet test (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') `
    -c Release --no-build `
    --filter 'FullyQualifiedName~FrozenVisibleBoundsTests|FullyQualifiedName~DeviceViewMapperSerialTests|FullyQualifiedName~DeviceIdentityTests' `
    -v q
Assert-Ok 'csharp-freeze-serial'

Write-Host 'MOTION: disclosure CSS vitest...'
Push-Location (Join-Path $repo 'ui')
try {
    npm.cmd test -- --run src/styles/motion-tokens.test.ts src/components/DeviceCard.expansion.test.tsx
    Assert-Ok 'vitest-disclosure'
}
finally {
    Pop-Location
}

Write-Host 'MOTION: Playwright rapid-toggle...'
Push-Location (Join-Path $repo 'ui')
try {
    node (Join-Path (Get-Location) 'node_modules\@playwright\test\cli.js') test --project=motion
    Assert-Ok 'playwright-motion'
}
finally {
    Pop-Location
}

Write-Host 'MOTION: ALL GATES GREEN.'
