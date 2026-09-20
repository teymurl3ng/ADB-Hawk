# Mutations M1–M4 for stability-first freeze (decision 0005).
# Apply → expect FAIL → revert → expect PASS.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Users\Teymur\.dotnet' }
$nodeDir = Join-Path $repo 'node_tool\node-v24.21.0-win-x64'
if (Test-Path $nodeDir) { $env:PATH = "$env:DOTNET_ROOT;$nodeDir;$env:PATH" }

$report = New-Object System.Collections.Generic.List[string]
function Note([string]$msg) { Write-Host $msg; [void]$report.Add($msg) }
function Assert-Fail([string]$step) {
    if ($LASTEXITCODE -eq 0) { throw "Mutation '$step' expected FAIL but passed." }
    Note "  FAIL as expected ($step)"
}
function Assert-Pass([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "Mutation '$step' expected PASS after revert ($LASTEXITCODE)." }
    Note "  PASS after revert ($step)"
}
function Run-Vitest([string]$files) {
    Push-Location (Join-Path $repo 'ui')
    try {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & npm.cmd test -- --run $files
        $script:LASTEXITCODE = $LASTEXITCODE
    }
    finally { Pop-Location }
}
function Run-DotnetFilter([string]$filter) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet test (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release --no-build --filter $filter -v q
    $script:LASTEXITCODE = $LASTEXITCODE
    $ErrorActionPreference = $prev
}
function Invoke-MutationStep {
    param(
        [string]$Name,
        [string]$Path,
        [string]$Original,
        [string]$Mutated,
        [scriptblock]$RunFail,
        [scriptblock]$RunPass,
        [scriptblock]$BeforeFail = $null,
        [scriptblock]$BeforePass = $null
    )
    if ($Mutated -eq $Original) { throw "$Name patch failed (no change)" }
    Note $Name
    try {
        Set-Content -Path $Path -Value $Mutated -NoNewline
        if ($BeforeFail) { & $BeforeFail }
        & $RunFail
        Assert-Fail $Name
    }
    finally {
        Set-Content -Path $Path -Value $Original -NoNewline
    }
    if ($BeforePass) { & $BeforePass }
    & $RunPass
    Assert-Pass $Name
}

Write-Host 'MUTATIONS: ensure Release build...'
dotnet build (Join-Path $repo 'ADBView.sln') -c Release -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

# M1 — allow DeviceCard disclosure to resize HWND while visible
$pol = Join-Path $repo 'src\AdbView\Host\FlyoutInteractionPolicy.cs'
$polOrig = Get-Content -Raw $pol
$polMut = $polOrig.Replace(
    'public static bool ShouldApplyContentResizeToHwnd(bool flyoutVisible) =>' + "`r`n" + '        !flyoutVisible;',
    'public static bool ShouldApplyContentResizeToHwnd(bool flyoutVisible) =>' + "`r`n" + '        true; // MUTATION M1')
if ($polMut -eq $polOrig) {
    $polMut = $polOrig.Replace(
        'public static bool ShouldApplyContentResizeToHwnd(bool flyoutVisible) =>' + "`n" + '        !flyoutVisible;',
        'public static bool ShouldApplyContentResizeToHwnd(bool flyoutVisible) =>' + "`n" + '        true; // MUTATION M1')
}
Invoke-MutationStep -Name 'M1: disclosure may resize HWND while visible' -Path $pol -Original $polOrig -Mutated $polMut `
    -BeforeFail {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & dotnet build (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release -v q | Out-Null
        $ErrorActionPreference = $prev
    } `
    -RunFail { Run-DotnetFilter 'FullyQualifiedName~FrozenVisibleBoundsTests' } `
    -BeforePass {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & dotnet build (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release -v q | Out-Null
        $ErrorActionPreference = $prev
    } `
    -RunPass { Run-DotnetFilter 'FullyQualifiedName~FrozenVisibleBoundsTests' }

# M2 — same freeze broken for pairing (same policy; assert still fails when always-apply)
# Re-run with inverted comment-only is weak; instead mutate ShouldApply to ignore "pairing" by always true again after M1 revert.
# Use a second distinct mutation: invert the boolean.
$polMut2 = $polOrig.Replace('!flyoutVisible', 'flyoutVisible /* MUTATION M2 */')
Invoke-MutationStep -Name 'M2: pairing/content resize while visible' -Path $pol -Original $polOrig -Mutated $polMut2 `
    -BeforeFail {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & dotnet build (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release -v q | Out-Null
        $ErrorActionPreference = $prev
    } `
    -RunFail { Run-DotnetFilter 'FullyQualifiedName~FrozenVisibleBoundsTests' } `
    -BeforePass {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & dotnet build (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release -v q | Out-Null
        $ErrorActionPreference = $prev
    } `
    -RunPass { Run-DotnetFilter 'FullyQualifiedName~FrozenVisibleBoundsTests' }

# M3 — restore temporary blank / hide of DeviceCard on toggle
$app = Join-Path $repo 'ui\src\App.tsx'
$appOrig = Get-Content -Raw $app
$needle3 = 'setExpandedIds((prev) => {' + "`n" + '      if (prev.has(id)) return new Set<string>();' + "`n" + '      return new Set<string>([id]);' + "`n" + '    });'
$repl3 = @'
setExpandedIds((prev) => {
      // MUTATION M3: blank then expand — forces disappearance
      if (prev.has(id)) return new Set<string>();
      return new Set<string>();
    });
    window.setTimeout(() => setExpandedIds(new Set<string>([id])), 50);
'@
# Use simpler mutation: clear root content briefly via CSS class injection in handleToggleExpand
$appMut = $appOrig.Replace(
    'setExpandedIds((prev) => {
      if (prev.has(id)) return new Set<string>();
      return new Set<string>([id]);
    });',
    @'
setExpandedIds(new Set()); // MUTATION M3 blank
    document.querySelector('[data-testid="flyout-shell"]')?.replaceChildren();
    setTimeout(() => setExpandedIds(new Set([id])), 40);
'@)
if ($appMut -eq $appOrig) {
    $appMut = $appOrig.Replace(
        "setExpandedIds((prev) => {`r`n      if (prev.has(id)) return new Set<string>();`r`n      return new Set<string>([id]);`r`n    });",
        "setExpandedIds(new Set()); // MUTATION M3`r`n    document.querySelector('[data-testid=`"flyout-shell`"]')?.replaceChildren();`r`n    setTimeout(() => setExpandedIds(new Set([id])), 40);")
}
Invoke-MutationStep -Name 'M3: blank DeviceCard on toggle' -Path $app -Original $appOrig -Mutated $appMut `
    -RunFail {
        Push-Location (Join-Path $repo 'ui')
        try {
            $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            & npx.cmd playwright test --project=motion tests/motion/rapid-toggle.spec.ts
            $script:LASTEXITCODE = $LASTEXITCODE
            $ErrorActionPreference = $prev
        }
        finally { Pop-Location }
    } `
    -RunPass {
        Push-Location (Join-Path $repo 'ui')
        try {
            $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            & npx.cmd playwright test --project=motion tests/motion/rapid-toggle.spec.ts
            $script:LASTEXITCODE = $LASTEXITCODE
            $ErrorActionPreference = $prev
        }
        finally { Pop-Location }
    }

# M4 — map mDNS service instance to display Serial
$mapper = Join-Path $repo 'src\AdbView\State\DeviceViewMapper.cs'
$mapOrig = Get-Content -Raw $mapper
$mapMut = $mapOrig.Replace(
    'if (LooksLikeMdnsOrDiscoveryIdentity(hardwareSerial)) {' + "`r`n" + '            return null;' + "`r`n" + '        }',
    'if (false && LooksLikeMdnsOrDiscoveryIdentity(hardwareSerial)) { // MUTATION M4' + "`r`n" + '            return null;' + "`r`n" + '        }')
if ($mapMut -eq $mapOrig) {
    $mapMut = $mapOrig.Replace(
        'if (LooksLikeMdnsOrDiscoveryIdentity(hardwareSerial)) {' + "`n" + '            return null;' + "`n" + '        }',
        'if (false && LooksLikeMdnsOrDiscoveryIdentity(hardwareSerial)) { // MUTATION M4' + "`n" + '            return null;' + "`n" + '        }')
}
# Also break identity extraction so messy string becomes HardwareSerial
$idPath = Join-Path $repo 'src\AdbView\Adb\DeviceIdentity.cs'
$idOrig = Get-Content -Raw $idPath
# For M4 we only need LooksLike bypass — Unextractable test uses adb-unknownblob after strip.
# Mdns_Connect extracts hardware — LooksLike false. Use Unextractable test.
Invoke-MutationStep -Name 'M4: mDNS instance as display Serial' -Path $mapper -Original $mapOrig -Mutated $mapMut `
    -BeforeFail {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & dotnet build (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release -v q | Out-Null
        $ErrorActionPreference = $prev
    } `
    -RunFail { Run-DotnetFilter 'FullyQualifiedName~Unextractable_Mdns_Service_Name_Omits_Display_Serial|FullyQualifiedName~LooksLikeMdns_Rejects' } `
    -BeforePass {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & dotnet build (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release -v q | Out-Null
        $ErrorActionPreference = $prev
    } `
    -RunPass { Run-DotnetFilter 'FullyQualifiedName~DeviceViewMapperSerialTests' }

$residue = @()
$residue += Get-ChildItem -Path (Join-Path $repo 'ui\src') -Recurse -Include *.ts,*.tsx | Select-String -Pattern 'MUTATION M' -SimpleMatch -ErrorAction SilentlyContinue
$residue += Get-ChildItem -Path (Join-Path $repo 'src\AdbView') -Recurse -Include *.cs | Select-String -Pattern 'MUTATION M' -SimpleMatch -ErrorAction SilentlyContinue
if ($residue.Count -gt 0) {
    $residue | ForEach-Object { Write-Host $_ }
    throw 'Mutation residue left in sources'
}

Note 'MUTATIONS M1-M4: ALL EXECUTED. No residue.'
$dir = Join-Path $repo 'artifacts\diagnosis'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$report | Set-Content (Join-Path $dir 'mutations-m1-m4-stability.txt')
Write-Host 'MUTATIONS: DONE.'
