# Packaging validation + compact mutation proofs for Phase 10B.
# Requires artifacts from scripts/package.ps1 (or -PackageRoot).
[CmdletBinding()]
param(
    [string]$PackageRoot,
    [switch]$SkipMutations
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib\PackageCommon.ps1')

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Invoke-ExpectFailure([scriptblock]$action, [string]$label) {
    $failed = $false
    try {
        & $action
    }
    catch {
        $failed = $true
        Write-Host "  mutation $label failed as expected: $($_.Exception.Message)"
    }
    if (-not $failed) {
        throw "Mutation $label was expected to fail but succeeded."
    }
}

$version = Get-AdbViewVersion -RepoRoot $repo
if (-not $PackageRoot) {
    $PackageRoot = Join-Path $repo "artifacts\release\$version"
}
Assert-True (Test-Path -LiteralPath $PackageRoot) "Package root missing: $PackageRoot. Run scripts/package.ps1 first."

Write-Host "test-package: validating $PackageRoot"

Assert-InnoScriptContract -IssPath (Join-Path $repo 'installer\AdbView.iss')

$sumsPath = Join-Path $PackageRoot "ADBView-$version-SHA256SUMS.txt"
$manifestPath = Join-Path $PackageRoot "ADBView-$version-manifest.json"
Assert-Sha256SumsFile -SumsPath $sumsPath -Directory $PackageRoot

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-True ($manifest.version -eq $version) "Manifest version '$($manifest.version)' != '$version'"
Assert-True ($manifest.adb.bundled -eq $false) 'Manifest must record adb.bundled=false'
Assert-True ($manifest.publish.selfContained -eq $true) 'Manifest must record selfContained=true'
Assert-True ($manifest.publish.trimmed -eq $false) 'Manifest must record trimmed=false'
Assert-True ($manifest.installer.appId -eq (Get-FrozenInnoAppId)) 'Manifest AppId drift'

foreach ($rid in @('win-x64', 'win-arm64')) {
    $zip = Join-Path $PackageRoot "ADBView-$version-$rid.zip"
    $setup = Join-Path $PackageRoot "ADBView-$version-$rid-setup.exe"
    Assert-True (Test-Path -LiteralPath $zip) "Missing $zip"
    Assert-True (Test-Path -LiteralPath $setup) "Missing $setup"
    Assert-True ($zip -match [regex]::Escape($version)) "ZIP name must include version"
    Assert-True ($setup -match [regex]::Escape($version)) "Installer name must include version"

    $pkg = Join-Path $PackageRoot "$rid\package"
    Assert-PublicPackageContents -PackageDir $pkg -ExpectedVersion $version -ExpectedRid $rid

    # Manifest hash matches file
    $entry = $manifest.artifacts | Where-Object { $_.fileName -eq [IO.Path]::GetFileName($zip) }
    Assert-True ($null -ne $entry) "Manifest missing zip entry for $rid"
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ($actual -eq $entry.sha256) "Manifest SHA-256 mismatch for $rid zip"
}

Write-Host 'test-package: structural checks PASS'

if (-not $SkipMutations) {
    Write-Host 'test-package: running mutations M1–M7...'
    $pkgX64 = Join-Path $PackageRoot 'win-x64\package'
    $backupRoot = Join-Path $env:TEMP ("adbview-pkg-mut-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $backupRoot | Out-Null
    try {
        # M1: adb.exe in staging
        $adb = Join-Path $pkgX64 'adb.exe'
        Set-Content -LiteralPath $adb -Value 'not-real-adb'
        Invoke-ExpectFailure { Assert-PackageDenylist -PackageDir $pkgX64 } 'M1'
        Remove-Item -LiteralPath $adb -Force

        # M2: pdb in staging
        $pdb = Join-Path $pkgX64 'AdbView.pdb'
        Set-Content -LiteralPath $pdb -Value 'fake-pdb'
        Invoke-ExpectFailure { Assert-PackageDenylist -PackageDir $pkgX64 } 'M2'
        Remove-Item -LiteralPath $pdb -Force

        # M3: AppId change
        $iss = Join-Path $repo 'installer\AdbView.iss'
        Copy-Item $iss (Join-Path $backupRoot 'AdbView.iss') -Force
        $issText = Get-Content -LiteralPath $iss -Raw
        $mutIss = $issText -replace '7C9E2A14-8B3F-4D61-9E5A-2F1C0B8D6A47', '00000000-0000-0000-0000-000000000000'
        Set-Content -LiteralPath $iss -Value $mutIss -Encoding utf8
        Invoke-ExpectFailure { Assert-InnoScriptContract -IssPath $iss } 'M3'
        Copy-Item (Join-Path $backupRoot 'AdbView.iss') $iss -Force

        # M4: Start Menu uses --startup
        $issText = Get-Content -LiteralPath $iss -Raw
        $mutIss = $issText -replace `
            'Name: "\{group\}\\\{#MyAppName\}"; Filename: "\{app\}\\\{#MyAppExeName\}"', `
            'Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--startup"'
        Set-Content -LiteralPath $iss -Value $mutIss -Encoding utf8
        Invoke-ExpectFailure { Assert-InnoScriptContract -IssPath $iss } 'M4'
        Copy-Item (Join-Path $backupRoot 'AdbView.iss') $iss -Force

        # M5: Run entry loses --startup
        $issText = Get-Content -LiteralPath $iss -Raw
        $mutIss = $issText -replace '""\{app\}\\\{#MyAppExeName\}"" --startup', '""{app}\{#MyAppExeName}""'
        Set-Content -LiteralPath $iss -Value $mutIss -Encoding utf8
        Invoke-ExpectFailure { Assert-InnoScriptContract -IssPath $iss } 'M5'
        Copy-Item (Join-Path $backupRoot 'AdbView.iss') $iss -Force

        # M6: corrupt SHA-256
        Copy-Item $sumsPath (Join-Path $backupRoot 'SHA256SUMS.txt') -Force
        $sumLines = Get-Content -LiteralPath $sumsPath
        $sumLines[0] = ('0' * 64) + '  ' + ($sumLines[0] -replace '^[0-9a-fA-F]+\s+', '')
        Set-Content -LiteralPath $sumsPath -Value ($sumLines -join "`n") -Encoding utf8
        Invoke-ExpectFailure { Assert-Sha256SumsFile -SumsPath $sumsPath -Directory $PackageRoot } 'M6'
        Copy-Item (Join-Path $backupRoot 'SHA256SUMS.txt') $sumsPath -Force

        # M7: version mismatch vs Directory.Build.props
        Invoke-ExpectFailure {
            Assert-PublicPackageContents -PackageDir $pkgX64 -ExpectedVersion '9.9.9' -ExpectedRid 'win-x64'
        } 'M7'
    }
    finally {
        # Restore iss if still mutated
        $bakIss = Join-Path $backupRoot 'AdbView.iss'
        if (Test-Path -LiteralPath $bakIss) {
            Copy-Item $bakIss (Join-Path $repo 'installer\AdbView.iss') -Force
        }
        $bakSums = Join-Path $backupRoot 'SHA256SUMS.txt'
        if (Test-Path -LiteralPath $bakSums) {
            Copy-Item $bakSums $sumsPath -Force
        }
        Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
        # Ensure no mutation files remain in package
        Remove-Item -LiteralPath (Join-Path $pkgX64 'adb.exe') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $pkgX64 'AdbView.pdb') -Force -ErrorAction SilentlyContinue
    }

    Write-Host 'test-package: mutations M1–M7 PASS (all reverted)'
}

Write-Host 'test-package: ALL CHECKS GREEN.'
