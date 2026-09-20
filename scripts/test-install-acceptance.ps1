# Phase 10C - install / upgrade / uninstall acceptance helpers (silent Inno where safe).
# Headed tray/flyout checks remain human/LIVE; this script covers inventory + silent paths.
[CmdletBinding()]
param(
    [ValidateSet('install', 'uninstall', 'upgrade-prep', 'upgrade', 'inventory', 'all-silent')]
    [string]$Action = 'inventory',
    [string]$ArtifactsDir,
    [string]$Version = '0.1.0',
    [string]$UpgradeVersion = '0.1.1',
    [string]$UpgradeArtifactsDir,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',
    [switch]$WithStartup,
    [string]$SetupPath,
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib\PackageCommon.ps1')

if (-not $ArtifactsDir) {
    $ArtifactsDir = Join-Path $repo "artifacts\release\$Version"
}
if (-not $ReportPath) {
    $ReportPath = Join-Path $repo 'artifacts\phase10c\report.json'
}

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ADBView'
$logDir = Join-Path $env:LOCALAPPDATA 'ADBView\logs'
$wvDir = Join-Path $env:LOCALAPPDATA 'ADBView\WebView2'
$startMenuLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ADB View\ADB View.lnk'
$appIdGuid = (Get-FrozenInnoAppId).Trim('{', '}')

function Write-Report($obj) {
    $dir = Split-Path -Parent $ReportPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($ReportPath, ($obj | ConvertTo-Json -Depth 8), $utf8)
    Write-Host "Wrote $ReportPath"
}

function Assert-ShaIntegrity {
    $sums = Join-Path $ArtifactsDir "ADBView-$Version-SHA256SUMS.txt"
    Assert-Sha256SumsFile -SumsPath $sums -Directory $ArtifactsDir
    Write-Host 'SHA-256 integrity: PASS'
}

function Get-RunValue {
    try {
        $item = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ADB View' -ErrorAction Stop
        return $item.'ADB View'
    }
    catch {
        return $null
    }
}

function Get-InstalledInventory {
    [ordered]@{
        installDirExists = (Test-Path -LiteralPath $installDir)
        files = @(
            if (Test-Path -LiteralPath $installDir) {
                Get-ChildItem -LiteralPath $installDir -Recurse -File | ForEach-Object {
                    [ordered]@{
                        path = $_.FullName.Substring($installDir.Length).TrimStart('\')
                        sizeBytes = $_.Length
                    }
                }
            }
        )
        startMenuShortcut = (Test-Path -LiteralPath $startMenuLnk)
        runValue = (Get-RunValue)
        uninstallKey = @(
            Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -EA SilentlyContinue |
                ForEach-Object {
                    $p = Get-ItemProperty $_.PSPath -EA SilentlyContinue
                    if ($null -ne $p -and ($p.DisplayName -eq 'ADB View' -or ($_.PSChildName -match $appIdGuid))) {
                        [ordered]@{
                            key = $_.PSChildName
                            displayName = $p.DisplayName
                            displayVersion = $p.DisplayVersion
                            uninstallString = $p.UninstallString
                        }
                    }
                }
        )
        logDirExists = (Test-Path -LiteralPath $logDir)
        webViewProfileExists = (Test-Path -LiteralPath $wvDir)
        processCount = @(Get-Process -Name AdbView -EA SilentlyContinue).Count
    }
}

function Assert-InstalledDenylist {
    if (-not (Test-Path -LiteralPath $installDir)) {
        throw "Install dir missing: $installDir"
    }
    Assert-PackageDenylist -PackageDir $installDir
    $exe = Join-Path $installDir 'AdbView.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'AdbView.exe missing after install' }
    $index = Join-Path $installDir 'wwwroot\index.html'
    if (-not (Test-Path -LiteralPath $index)) { throw 'wwwroot/index.html missing after install' }
}

function Get-ShortcutTarget([string]$lnkPath) {
    if (-not (Test-Path -LiteralPath $lnkPath)) { return $null }
    $sh = New-Object -ComObject WScript.Shell
    $sc = $sh.CreateShortcut($lnkPath)
    return [ordered]@{
        targetPath = $sc.TargetPath
        arguments = $sc.Arguments
        workingDirectory = $sc.WorkingDirectory
    }
}

function Invoke-SilentSetup([string]$setup, [switch]$Startup) {
    if (-not (Test-Path -LiteralPath $setup)) { throw "Setup missing: $setup" }
    $args = @('/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES', '/SP-')
    if ($Startup) {
        $args += '/TASKS=startup'
    }
    else {
        $args += '/TASKS='
    }
    Write-Host "Running: $setup $($args -join ' ')"
    $p = Start-Process -FilePath $setup -ArgumentList $args -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        throw "Silent setup failed with exit $($p.ExitCode)"
    }
}

function Invoke-SilentUninstall {
    $keys = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -EA SilentlyContinue
    $unins = $null
    foreach ($k in $keys) {
        $p = Get-ItemProperty $k.PSPath -EA SilentlyContinue
        if ($p.DisplayName -eq 'ADB View' -or ($k.PSChildName -match $appIdGuid)) {
            $unins = $p.UninstallString
            break
        }
    }
    if (-not $unins) {
        # Fallback to Inno unins000.exe in install dir
        $candidate = Join-Path $installDir 'unins000.exe'
        if (Test-Path $candidate) { $unins = "`"$candidate`"" }
    }
    if (-not $unins) { throw 'No uninstall entry found for ADB View' }

    # Inno UninstallString is typically: "...\unins000.exe"
    $exe = $unins.Trim('"')
    Write-Host "Silent uninstall: $exe"
    $p = Start-Process -FilePath $exe -ArgumentList @('/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES') -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        throw "Silent uninstall failed with exit $($p.ExitCode)"
    }
}

function Stop-AdbViewProcesses {
    Get-Process -Name AdbView -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Seconds 1
}

switch ($Action) {
    'inventory' {
        $inv = Get-InstalledInventory
        $inv['shortcut'] = Get-ShortcutTarget $startMenuLnk
        $inv | ConvertTo-Json -Depth 6 | Write-Host
        Write-Report $inv
    }
    'install' {
        Assert-ShaIntegrity
        Stop-AdbViewProcesses
        if (-not $SetupPath) {
            $SetupPath = Join-Path $ArtifactsDir "ADBView-$Version-$Rid-setup.exe"
        }
        Invoke-SilentSetup -setup $SetupPath -Startup:$WithStartup
        Assert-InstalledDenylist
        $inv = Get-InstalledInventory
        $sc = Get-ShortcutTarget $startMenuLnk
        if (-not $sc) { throw 'Start Menu shortcut missing after install' }
        if ($sc.targetPath -ne (Join-Path $installDir 'AdbView.exe')) {
            throw "Unexpected shortcut target: $($sc.targetPath)"
        }
        if ($sc.arguments -and $sc.arguments.ToString().Trim().Length -gt 0) {
            throw "Start Menu shortcut must have empty arguments (found '$($sc.arguments)')"
        }
        if ($WithStartup) {
            $run = Get-RunValue
            if (-not $run -or $run -notmatch '--startup') { throw "Run entry missing --startup: $run" }
            if ($run -notmatch [regex]::Escape((Join-Path $installDir 'AdbView.exe'))) {
                throw "Run entry path unexpected: $run"
            }
        }
        else {
            $run = Get-RunValue
            if ($run) { throw "Run entry present but startup task was not selected: $run" }
        }
        Write-Host 'Silent install: PASS'
        Write-Report ([ordered]@{ action = 'install'; inventory = $inv; shortcut = $sc })
    }
    'uninstall' {
        Stop-AdbViewProcesses
        Invoke-SilentUninstall
        Start-Sleep -Seconds 1
        if (Test-Path -LiteralPath $installDir) {
            throw "Install dir still present after uninstall: $installDir"
        }
        if (Test-Path -LiteralPath $startMenuLnk) { throw 'Start Menu shortcut remains after uninstall' }
        $run = Get-RunValue
        if ($run) { throw "Run entry remains after uninstall: $run" }
        if (Test-Path -LiteralPath $logDir) { throw "Logs remain after uninstall: $logDir" }
        if (Test-Path -LiteralPath $wvDir) { throw "WebView2 profile remains after uninstall: $wvDir" }
        Write-Host 'Silent uninstall: PASS'
        Write-Report ([ordered]@{ action = 'uninstall'; ok = $true })
    }
    'upgrade-prep' {
        # Caller builds N+1 separately; this only verifies AppId stability in iss.
        Assert-InnoScriptContract -IssPath (Join-Path $repo 'installer\AdbView.iss')
        Write-Host 'Upgrade prep (AppId contract): PASS'
    }
    'upgrade' {
        Assert-InnoScriptContract -IssPath (Join-Path $repo 'installer\AdbView.iss')
        if (-not $UpgradeArtifactsDir) {
            $UpgradeArtifactsDir = Join-Path $repo "artifacts\release-nplus1\$UpgradeVersion"
        }
        $upgradeSetup = Join-Path $UpgradeArtifactsDir "ADBView-$UpgradeVersion-$Rid-setup.exe"
        if (-not (Test-Path -LiteralPath $upgradeSetup)) {
            throw "N+1 setup missing: $upgradeSetup"
        }

        Stop-AdbViewProcesses
        if (-not (Test-Path -LiteralPath $installDir)) {
            throw "Base install missing at $installDir - run -Action install first"
        }

        $assetsDir = Join-Path $installDir 'wwwroot\assets'
        New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
        $staleName = 'stale-old-hash-SENTINEL.js'
        $stalePath = Join-Path $assetsDir $staleName
        Set-Content -LiteralPath $stalePath -Value '// phase10c stale asset sentinel' -Encoding utf8
        $beforeAssets = @(Get-ChildItem -LiteralPath $assetsDir -File | Select-Object -ExpandProperty Name)
        Write-Host ("Pre-upgrade assets ({0}): {1}" -f $beforeAssets.Count, ($beforeAssets -join ', '))

        $beforeUnins = @(
            Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -EA SilentlyContinue |
                Where-Object {
                    $p = Get-ItemProperty $_.PSPath -EA SilentlyContinue
                    $null -ne $p -and ($p.DisplayName -eq 'ADB View' -or ($_.PSChildName -match $appIdGuid))
                }
        )
        if ($beforeUnins.Count -ne 1) {
            throw "Expected exactly one uninstall entry before upgrade; found $($beforeUnins.Count)"
        }

        Invoke-SilentSetup -setup $upgradeSetup -Startup:$WithStartup
        Assert-InstalledDenylist

        if (Test-Path -LiteralPath $stalePath) {
            throw "Stale frontend asset remained after upgrade: $stalePath"
        }
        $afterAssets = @(Get-ChildItem -LiteralPath $assetsDir -File -EA SilentlyContinue | Select-Object -ExpandProperty Name)
        if ($afterAssets -contains $staleName) {
            throw 'Stale sentinel still listed in wwwroot/assets after upgrade'
        }

        $exe = Join-Path $installDir 'AdbView.exe'
        $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
        if ($vi.ProductVersion -notmatch [regex]::Escape($UpgradeVersion) -and $vi.FileVersion -notmatch '^0\.1\.1') {
            throw "Upgraded exe version unexpected: ProductVersion='$($vi.ProductVersion)' FileVersion='$($vi.FileVersion)'"
        }
        if ($vi.ProductName -ne 'ADB View' -or $vi.FileDescription -ne 'ADB View') {
            throw "Product metadata drifted after upgrade: Name='$($vi.ProductName)' Desc='$($vi.FileDescription)'"
        }

        $afterUnins = @(
            Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -EA SilentlyContinue |
                Where-Object {
                    $p = Get-ItemProperty $_.PSPath -EA SilentlyContinue
                    $null -ne $p -and ($p.DisplayName -eq 'ADB View' -or ($_.PSChildName -match $appIdGuid))
                }
        )
        if ($afterUnins.Count -ne 1) {
            throw "Expected exactly one uninstall entry after upgrade; found $($afterUnins.Count)"
        }
        $dispVer = (Get-ItemProperty $afterUnins[0].PSPath).DisplayVersion
        if ($dispVer -ne $UpgradeVersion) {
            throw "Uninstall DisplayVersion='$dispVer' expected '$UpgradeVersion'"
        }

        $sc = Get-ShortcutTarget $startMenuLnk
        if (-not $sc -or $sc.targetPath -ne $exe) {
            throw "Start Menu shortcut invalid after upgrade: $($sc | ConvertTo-Json -Compress)"
        }

        Write-Host 'Silent upgrade N->N+1 + stale wwwroot purge: PASS'
        Write-Report ([ordered]@{
                action = 'upgrade'
                fromVersion = $Version
                toVersion = $UpgradeVersion
                beforeAssets = $beforeAssets
                afterAssets = $afterAssets
                productVersion = $vi.ProductVersion
                fileVersion = $vi.FileVersion
                uninstallCount = $afterUnins.Count
                displayVersion = $dispVer
                ok = $true
            })
    }
    'all-silent' {
        Assert-ShaIntegrity
        Stop-AdbViewProcesses
        if (Test-Path $installDir) {
            try { Invoke-SilentUninstall } catch { Write-Host "pre-clean uninstall skipped: $_" }
        }
        $SetupPath = Join-Path $ArtifactsDir "ADBView-$Version-$Rid-setup.exe"
        Invoke-SilentSetup -setup $SetupPath
        Assert-InstalledDenylist
        $beforeAssets = @(Get-ChildItem (Join-Path $installDir 'wwwroot\assets') -File -EA SilentlyContinue | Select-Object -ExpandProperty Name)
        Write-Host ("Installed wwwroot assets: {0}" -f ($beforeAssets -join ', '))
        Invoke-SilentUninstall
        Write-Host 'all-silent baseline install/uninstall: PASS'
        Write-Report ([ordered]@{
                action = 'all-silent'
                version = $Version
                rid = $Rid
                assets = $beforeAssets
                ok = $true
            })
    }
}
