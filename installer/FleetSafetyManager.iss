#ifndef MyEdition
  #define MyEdition "Office"
#endif

#ifndef OutputBase
  #define OutputBase "FleetSafetyManager_RC19_Setup"
#endif

#define AppName "Fleet Safety Manager"
#define AppVersion "1.0.0-RC19"
#define ServiceName "FleetSafetyManager"
#define AppUrl "http://127.0.0.1:8088/"

[Setup]
AppId={{0A8B6776-5D99-4AE8-BC42-BAD495455E68}
AppName={#AppName} {#MyEdition}
AppVersion={#AppVersion}
AppVerName={#AppName} {#MyEdition} {#AppVersion}
DefaultDirName={autopf}\Fleet Safety Manager
DefaultGroupName=Fleet Safety Manager
OutputDir=output
OutputBaseFilename={#OutputBase}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName} {#MyEdition}

[Files]
; Replace application binaries, but never overwrite user data or an existing configuration.
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "appsettings.json;Data\*;Documents\*;Backups\*;Logs\*"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
Source: "..\publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "FleetSafetyManagerLauncher.vbs"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{app}\Data"; Flags: uninsneveruninstall
Name: "{app}\Documents"; Flags: uninsneveruninstall
Name: "{app}\Documents\reports"; Flags: uninsneveruninstall
Name: "{app}\Documents\certificates"; Flags: uninsneveruninstall
Name: "{app}\Backups"; Flags: uninsneveruninstall
Name: "{app}\Logs"; Flags: uninsneveruninstall

[Icons]
Name: "{autodesktop}\Fleet Safety Manager RC19"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\FleetSafetyManagerLauncher.vbs"""; WorkingDir: "{app}"; Comment: "Open Fleet Safety Manager RC19"
Name: "{group}\Fleet Safety Manager RC19"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\FleetSafetyManagerLauncher.vbs"""; WorkingDir: "{app}"; Comment: "Open Fleet Safety Manager RC19"
Name: "{group}\Fleet Safety Manager Data"; Filename: "{app}"
Name: "{group}\Uninstall Fleet Safety Manager"; Filename: "{uninstallexe}"

[Run]
Filename: "{sys}\wscript.exe"; Parameters: """{app}\FleetSafetyManagerLauncher.vbs"""; Description: "Open Fleet Safety Manager RC19"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C sc stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{cmd}"; Parameters: "/C sc delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
function ServiceExists(const ServiceName: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/C sc query "' + ServiceName + '"', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure StopExistingApplication;
var
  ResultCode: Integer;
  I: Integer;
begin
  if ServiceExists('{#ServiceName}') then
    Exec(ExpandConstant('{cmd}'), '/C sc stop {#ServiceName}', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);

  ; Also stop portable or older copies that were not installed as a service.
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM FSM.Api.exe /T', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);

  for I := 1 to 20 do
  begin
    if not CheckForMutexes('FleetSafetyManagerRunning') then
      Sleep(250);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopExistingApplication;
  Result := '';
end;

procedure UpdateEditionConfiguration;
var
  ConfigPath: string;
  ConfigText: AnsiString;
  UpdatedConfigText: String;
begin
  ConfigPath := ExpandConstant('{app}\appsettings.json');
  if LoadStringFromFile(ConfigPath, ConfigText) then
  begin
    UpdatedConfigText := String(ConfigText);
    StringChangeEx(UpdatedConfigText, '"Edition": "Office"', '"Edition": "{#MyEdition}"', True);
    StringChangeEx(UpdatedConfigText, '"Edition": "Ship"', '"Edition": "{#MyEdition}"', True);
    SaveStringToFile(ConfigPath, AnsiString(UpdatedConfigText), False);
  end;
end;

procedure InstallOrUpdateService;
var
  ResultCode: Integer;
  BinPath: string;
begin
  if ServiceExists('{#ServiceName}') then
    Exec(ExpandConstant('{cmd}'), '/C sc delete {#ServiceName}', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);

  Sleep(1000);
  BinPath := '"' + ExpandConstant('{app}\FSM.Api.exe') + '" --urls http://127.0.0.1:8088';

  Exec(ExpandConstant('{cmd}'),
    '/C sc create {#ServiceName} binPath= "' + BinPath + '" start= auto DisplayName= "Fleet Safety Manager RC19"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'),
    '/C sc description {#ServiceName} "Fleet Safety Manager {#MyEdition} Edition RC19"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'),
    '/C sc failure {#ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/30000',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C sc start {#ServiceName}', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    UpdateEditionConfiguration;
    InstallOrUpdateService;
  end;
end;
