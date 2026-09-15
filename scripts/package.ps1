# Packaging: self-contained single-file publish + portable zip.
# Full installer work lands in Phase 10 (PHASES.md); this produces the publish
# artifacts. Not part of scripts/verify.ps1.
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Assert-LastExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        throw "Package step '$step' failed with exit code $LASTEXITCODE."
    }
}

& (Join-Path $PSScriptRoot 'build.ps1')

$version = (Select-Xml -Path (Join-Path $repo 'Directory.Build.props') -XPath '//Version').Node.InnerText.Trim()
$outDir = Join-Path $repo 'artifacts\publish' + $Runtime

dotnet publish (Join-Path $repo 'src\AdbView\AdbView.csproj') `
    -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:PublishTrimmed=false `
    -o $outDir
Assert-LastExitCode 'dotnet publish'

$zip = Join-Path $repo "artifacts\AdbView-$version-$Runtime.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip

Write-Host "package: $zip"
