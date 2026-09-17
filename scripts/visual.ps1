# Visual regression gate. Baselines are committed; updating them requires the
# explicit -Accept flag (AGENTS.md R-12) and must record the WebView2/Chromium
# version in the commit message. No specs exist until Phase 2 (PHASES.md).
[CmdletBinding()]
param(
    # Accept visual diffs by updating the committed baselines. Never automatic.
    [switch]$Accept
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$specsDir = Join-Path $repo 'ui\tests\visual'

$hasSpecs = (Test-Path $specsDir) -and (@(Get-ChildItem -Path $specsDir -Filter '*.spec.ts' -Recurse -ErrorAction SilentlyContinue).Count -gt 0)
if (-not $hasSpecs) {
    Write-Host 'visual: no Playwright specs yet (visual baselines begin in Phase 2); nothing to run.'
    return
}

Push-Location (Join-Path $repo 'ui')
try {
    npx.cmd playwright install chromium
    if ($Accept) {
        Write-Host 'visual: updating baselines (-Accept). Record the WebView2 runtime version in the commit.'
        npx.cmd playwright test --update-snapshots
    }
    else {
        npx.cmd playwright test
    }

    if ($LASTEXITCODE -ne 0) {
        throw "visual: playwright exited with $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
