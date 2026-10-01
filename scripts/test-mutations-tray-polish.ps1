# Mutations M1–M8 for tray preferences + centering (decision 0006).
# Apply → expect FAIL → revert → expect PASS.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Join-Path $env:USERPROFILE '.dotnet' }
$nodeDir = Join-Path $repo 'node_tool\node-v24.21.0-win-x64'
if (Test-Path $nodeDir) { $env:PATH = "$env:DOTNET_ROOT;$nodeDir;$env:PATH" }
else { $env:PATH = "$env:DOTNET_ROOT;$env:PATH" }

$exe = Join-Path $repo 'tests\AdbView.Tests\bin\Release\net10.0-windows\AdbView.Tests.exe'

function Note([string]$msg) { Write-Host $msg }
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
        $ErrorActionPreference = 'Continue'
        & npm.cmd test -- --run $files
        $script:LASTEXITCODE = $LASTEXITCODE
    }
    finally { Pop-Location }
}
function Run-Class([string]$class, [string]$methodGlob = '*') {
    $ErrorActionPreference = 'Continue'
    & $exe -class $class -method $methodGlob
    $script:LASTEXITCODE = $LASTEXITCODE
}
function Rebuild-Tests {
    & dotnet build (Join-Path $repo 'tests\AdbView.Tests\AdbView.Tests.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'rebuild failed' }
}
function Invoke-MutationStep {
    param(
        [string]$Name,
        [string]$Path,
        [string]$Original,
        [string]$Mutated,
        [scriptblock]$RunFail,
        [scriptblock]$RunPass,
        [switch]$NeedsRebuild
    )
    Note $Name
    $text = [System.IO.File]::ReadAllText($Path)
    if ($text.IndexOf($Original, [StringComparison]::Ordinal) -lt 0) {
        throw "Mutation ${Name}: original fragment not found in $Path"
    }
    [System.IO.File]::WriteAllText($Path, $text.Replace($Original, $Mutated))
    try {
        if ($NeedsRebuild) { Rebuild-Tests }
        & $RunFail
        Assert-Fail $Name
    }
    finally {
        [System.IO.File]::WriteAllText($Path, $text)
    }
    if ($NeedsRebuild) { Rebuild-Tests }
    & $RunPass
    Assert-Pass $Name
}

Note 'Building Release tests once…'
Rebuild-Tests

Invoke-MutationStep -Name 'M1 centering gutter' `
    -Path (Join-Path $repo 'ui\src\styles\device-card-motion.css') `
    -Original 'scrollbar-gutter: stable both-edges;' `
    -Mutated 'scrollbar-gutter: stable;' `
    -RunFail {
        $txt = Join-Path $repo 'ui\src\styles\device-card-motion.css.txt'
        $t = [System.IO.File]::ReadAllText($txt)
        [System.IO.File]::WriteAllText($txt, $t.Replace('scrollbar-gutter: stable both-edges;', 'scrollbar-gutter: stable;'))
        try { Run-Vitest 'src/styles/scrollbar-gutter.test.ts' }
        finally { [System.IO.File]::WriteAllText($txt, $t) }
    } `
    -RunPass { Run-Vitest 'src/styles/scrollbar-gutter.test.ts' }

Invoke-MutationStep -Name 'M2 startup missing --startup' -NeedsRebuild `
    -Path (Join-Path $repo 'src\AdbView\Host\StartupRegistration.cs') `
    -Original ' + StartupArgument;' `
    -Mutated ';' `
    -RunFail { Run-Class 'AdbView.Tests.Host.StartupRegistrationTests' '*D_E*' } `
    -RunPass { Run-Class 'AdbView.Tests.Host.StartupRegistrationTests' '*D_E*' }

Invoke-MutationStep -Name 'M3 menu refresh ignores registry' -NeedsRebuild `
    -Path (Join-Path $repo 'src\AdbView\Host\TrayIcon.cs') `
    -Original 'RequireItem("Start with Windows").Checked = _startup.GetState().IsRegistered;' `
    -Mutated 'RequireItem("Start with Windows").Checked = false;' `
    -RunFail { Run-Class 'AdbView.Tests.Host.TrayContextMenuTests' '*Opening_Refresh*' } `
    -RunPass { Run-Class 'AdbView.Tests.Host.TrayContextMenuTests' '*Opening_Refresh*' }

Invoke-MutationStep -Name 'M4 notifications ignore master off' -NeedsRebuild `
    -Path (Join-Path $repo 'src\AdbView\Notifications\NotificationCoordinator.cs') `
    -Original @'
    public void SetNotificationsEnabled(bool enabled) {
        lock (_gate) {
            if (_disposed || _state.ShuttingDown) {
                return;
            }

            if (_notificationsEnabled == enabled) {
                return;
            }

            _notificationsEnabled = enabled;
            CancelAllPendingLocked();
            if (enabled && _current is not null) {
                _previous = _current;
            }
        }
    }
'@ `
    -Mutated @'
    public void SetNotificationsEnabled(bool enabled) {
        _ = _gate; // mutation: master switch is a no-op
        _ = enabled;
    }
'@ `
    -RunFail { Run-Class 'AdbView.Tests.Notifications.NotificationMasterSwitchTests' '*Off_Same_Event*' } `
    -RunPass { Run-Class 'AdbView.Tests.Notifications.NotificationMasterSwitchTests' '*Off_Same_Event*' }

Invoke-MutationStep -Name 'M5 pending fires after off' -NeedsRebuild `
    -Path (Join-Path $repo 'src\AdbView\Notifications\NotificationCoordinator.cs') `
    -Original 'CancelAllPendingLocked();
            if (enabled && _current is not null) {
                _previous = _current;
            }' `
    -Mutated 'if (enabled && _current is not null) {
                _previous = _current;
            }' `
    -RunFail {
        $p = Join-Path $repo 'src\AdbView\Notifications\NotificationCoordinator.cs'
        $t = [System.IO.File]::ReadAllText($p)
        $t2 = $t.Replace(' || !_notificationsEnabled', '')
        $t2 = $t2.Replace(@'
            if (!_notificationsEnabled) {
                return;
            }

            DateTimeOffset now = _clock();
            (toEmit, _state) = NotificationPolicy.ApplyBurstAndRecord(
'@, @'
            DateTimeOffset now = _clock();
            (toEmit, _state) = NotificationPolicy.ApplyBurstAndRecord(
'@)
        [System.IO.File]::WriteAllText($p, $t2)
        Rebuild-Tests
        Run-Class 'AdbView.Tests.Notifications.NotificationMasterSwitchTests' '*Off_While_Pending*'
    } `
    -RunPass { Run-Class 'AdbView.Tests.Notifications.NotificationMasterSwitchTests' '*Off_While_Pending*' }

Invoke-MutationStep -Name 'M6 System ignores OS theme' -NeedsRebuild `
    -Path (Join-Path $repo 'src\AdbView\Host\EffectiveTheme.cs') `
    -Original '_ => systemAppsTheme,' `
    -Mutated '_ => ThemeKind.Dark,' `
    -RunFail { Run-Class 'AdbView.Tests.Host.EffectiveThemeTests' '*Resolve_Matrix*' } `
    -RunPass { Run-Class 'AdbView.Tests.Host.EffectiveThemeTests' '*Resolve_Matrix*' }

Invoke-MutationStep -Name 'M7 effective theme single resolver' -NeedsRebuild `
    -Path (Join-Path $repo 'src\AdbView\Host\AppHost.cs') `
    -Original 'return EffectiveTheme.Resolve(preference, SystemTheme.ReadAppsTheme());' `
    -Mutated 'return SystemTheme.ReadAppsTheme();' `
    -RunFail { Run-Class 'AdbView.Tests.Host.EffectiveThemeSourceConsistencyTests' } `
    -RunPass { Run-Class 'AdbView.Tests.Host.EffectiveThemeSourceConsistencyTests' }

Invoke-MutationStep -Name 'M8 theme exclusivity' -NeedsRebuild `
    -Path (Join-Path $repo 'src\AdbView\Host\TrayIcon.cs') `
    -Original @'
            RequireThemeChild("System").Checked = theme == ThemePreference.System;
            RequireThemeChild("Light").Checked = theme == ThemePreference.Light;
            RequireThemeChild("Dark").Checked = theme == ThemePreference.Dark;
'@ `
    -Mutated @'
            RequireThemeChild("System").Checked = true;
            RequireThemeChild("Light").Checked = true;
            RequireThemeChild("Dark").Checked = true;
'@ `
    -RunFail { Run-Class 'AdbView.Tests.Host.TrayContextMenuTests' '*Theme_Exclusivity*' } `
    -RunPass { Run-Class 'AdbView.Tests.Host.TrayContextMenuTests' '*Theme_Exclusivity*' }

Note 'Mutations M1-M8 complete.'
