; Inno Setup 6 script. Build the files first: powershell -File build\publish.ps1
; Then: ISCC.exe installer\RouteDeck.iss  → artifacts\installer\RouteDeck-Setup-<version>.exe

#define AppVersion "0.1.0"
#define PublishDir "..\artifacts\publish\RouteDeck"

[Setup]
AppId={{6F1B6C7E-2E0D-4C59-9C1F-8E3A4A1D5B21}
AppName=RouteDeck
AppVersion={#AppVersion}
AppPublisher=linnjoy
DefaultDirName={autopf}\RouteDeck
DefaultGroupName=RouteDeck
OutputDir=..\artifacts\installer
OutputBaseFilename=RouteDeck-Setup-{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\RouteDeck.exe

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "autostart"; Description: "登录时启动 RouteDeck"; Flags: unchecked

[Files]
; GUI and service must stay in the same directory: the service only accepts writes from a GUI next to it.
Source: "{#PublishDir}\RouteDeck.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\RouteDeck.Service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist

[Icons]
Name: "{group}\RouteDeck"; Filename: "{app}\RouteDeck.exe"
Name: "{group}\卸载 RouteDeck"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RouteDeck"; \
    ValueData: """{app}\RouteDeck.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\RouteDeck.Service.exe"; Parameters: "--install"; StatusMsg: "正在注册 RouteDeck 服务…"; Flags: runhidden waituntilterminated
Filename: "{app}\RouteDeck.exe"; Description: "启动 RouteDeck"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stopping the service does not touch routes: anything RouteDeck added stays until undone or rebooted (temporary routes).
Filename: "{app}\RouteDeck.Service.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveRouteDeckService"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  // Upgrade: stop the old service before files are replaced.
  if CurStep = ssInstall then
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop RouteDeck', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
