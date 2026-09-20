# Phase 10 packaging: self-contained publish, portable ZIP, Inno installer,
# SHA-256 sums, and machine-readable manifest for win-x64 and win-arm64.
# Not part of scripts/verify.ps1. Normal release packaging must NOT use -SkipVerify.
[CmdletBinding()]
param(
    # Expected version must equal Directory.Build.props <Version> (not a second source of truth).
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64', 'all')]
    [string]$Rid = 'all',
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipVerify,
    [string]$OutputDirectory,
    [string]$InnoCompiler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Users\Teymur\.dotnet' }
$nodeDir = Join-Path $repo 'node_tool\node-v24.21.0-win-x64'
if (Test-Path $nodeDir) { $env:PATH = "$env:DOTNET_ROOT;$nodeDir;$env:PATH" }

. (Join-Path $PSScriptRoot 'lib\PackageCommon.ps1')

function Assert-LastExitCode([string]$step) {
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Package step '$step' failed with exit code $LASTEXITCODE."
    }
}

function Resolve-InnoCompiler([string]$explicit) {
    if ($explicit) {
        if (-not (Test-Path -LiteralPath $explicit)) {
            throw "Inno compiler not found at -InnoCompiler path: $explicit"
        }
        return (Resolve-Path -LiteralPath $explicit).Path
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) {
            return (Resolve-Path -LiteralPath $c).Path
        }
    }

    throw @"
Inno Setup 6 ISCC.exe was not found.
Install Inno Setup 6, or pass -InnoCompiler <path-to-ISCC.exe>.
package.ps1 does not install Inno Setup automatically.
"@
}

Write-Host 'package: resolving version and tools...'
$propsVersion = Get-AdbViewVersion -RepoRoot $repo
if ($Version) {
    if ($Version -ne $propsVersion) {
        throw "Version parameter '$Version' does not match Directory.Build.props <Version> '$propsVersion'."
    }
}
$version = $propsVersion
Write-Host "package: version=$version"

$dotnetVersion = (dotnet --version)
if ($dotnetVersion -ne '10.0.401') {
    throw "dotnet SDK must be 10.0.401 (found '$dotnetVersion'); see global.json"
}

$iscc = Resolve-InnoCompiler $InnoCompiler
Write-Host "package: ISCC=$iscc"

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo 'artifacts\release'
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$releaseRoot = Join-Path $OutputDirectory $version
if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

$rids = if ($Rid -eq 'all') { @('win-x64', 'win-arm64') } else { @($Rid) }

if (-not $SkipVerify) {
    Write-Host 'package: running scripts/verify.ps1 (required for release packaging)...'
    & (Join-Path $PSScriptRoot 'verify.ps1')
    Assert-LastExitCode 'verify.ps1'
}
else {
    Write-Host 'package: -SkipVerify set; building frontend via scripts/build.ps1 only.'
    & (Join-Path $PSScriptRoot 'build.ps1')
    Assert-LastExitCode 'build.ps1'
}

$wwwrootIndex = Join-Path $repo 'src\AdbView\wwwroot\index.html'
if (-not (Test-Path -LiteralPath $wwwrootIndex)) {
    throw "Production wwwroot missing: $wwwrootIndex"
}
Assert-FrontendWwwroot -WwwrootDir (Join-Path $repo 'src\AdbView\wwwroot')

$redistDir = Join-Path $repo 'installer\redist'
New-Item -ItemType Directory -Path $redistDir -Force | Out-Null
$bootstrapper = Ensure-WebView2Bootstrapper -RedistDir $redistDir
$setupIcon = Join-Path $repo 'assets\icons\aperture-link-idle.ico'
if (-not (Test-Path -LiteralPath $setupIcon)) {
    throw "Setup icon missing: $setupIcon"
}

# Frozen V-6 decision: IncludeNativeLibrariesForSelfExtract=true so WebView2Loader
# and other native deps extract reliably from the single-file bundle.
$includeNative = 'true'
$gitCommit = (git -C $repo rev-parse HEAD).Trim()
$builtUtc = [DateTime]::UtcNow.ToString('o')
$artifactRecords = @()

foreach ($rid in $rids) {
    Write-Host "package: publishing $rid..."
    $ridRoot = Join-Path $releaseRoot $rid
    $publishDir = Join-Path $ridRoot 'publish'
    $packageDir = Join-Path $ridRoot 'package'
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    $csproj = Join-Path $repo 'src\AdbView\AdbView.csproj'
    dotnet publish $csproj `
        -c $Configuration `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:PublishTrimmed=false `
        -p:IncludeNativeLibrariesForSelfExtract=$includeNative `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDir
    Assert-LastExitCode "dotnet publish $rid"

    if (Test-Path -LiteralPath $packageDir) {
        Remove-Item -LiteralPath $packageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $packageDir -Recurse -Force

    Remove-NonPublicPublishArtifacts -PackageDir $packageDir
    Assert-PublicPackageContents -PackageDir $packageDir -ExpectedVersion $version -ExpectedRid $rid

    $zipName = "ADBView-$version-$rid.zip"
    $zipPath = Join-Path $releaseRoot $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "package: wrote $zipPath"

    $archAllowed = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64compatible' }
    $archInstall = $archAllowed
    $setupBase = "ADBView-$version-$rid-setup"
    $setupPath = Join-Path $releaseRoot "$setupBase.exe"

    $iss = Join-Path $repo 'installer\AdbView.iss'
    # ISCC resolves relative /D paths against the .iss directory — always pass absolute paths.
    $packageDirAbs = (Resolve-Path -LiteralPath $packageDir).Path
    $releaseRootAbs = (Resolve-Path -LiteralPath $releaseRoot).Path
    $bootstrapperAbs = (Resolve-Path -LiteralPath $bootstrapper).Path
    $setupIconAbs = (Resolve-Path -LiteralPath $setupIcon).Path
    $isccArgs = @(
        "/DMyAppVersion=$version",
        "/DMyAppRid=$rid",
        "/DMySourceDir=$packageDirAbs",
        "/DMyOutputDir=$releaseRootAbs",
        "/DMyOutputBaseFilename=$setupBase",
        "/DMyWebView2Bootstrapper=$bootstrapperAbs",
        "/DMySetupIcon=$setupIconAbs",
        "/DMyArchitecturesAllowed=$archAllowed",
        "/DMyArchitecturesInstallIn64BitMode=$archInstall",
        $iss
    )
    Write-Host "package: compiling installer $setupBase.exe"
    & $iscc @isccArgs
    Assert-LastExitCode "ISCC $rid"
    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw "Installer was not produced: $setupPath"
    }

    $pkgFiles = Get-ChildItem -Recurse -File $packageDir | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($packageDir.Length).TrimStart('\', '/')
            sizeBytes = $_.Length
        }
    }

    $artifactRecords += [ordered]@{
        rid = $rid
        publishMode = 'self-contained-single-file-r2r'
        selfContained = $true
        singleFile = $true
        readyToRun = $true
        trimmed = $false
        includeNativeLibrariesForSelfExtract = ($includeNative -eq 'true')
        packageFiles = @($pkgFiles)
        zip = $zipName
        installer = "$setupBase.exe"
    }
}

Write-Host 'package: writing manifest and SHA-256 sums...'
$manifestName = "ADBView-$version-manifest.json"
$manifestPath = Join-Path $releaseRoot $manifestName
$sumsName = "ADBView-$version-SHA256SUMS.txt"
$sumsPath = Join-Path $releaseRoot $sumsName

$publicArtifacts = @()
foreach ($rec in $artifactRecords) {
    $publicArtifacts += $rec.zip
    $publicArtifacts += $rec.installer
}

$shaLines = New-Object System.Collections.Generic.List[string]
$manifestArtifacts = @()
foreach ($name in $publicArtifacts) {
    $path = Join-Path $releaseRoot $name
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item -LiteralPath $path).Length
    $shaLines.Add("$hash  $name")
    $manifestArtifacts += [ordered]@{
        fileName = $name
        sizeBytes = $size
        sha256 = $hash
    }
}

$manifest = [ordered]@{
    productName = 'ADB View'
    version = $version
    gitCommit = $gitCommit
    builtAtUtc = $builtUtc
    architectures = @($rids)
    publish = [ordered]@{
        selfContained = $true
        singleFile = $true
        readyToRun = $true
        trimmed = $false
        includeNativeLibrariesForSelfExtract = ($includeNative -eq 'true')
    }
    webView2 = [ordered]@{
        strategy = 'evergreen-runtime'
        bootstrapper = 'MicrosoftEdgeWebview2Setup.exe'
        bundledRuntime = $false
    }
    adb = [ordered]@{
        bundled = $false
        discovery = 'AdbExecutableLocator precedence unchanged'
    }
    installer = [ordered]@{
        technology = 'Inno Setup 6'
        appId = '{7C9E2A14-8B3F-4D61-9E5A-2F1C0B8D6A47}'
        privilegesRequired = 'lowest'
        installPath = '%LOCALAPPDATA%\Programs\ADBView'
        startMenu = 'ADB View'
        desktopShortcut = $false
        optionalAutoStart = 'HKCU Run with --startup (unchecked by default)'
    }
    artifacts = @($manifestArtifacts)
    ridDetails = @($artifactRecords)
}

$manifestJson = $manifest | ConvertTo-Json -Depth 8
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaLines.Add("$manifestHash  $manifestName")
[System.IO.File]::WriteAllText($sumsPath, ($shaLines -join "`n") + "`n", $utf8NoBom)

Assert-Sha256SumsFile -SumsPath $sumsPath -Directory $releaseRoot

Write-Host ''
Write-Host 'package: COMPLETE'
Write-Host "  release root: $releaseRoot"
Get-ChildItem -LiteralPath $releaseRoot -File | ForEach-Object {
    Write-Host ("  {0,12:N0}  {1}" -f $_.Length, $_.Name)
}
