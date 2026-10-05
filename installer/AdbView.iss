; ADB View — Inno Setup 6 per-user installer (Phase 10)
; Frozen AppId — NEVER change between upgrades of the same product.
; Parameterized via ISCC /D defines from scripts/package.ps1.

#ifndef MyAppVersion
  #error MyAppVersion must be passed by scripts/package.ps1
#endif
#ifndef MyAppRid
  #error MyAppRid must be passed by scripts/package.ps1
#endif
#ifndef MySourceDir
  #error MySourceDir must be passed by scripts/package.ps1
#endif
#ifndef MyOutputDir
  #error MyOutputDir must be passed by scripts/package.ps1
#endif
#ifndef MyOutputBaseFilename
  #error MyOutputBaseFilename must be passed by scripts/package.ps1
#endif
#ifndef MySetupIcon
  #error MySetupIcon must be passed by scripts/package.ps1
#endif

; Stable product identity — do not derive from version.
#define MyAppId "{{7C9E2A14-8B3F-4D61-9E5A-2F1C0B8D6A47}}"
#define MyAppName "ADB View"
#define MyAppExeName "AdbView.exe"
#define MyAppMutex "Local\AdbView.SingleInstance"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=
DefaultDirName={localappdata}\Programs\ADBView
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#MyArchitecturesAllowed}
ArchitecturesInstallIn64BitMode={#MyArchitecturesInstallIn64BitMode}
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyOutputBaseFilename}
SetupIconFile={#MySetupIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Prefer prompt/wait over silent force-kill (ENGINEERING §17 / Phase 10A).
AppMutex={#MyAppMutex}
CloseApplications=no
RestartApplications=no
DisableReadyPage=no
; Per-user uninstall registration under HKCU.
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start ADB View when I sign in"; GroupDescription: "Additional options:"; Flags: unchecked

[InstallDelete]
; Migration: versions up to 0.1.0 shipped a WebView2 frontend under {app}\wwwroot. Nothing
; writes there now (decision 0022), so an upgrade deletes it rather than leaving it orphaned.
Type: filesandordirs; Name: "{app}\wwwroot"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Interactive Start Menu launch — no silent tray-only argv.
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
; Clear any prior Run value first so a modify/reinstall with startup unchecked does not leave an orphan.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueName: "ADB View"; Flags: deletevalue
; Optional auto-start when the startup task is selected. Runtime tray "Start with Windows"
; toggles this same value (StartupRegistrationContract.ValueName = "ADB View").
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "ADB View"; \
    ValueData: """{app}\{#MyAppExeName}"" --startup"; \
    Flags: uninsdeletevalue; Tasks: startup

[UninstallDelete]
; App-owned data only. Never remove platform-tools or anything else the user installed.
; The WebView2 user-data folder is a migration path: versions up to 0.1.0 created it, nothing
; writes there now (decision 0022), and an uninstall must not leave it behind.
Type: filesandordirs; Name: "{localappdata}\ADBView\WebView2"
Type: filesandordirs; Name: "{localappdata}\ADBView\logs"
Type: dirifempty; Name: "{localappdata}\ADBView"
