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
#ifndef MyWebView2Bootstrapper
  #error MyWebView2Bootstrapper must be passed by scripts/package.ps1
#endif
#ifndef MySetupIcon
  #error MySetupIcon must be passed by scripts/package.ps1
#endif

; Stable product identity — do not derive from version.
#define MyAppId "{{7C9E2A14-8B3F-4D61-9E5A-2F1C0B8D6A47}}"
#define MyAppName "ADB View"
#define MyAppExeName "AdbView.exe"
#define MyAppMutex "Local\AdbView.SingleInstance"
#define MyWebView2ClientId "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

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

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Bootstrapper is used only during install; not left as a permanent app file.
Source: "{#MyWebView2Bootstrapper}"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
; Interactive Start Menu launch — no silent tray-only argv.
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
; Optional auto-start only when the startup task is selected (installer owns Run; app never writes it).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "ADB View"; \
    ValueData: """{app}\{#MyAppExeName}"" --startup"; \
    Flags: uninsdeletevalue; Tasks: startup

[UninstallDelete]
; App-owned profile + logs only. Never remove Evergreen WebView2 Runtime or platform-tools.
Type: filesandordirs; Name: "{localappdata}\ADBView\WebView2"
Type: filesandordirs; Name: "{localappdata}\ADBView\logs"
Type: dirifempty; Name: "{localappdata}\ADBView"

[Code]
function IsWebView2RuntimePresent: Boolean;
var
  Version: String;
begin
  Result := False;

  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{#MyWebView2ClientId}', 'pv', Version) then
  begin
    if (Version <> '') and (Version <> '0.0.0.0') then
    begin
      Result := True;
      Exit;
    end;
  end;

  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{#MyWebView2ClientId}', 'pv', Version) then
  begin
    if (Version <> '') and (Version <> '0.0.0.0') then
    begin
      Result := True;
      Exit;
    end;
  end;

  if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{#MyWebView2ClientId}', 'pv', Version) then
  begin
    if (Version <> '') and (Version <> '0.0.0.0') then
      Result := True;
  end;
end;

function InstallWebView2Runtime: Boolean;
var
  ResultCode: Integer;
  Bootstrapper: String;
begin
  Bootstrapper := ExpandConstant('{tmp}\' + ExtractFileName(ExpandConstant('{#MyWebView2Bootstrapper}')));
  if not FileExists(Bootstrapper) then
  begin
    MsgBox('ADB View Setup could not find the WebView2 Evergreen bootstrapper.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  { Official silent install flags for the Evergreen Bootstrapper. }
  if not Exec(Bootstrapper, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('ADB View Setup could not start the WebView2 Evergreen bootstrapper.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    MsgBox('WebView2 Runtime installation failed (exit code ' + IntToStr(ResultCode) + ').' + #13#10 +
      'ADB View requires the Microsoft Edge WebView2 Runtime.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if not IsWebView2RuntimePresent then
  begin
    MsgBox('WebView2 Runtime was not detected after the bootstrapper finished.' + #13#10 +
      'ADB View cannot be installed without it.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  Result := '';

  if IsWebView2RuntimePresent then
    Exit;

  if not InstallWebView2Runtime then
    Result := 'WebView2 Runtime is required and could not be installed.';
end;
