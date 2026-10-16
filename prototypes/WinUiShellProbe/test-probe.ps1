[CmdletBinding()]
param([switch]$Publish)

$ErrorActionPreference = 'Stop'
$probeRepo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$probeEvidence = Join-Path $probeRepo 'artifacts\winui-rebuild\m02'
$probePublishRoot = Join-Path $probeRepo 'artifacts\winui-rebuild\probe-publish'
New-Item -ItemType Directory -Path $probeEvidence -Force | Out-Null
if ($Publish) {
    & 'C:\Users\Teymur\.dotnet\dotnet.exe' publish (Join-Path $PSScriptRoot 'WinUiShellProbe.csproj') -c Release -p:RestoreLockedMode=true -o $probePublishRoot
    if ($LASTEXITCODE -ne 0) { throw "Probe publish failed: $LASTEXITCODE" }
}

$probeExe = Join-Path $probePublishRoot 'WinUiShellProbe.exe'
if (-not (Test-Path -LiteralPath $probeExe)) { throw "Missing published probe: $probeExe" }
$probeCases = @(
    @{Name='compiled-startup';Extra=@();Expected=0},
    @{Name='forced-failure';Extra=@('--force-failure');Expected=1},
    @{Name='invalid-driver-option';Extra=@('--hold-ms=bad');Expected=1},
    @{Name='bounded-acrylic';Extra=@('--acrylic');Expected=0},
    @{Name='native-lifetime';Extra=@('--native');Expected=0},
    @{Name='native-callback-failure';Extra=@('--native','--force-native-failure');Expected=1},
    @{Name='surface-api-experiment';Extra=@('--surface','--acrylic');Expected=0}
)
$probeCaseResults = foreach ($probeCase in $probeCases) {
    $probeResultPath = Join-Path $probeEvidence ($probeCase.Name + '-' + [guid]::NewGuid().ToString('N') + '.json')
    $probeArguments = @(('"--verify=' + $probeResultPath + '"')) + $probeCase.Extra
    $probeProcess = Start-Process -FilePath $probeExe -ArgumentList $probeArguments -PassThru
    try {
        if (-not $probeProcess.WaitForExit(20000)) { throw "Probe timed out: $($probeCase.Name)" }
        if (-not (Test-Path -LiteralPath $probeResultPath)) { throw "Probe produced no result: $($probeCase.Name), exit $($probeProcess.ExitCode)" }
        $probeResult = Get-Content -LiteralPath $probeResultPath -Raw | ConvertFrom-Json
        if ($probeProcess.ExitCode -ne $probeCase.Expected -or $probeResult.Passed -ne ($probeCase.Expected -eq 0)) {
            throw "Wrong exit/assertion result: $($probeCase.Name), exit $($probeProcess.ExitCode)"
        }
        [pscustomobject]@{Case=$probeCase.Name;Exit=$probeProcess.ExitCode;Passed=$true;Result=$probeResultPath}
    }
    finally {
        if (-not $probeProcess.HasExited) { Stop-Process -Id $probeProcess.Id -Force }
        $probeProcess.Dispose()
    }
}
$probeCaseResults | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $probeEvidence 'api-verification.json') -Encoding utf8
$probeCaseResults | Format-Table -AutoSize
'API verification passed. The transparent-envelope experiment fails cross-process input and is not the selected surface. Full M02 acceptance is separate.'
