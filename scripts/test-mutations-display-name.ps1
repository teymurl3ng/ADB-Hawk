# Mutations M1–M6 for human device DisplayName resolution.
# Apply → expect FAIL → revert → expect PASS. No residue.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Join-Path $env:USERPROFILE '.dotnet' }
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

$exe = Join-Path $repo 'tests\AdbView.Tests\bin\Release\net10.0-windows\AdbView.Tests.exe'
$resolver = Join-Path $repo 'src\AdbView\State\DeviceDisplayName.cs'
$mapper = Join-Path $repo 'src\AdbView\State\DeviceViewMapper.cs'
$reducer = Join-Path $repo 'src\AdbView\State\StateReducer.cs'

function Note([string]$msg) { Write-Host $msg }
function Assert-Fail([string]$step) {
    if ($LASTEXITCODE -eq 0) { throw "Mutation '$step' expected FAIL but passed." }
    Note "  FAIL as expected ($step)"
}
function Assert-Pass([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "Mutation '$step' expected PASS after revert ($LASTEXITCODE)." }
    Note "  PASS after revert ($step)"
}
function Run-Class([string]$class, [string]$methodGlob = '*') {
    $ErrorActionPreference = 'Continue'
    & $exe -class $class -method $methodGlob
    $script:LASTEXITCODE = $LASTEXITCODE
}
function Rebuild-Tests {
    $ErrorActionPreference = 'Continue'
    & dotnet build (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Rebuild failed ($LASTEXITCODE)." }
}

Note 'Building tests…'
Rebuild-Tests

# --- M1: Ignore marketname; fall through to certification model ---
Note 'M1: ignore marketname'
$orig = Get-Content -Raw $resolver
$mut = $orig -replace 'if \(TryAccept\(marketingName, out string market\)\) \{[\r\n\s]+return market;[\r\n\s]+\}', ''
if ($mut -eq $orig) { throw 'M1 patch did not apply' }
Set-Content -Path $resolver -Value $mut -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.DeviceDisplayNameTests' '*Xiaomi_Certification*'
Assert-Fail 'M1'
Set-Content -Path $resolver -Value $orig -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.DeviceDisplayNameTests' '*Xiaomi_Certification*'
Assert-Pass 'M1'

# --- M2: Keep tracker model forever after shell metadata (skip ExactModel update path via DisplayName) ---
Note 'M2: tracker model forever (mapper never preserves refined DisplayName)'
$origMap = Get-Content -Raw $mapper
$mutMap = $origMap -replace 'string displayName = preserveMetadata && previous\?\.Detail\.ExactModel is not null\s+\? previous\.DisplayName\s+: normalized\.DisplayName;', 'string displayName = normalized.DisplayName;'
if ($mutMap -eq $origMap) { throw 'M2 patch did not apply' }
Set-Content -Path $mapper -Value $mutMap -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.MetadataIntegrationTests' '*DisplayName_Refinement*'
Assert-Fail 'M2'
Set-Content -Path $mapper -Value $origMap -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.MetadataIntegrationTests' '*DisplayName_Refinement*'
Assert-Pass 'M2'

# --- M3: Allow transport-like DisplayName ---
Note 'M3: allow transport-like'
$orig = Get-Content -Raw $resolver
$mut = $orig -replace 'if \(IsRejectedToken\(trimmed\) \|\| IsTransportLike\(trimmed\)\) \{[\r\n\s]+return false;[\r\n\s]+\}', 'if (IsRejectedToken(trimmed)) { return false; }'
if ($mut -eq $orig) { throw 'M3 patch did not apply' }
Set-Content -Path $resolver -Value $mut -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.DeviceDisplayNameTests' '*Rejects_Transport*'
Assert-Fail 'M3'
Set-Content -Path $resolver -Value $orig -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.DeviceDisplayNameTests' '*Rejects_Transport*'
Assert-Pass 'M3'

# --- M4: Use friendly DisplayName as DeviceId (corrupt identity check) ---
Note 'M4: DisplayName as DeviceId'
$origRed = Get-Content -Raw $reducer
# Force DisplayName into Id on metadata refine — duplicate-name identity test must fail.
$mutRed = $origRed -replace 'var updatedDevice = device with \{[\r\n\s]+BatteryPercent = battery,[\r\n\s]+DisplayName = displayName,[\r\n\s]+Detail = updatedDetail,[\r\n\s]+\};', @'
var updatedDevice = device with {
            Id = displayName,
            BatteryPercent = battery,
            DisplayName = displayName,
            Detail = updatedDetail,
        };
'@
if ($mutRed -eq $origRed) { throw 'M4 patch did not apply' }
Set-Content -Path $reducer -Value $mutRed -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.MetadataIntegrationTests' '*Duplicate_Friendly*'
Assert-Fail 'M4'
Set-Content -Path $reducer -Value $origRed -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.MetadataIntegrationTests' '*Duplicate_Friendly*'
Assert-Pass 'M4'

# --- M5: Metadata refinement resets ConnectedSince ---
Note 'M5: reset ConnectedSince on refine'
$origRed = Get-Content -Raw $reducer
$mutRed = $origRed -replace 'var updatedDevice = device with \{[\r\n\s]+BatteryPercent = battery,[\r\n\s]+DisplayName = displayName,[\r\n\s]+Detail = updatedDetail,[\r\n\s]+\};', @'
var updatedDevice = device with {
            BatteryPercent = battery,
            DisplayName = displayName,
            ConnectedSince = now.AddHours(1),
            Detail = updatedDetail,
        };
'@
if ($mutRed -eq $origRed) { throw 'M5 patch did not apply' }
Set-Content -Path $reducer -Value $mutRed -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.MetadataIntegrationTests' '*DisplayName_Refinement*'
Assert-Fail 'M5'
Set-Content -Path $reducer -Value $origRed -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.MetadataIntegrationTests' '*DisplayName_Refinement*'
Assert-Pass 'M5'

# --- M6: Remove model fallback ---
Note 'M6: remove model fallback'
$orig = Get-Content -Raw $resolver
$mut = $orig -replace 'if \(TryAccept\(model, out string modelValue\)\) \{[\s\S]*?return modelValue;[\r\n\s]+\}', ''
if ($mut -eq $orig) { throw 'M6 patch did not apply' }
Set-Content -Path $resolver -Value $mut -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.DeviceDisplayNameTests' '*Older_Android*'
Assert-Fail 'M6'
Set-Content -Path $resolver -Value $orig -NoNewline
Rebuild-Tests
Run-Class 'AdbView.Tests.State.DeviceDisplayNameTests' '*Older_Android*'
Assert-Pass 'M6'

Note 'All DisplayName mutations M1–M6: PASS (no residue)'
