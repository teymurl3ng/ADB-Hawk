# Build the whole product: frontend bundle, wwwroot copy, then the .NET solution.
[CmdletBinding()]
param(
    # Reinstall ui dependencies from the lockfile even if node_modules exists.
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Users\Teymur\.dotnet' }
$nodeDir = Join-Path $repo 'node_tool\node-v24.21.0-win-x64'
if (Test-Path $nodeDir) { $env:PATH = "$env:DOTNET_ROOT;$nodeDir;$env:PATH" }

function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "Build step '$step' failed with exit code $LASTEXITCODE."
    }
}

# 1. Frontend dependencies (lockfile-exact).
$ui = Join-Path $repo 'ui'
Push-Location $ui
try {
    if ($Clean -or -not (Test-Path (Join-Path $ui 'node_modules'))) {
        npm.cmd ci
        Assert-LastExitCode 'npm ci'
    }

    # 2. Production bundle to ui/dist (local assets only; the boundary tests
    #    scan this output for absolute http(s) URLs).
    npm.cmd run build
    Assert-LastExitCode 'vite build'
}
finally {
    Pop-Location
}

$dist = Join-Path $ui 'dist'
if (-not (Test-Path (Join-Path $dist 'index.html'))) {
    throw "vite build did not produce $dist\index.html."
}

# 3. Copy the bundle to the native wwwroot (served through the WebView2 virtual
#    host mapping from Phase 1 onward; gitignored build output).
$wwwroot = Join-Path $repo 'src\AdbView\wwwroot'
if (Test-Path -LiteralPath $wwwroot) {
    Remove-Item -LiteralPath $wwwroot -Recurse -Force
}
Copy-Item -LiteralPath $dist -Destination $wwwroot -Recurse

# 4. .NET solution, Release, warnings-as-errors (enforced by Directory.Build.props).
dotnet build (Join-Path $repo 'ADBView.sln') -c Release
Assert-LastExitCode 'dotnet build'

Write-Host 'build: frontend bundle, wwwroot copy, and Release solution build complete.'
