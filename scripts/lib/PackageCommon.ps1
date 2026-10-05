# Shared helpers for scripts/package.ps1 and scripts/test-package.ps1.
Set-StrictMode -Version Latest

function Get-AdbViewVersion {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $props = Join-Path $RepoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $props)) {
        throw "Directory.Build.props not found at $props"
    }
    $node = Select-Xml -Path $props -XPath '//Version'
    if (-not $node -or -not $node.Node -or [string]::IsNullOrWhiteSpace($node.Node.InnerText)) {
        throw "Could not parse <Version> from Directory.Build.props"
    }
    $version = $node.Node.InnerText.Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$') {
        throw "Directory.Build.props <Version> '$version' is not a usable SemVer-like value."
    }
    return $version
}

function Get-PeMachineKind {
    param([Parameter(Mandatory)][string]$ExePath)
    $fs = [System.IO.File]::OpenRead($ExePath)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        if ($br.ReadUInt16() -ne 0x5A4D) { throw "Not a PE image: $ExePath" }
        $fs.Seek(0x3C, [System.IO.SeekOrigin]::Begin) | Out-Null
        $peOffset = $br.ReadUInt32()
        $fs.Seek($peOffset, [System.IO.SeekOrigin]::Begin) | Out-Null
        if ($br.ReadUInt32() -ne 0x00004550) { throw "Missing PE signature: $ExePath" }
        $machine = $br.ReadUInt16()
        switch ($machine) {
            0x8664 { return 'x64' }
            0xAA64 { return 'arm64' }
            0x14C  { return 'x86' }
            default { return ("unknown-0x{0:X}" -f $machine) }
        }
    }
    finally {
        $fs.Dispose()
    }
}

function Remove-NonPublicPublishArtifacts {
    param([Parameter(Mandatory)][string]$PackageDir)
    Get-ChildItem -LiteralPath $PackageDir -Recurse -File -Force | ForEach-Object {
        $name = $_.Name
        $rel = $_.FullName.Substring($PackageDir.Length).TrimStart('\', '/')
        $ext = $_.Extension.ToLowerInvariant()

        $remove = $false
        if ($ext -eq '.pdb') { $remove = $true }
        elseif ($ext -eq '.map') { $remove = $true }
        elseif ($ext -eq '.xml' -and (
                $name -match '^(Microsoft\.|Serilog|System\.|AdbView\.xml$)' -or
                $rel -match '(^|/)Serilog.*\.xml$'
            )) {
            # Documentation / reference XML next to managed assemblies — not app data.
            $remove = $true
        }

        if ($remove) {
            Remove-Item -LiteralPath $_.FullName -Force
        }
    }
}

function Assert-PublicPackageContents {
    param(
        [Parameter(Mandatory)][string]$PackageDir,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$ExpectedRid
    )

    $exe = Join-Path $PackageDir 'AdbView.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "AdbView.exe missing from package staging: $PackageDir"
    }

    $machine = Get-PeMachineKind -ExePath $exe
    $expectedMachine = if ($ExpectedRid -eq 'win-arm64') { 'arm64' } else { 'x64' }
    if ($machine -ne $expectedMachine) {
        throw "Executable architecture '$machine' does not match RID '$ExpectedRid' (expected $expectedMachine)."
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($versionInfo.ProductName -and $versionInfo.ProductName -ne 'ADB View') {
        throw "ProductName must be 'ADB View' (found '$($versionInfo.ProductName)')."
    }
    if ($versionInfo.FileDescription -and $versionInfo.FileDescription -ne 'ADB View') {
        throw "FileDescription must be 'ADB View' (found '$($versionInfo.FileDescription)')."
    }
    if ($versionInfo.ProductName -match 'Hawk' -or $versionInfo.FileDescription -match 'Hawk') {
        throw "User-facing metadata must not contain 'Hawk'."
    }
    $productVersion = ($versionInfo.ProductVersion -split '[+\s]')[0]
    if ($productVersion -ne $ExpectedVersion) {
        throw "Executable ProductVersion '$productVersion' does not match Directory.Build.props version '$ExpectedVersion'."
    }
    if (-not [string]::IsNullOrWhiteSpace($versionInfo.CompanyName)) {
        throw "Company metadata must be unset (found '$($versionInfo.CompanyName)')."
    }

    Assert-PackageDenylist -PackageDir $PackageDir
}

function Assert-PackageDenylist {
    param([Parameter(Mandatory)][string]$PackageDir)

    $forbiddenNamePatterns = @(
        '(?i)^adb\.exe$',
        '(?i)^AdbWinApi\.dll$',
        '(?i)^AdbWinUsbApi\.dll$',
        '(?i)FakeAdbServer',
        '(?i)\.pdb$',
        '(?i)\.map$',
        '(?i)^playwright',
        '(?i)node_modules',
        '(?i)^\.git$',
        '(?i)diagnosis',
        '(?i)human-motion',
        '(?i)mutation',
        '(?i)\.csproj$',
        '(?i)\.sln$',
        '(?i)^ENGINEERING\.md$',
        '(?i)^DESIGN\.md$',
        '(?i)^PHASES\.md$'
    )

    $forbiddenPathSegments = @(
        '\\tests\\', '/tests/',
        '\\fixtures\\', '/fixtures/',
        '\\research\\', '/research/',
        '\\docs\\', '/docs/',
        '\\artifacts\\', '/artifacts/',
        '\\diagnosis\\', '/diagnosis/',
        '\\node_modules\\', '/node_modules/',
        '\\.git\\', '/.git/',
        '\\platform-tools\\', '/platform-tools/',
        '\\playwright\\', '/playwright/'
    )

    Get-ChildItem -LiteralPath $PackageDir -Recurse -Force | ForEach-Object {
        $rel = $_.FullName.Substring($PackageDir.Length).TrimStart('\', '/')
        $norm = '\' + ($rel -replace '/', '\') + '\'

        foreach ($seg in $forbiddenPathSegments) {
            if ($norm -like ("*{0}*" -f ($seg.Trim('\')))) {
                # Use contains-style check with normalized separators.
            }
        }
        foreach ($seg in @(
                '\tests\', '\fixtures\', '\research\', '\docs\', '\artifacts\',
                '\diagnosis\', '\node_modules\', '\.git\', '\platform-tools\', '\playwright\'
            )) {
            if ($norm.Contains($seg)) {
                throw "Forbidden path segment in package: $rel"
            }
        }

        if (-not $_.PSIsContainer) {
            foreach ($pat in $forbiddenNamePatterns) {
                if ($_.Name -match $pat) {
                    throw "Forbidden file in package: $rel"
                }
            }

            # Narrow content scan for packaging-only diagnostic switches (not ordinary English words).
            if ($_.Extension -in '.js', '.html', '.json', '.config', '.exe') {
                # Skip binary exe content scan except name checks already done.
                if ($_.Extension -eq '.exe') { return }
                $text = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue
                if ($null -eq $text) { return }
                if ($text -match 'ADBVIEW_DIAG_') {
                    throw "Diagnostic switch residue in package file: $rel"
                }
                if ($text -match '(?m)^\s*--diagnose\b' -or $text -match 'human-motion-fixture') {
                    throw "Diagnosis/fixture-only residue in package file: $rel"
                }
            }
        }
    }
}

function Assert-Sha256SumsFile {
    param(
        [Parameter(Mandatory)][string]$SumsPath,
        [Parameter(Mandatory)][string]$Directory
    )
    if (-not (Test-Path -LiteralPath $SumsPath)) {
        throw "SHA256SUMS missing: $SumsPath"
    }
    Get-Content -LiteralPath $SumsPath | ForEach-Object {
        $line = $_.Trim()
        if (-not $line) { return }
        if ($line -notmatch '^([0-9a-fA-F]{64})\s+(\S+)$') {
            throw "Malformed SHA256SUMS line: $line"
        }
        $expected = $Matches[1].ToLowerInvariant()
        $name = $Matches[2]
        if ($name -eq [IO.Path]::GetFileName($SumsPath)) { return }
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path)) {
            throw "SHA256SUMS references missing file: $name"
        }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "SHA-256 mismatch for $name"
        }
    }
}

function Get-FrozenInnoAppId {
    return '{7C9E2A14-8B3F-4D61-9E5A-2F1C0B8D6A47}'
}

function Assert-InnoScriptContract {
    param([Parameter(Mandatory)][string]$IssPath)
    $text = Get-Content -LiteralPath $IssPath -Raw
    $expectedId = Get-FrozenInnoAppId
    if ($text -notmatch [regex]::Escape($expectedId.Trim('{', '}'))) {
        throw "installer/AdbView.iss must contain frozen AppId $expectedId"
    }
    if ($text -notmatch 'PrivilegesRequired=lowest') {
        throw "installer/AdbView.iss must set PrivilegesRequired=lowest"
    }
    if ($text -notmatch '\{localappdata\}\\Programs\\ADBView') {
        throw "installer/AdbView.iss must install to {localappdata}\Programs\ADBView"
    }
    if ($text -notmatch 'AppMutex=\{#MyAppMutex\}' -and $text -notmatch 'AppMutex=Local\\AdbView\.SingleInstance') {
        throw "installer/AdbView.iss must declare AppMutex for Local\AdbView.SingleInstance"
    }
    if ($text -notmatch 'Flags: unchecked') {
        throw "installer/AdbView.iss optional startup task must be unchecked by default"
    }

    # Start Menu icon must not include --startup (ignore comment lines).
    if ($text -notmatch 'Name: "\{group\}\\\{#MyAppName\}"; Filename: "\{app\}\\\{#MyAppExeName\}"') {
        throw "installer/AdbView.iss Start Menu shortcut target is missing or altered."
    }
    $iconsSection = [regex]::Match($text, '(?ms)^\[Icons\].*?(?=^\[|\z)').Value
    $iconsCode = ($iconsSection -split "`n" | Where-Object { $_ -notmatch '^\s*;' }) -join "`n"
    if ($iconsCode -match '--startup') {
        throw "Start Menu shortcut must NOT include --startup."
    }

    # Optional Run entry must include --startup.
    $registrySection = [regex]::Match($text, '(?ms)^\[Registry\].*?(?=^\[|\z)').Value
    $registryCode = ($registrySection -split "`n" | Where-Object { $_ -notmatch '^\s*;' }) -join "`n"
    if ($registryCode -notmatch 'ValueName: "ADB View"') {
        throw "HKCU Run ValueName must be 'ADB View' (shared with StartupRegistrationContract)."
    }
    if ($registryCode -notmatch '--startup') {
        throw "Optional HKCU Run entry must include --startup."
    }
    if ($registryCode -notmatch 'Tasks: startup') {
        throw "HKCU Run entry must be gated on the startup task."
    }
    # Modify/reinstall with startup unchecked must clear a prior Run value (not leave an orphan).
    if ($registryCode -notmatch 'Flags:\s*deletevalue') {
        throw "installer/AdbView.iss must deletevalue the HKCU Run entry before optionally rewriting it."
    }

    # Migration: versions up to 0.1.0 shipped a WebView2 frontend under {app}\wwwroot. Nothing
    # writes there now (decision 0022), so an upgrade must delete it rather than leave it orphaned.
    $installDelete = [regex]::Match($text, '(?ms)^\[InstallDelete\].*?(?=^\[|\z)').Value
    if ($installDelete -notmatch '\{app\}\\wwwroot') {
        throw "installer/AdbView.iss must InstallDelete the retired {app}\wwwroot frontend on upgrade."
    }
}
