; Hub installer (Inno Setup 6). Built by CI: ISCC /DAppVersion=x.y.z /DPublishDir=..\publish installer\Hub.iss
; Per-user install, no admin rights: Hub only needs HID access and per-user settings.

#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
AppId={{6F1D6C5E-3B0A-4C55-9C5E-2B7F0E4A9D11}
AppName=Hub
AppVersion={#AppVersion}
AppVerName=Hub {#AppVersion}
AppPublisher=Hub contributors
AppPublisherURL=https://github.com/mfaour34/hub
AppSupportURL=https://github.com/mfaour34/hub/issues
AppUpdatesURL=https://github.com/mfaour34/hub/releases
DefaultDirName={localappdata}\Programs\Hub
DefaultGroupName=Hub
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Hub-Setup-{#AppVersion}
SetupIconFile=..\assets\Hub.ico
UninstallDisplayIcon={app}\Hub.exe
UninstallDisplayName=Hub
LicenseFile=..\LICENSE
InfoBeforeFile=before-install.txt
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Hub holds this mutex while running; setup and uninstall ask to close it.
AppMutex=Local\Hub
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start Hub when I sign in to Windows (recommended: it applies your audio mix and battery alerts)"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Hub"; Filename: "{app}\Hub.exe"
Name: "{autodesktop}\Hub"; Filename: "{app}\Hub.exe"; Tasks: desktopicon

[Registry]
; Same value Hub's own "Start Hub with Windows" switch writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Hub"; ValueData: """{app}\Hub.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Hub.exe"; Description: "Launch Hub"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Points Windows back at the output device, restores the sink's name and any Hub 1.0 VoiceMeeter/Equalizer APO setup, removes autostart.
Filename: "{app}\Hub.exe"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "HubCleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\' + Copy(WebView2Key, 10, Length(WebView2Key)), 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, WebView2Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, WebView2Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  // /SKIPWEBVIEW2CHECK=1 is for CI smoke tests on runners without the runtime.
  if (ExpandConstant('{param:SKIPWEBVIEW2CHECK|0}') = '0') and not WebView2Installed() then
  begin
    if MsgBox('Hub''s window needs the Microsoft Edge WebView2 Runtime, which is not installed.' + #13#10#13#10 +
              'Open the download page now? (Install it, then run this setup again.)', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/#download-section', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() and
     (MsgBox('Also delete your Hub settings and custom profiles (%APPDATA%\Hub)?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(ExpandConstant('{userappdata}\Hub'), True, True, True);
end;
