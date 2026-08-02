#ifndef MyEdition
  #define MyEdition "Office"
#endif

#ifndef OutputBase
  #define OutputBase "FleetSafetyManager_RC20_Setup"
#endif

#ifndef MyBuild
  #define MyBuild GetDateTimeString('yyyymmddhhnnss', '', '')
#endif

#define AppName "Fleet Safety Manager"
#define AppVersion "1.0.0-RC20"
#define ServiceName "FleetSafetyManager"
#define AppUrl "http://127.0.0.1:8080/"
#define ReleaseName "RC20_" + MyBuild

[Setup]
AppId={{0A8B6776-5D99-4AE8-BC42-BAD495455E68}
AppName={#AppName} {#MyEdition}
AppVersion={#AppVersion}
AppVerName={#AppName} {#MyEdition} {#AppVersion}
DefaultDirName={localappdata}\FleetSafetyManager
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
UninstallDisplayIcon={app}\FleetSafetyManager.ico
SetupIconFile=FleetSafetyManager.ico

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\*"; DestDir: "{app}\Releases\{#ReleaseName}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\FleetSafetyManagerLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "FleetSafetyManager.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "Launch Fleet Safety Manager.cmd"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{app}\Releases\{#ReleaseName}\Data"
Name: "{app}\Releases\{#ReleaseName}\Documents"
Name: "{app}\Releases\{#ReleaseName}\Documents\reports"
Name: "{app}\Releases\{#ReleaseName}\Documents\certificates"
Name: "{app}\Releases\{#ReleaseName}\Documents\service-requests"
Name: "{app}\Releases\{#ReleaseName}\Backups"
Name: "{app}\Releases\{#ReleaseName}\Logs"

[Icons]
Name: "{autodesktop}\Fleet Safety Manager {#MyEdition}"; Filename: "{app}\FleetSafetyManagerLauncher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\FleetSafetyManager.ico"; Comment: "Open Fleet Safety Manager {#MyEdition}"; Tasks: desktopicon
Name: "{group}\Fleet Safety Manager {#MyEdition}"; Filename: "{app}\FleetSafetyManagerLauncher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\FleetSafetyManager.ico"; Comment: "Open Fleet Safety Manager {#MyEdition}"
Name: "{group}\Fleet Safety Manager Data"; Filename: "{app}"
Name: "{group}\Uninstall Fleet Safety Manager"; Filename: "{uninstallexe}"

[InstallDelete]
Type: files; Name: "{autodesktop}\Fleet Safety Manager RC20.lnk"

[Run]
Filename: "{app}\FleetSafetyManagerLauncher.exe"; WorkingDir: "{app}"; Description: "Open Fleet Safety Manager {#MyEdition}"; Flags: postinstall nowait skipifsilent

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
begin
  if ServiceExists('{#ServiceName}') then
    Exec(ExpandConstant('{cmd}'), '/C sc stop {#ServiceName}', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);

  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM FSM.Api.exe /T', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);

  Sleep(1500);
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
  ConfigPath := ExpandConstant('{app}\Releases\{#ReleaseName}\appsettings.json');
  if LoadStringFromFile(ConfigPath, ConfigText) then
  begin
    UpdatedConfigText := String(ConfigText);
    StringChangeEx(UpdatedConfigText, '"Edition": "Office"', '"Edition": "{#MyEdition}"', True);
    StringChangeEx(UpdatedConfigText, '"Edition": "Ship"', '"Edition": "{#MyEdition}"', True);
    if (Pos('"Edition": "{#MyEdition}"', UpdatedConfigText) = 0) or
       (not SaveStringToFile(ConfigPath, AnsiString(UpdatedConfigText), False)) then
      RaiseException('Could not configure the RC20 {#MyEdition} release.');
  end
  else
    RaiseException('The RC20 release is missing appsettings.json.');
end;

function CurrentReleasePath: string;
var
  CurrentText: AnsiString;
  CurrentValue: string;
begin
  Result := '';
  if LoadStringFromFile(ExpandConstant('{app}\current.txt'), CurrentText) then
  begin
    CurrentValue := Trim(String(CurrentText));
    if CurrentValue <> '' then
    begin
      if ExtractFileDrive(CurrentValue) <> '' then
        Result := CurrentValue
      else
        Result := ExpandConstant('{app}\') + CurrentValue;
    end;
  end;
end;

procedure CopyPersistentData(const OldReleasePath, NewReleasePath: string);
var
  ResultCode: Integer;
  FolderName: string;
  I: Integer;
begin
  if (OldReleasePath = '') or (CompareText(OldReleasePath, NewReleasePath) = 0) or
     (not DirExists(OldReleasePath)) then
    exit;

  for I := 0 to 3 do
  begin
    case I of
      0: FolderName := 'Data';
      1: FolderName := 'Documents';
      2: FolderName := 'Backups';
      3: FolderName := 'Logs';
    end;
    if DirExists(OldReleasePath + '\' + FolderName) then
    begin
      if not Exec(ExpandConstant('{sys}\robocopy.exe'),
        '"' + OldReleasePath + '\' + FolderName + '" "' + NewReleasePath + '\' + FolderName + '" /E /COPY:DAT /DCOPY:DAT /R:2 /W:1',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode > 7) then
        RaiseException('Could not migrate ' + FolderName + ' from the previous release.');
    end;
  end;
end;

procedure VerifyRelease(const ReleasePath: string);
begin
  if not FileExists(ReleasePath + '\FSM.Api.exe') then
    RaiseException('RC20 release copy is incomplete: FSM.Api.exe is missing.');
  if not FileExists(ReleasePath + '\appsettings.json') then
    RaiseException('RC20 release copy is incomplete: appsettings.json is missing.');
  if not FileExists(ExpandConstant('{app}\FleetSafetyManagerLauncher.exe')) then
    RaiseException('RC20 launcher copy is incomplete.');
end;

procedure InstallOrUpdateService(const ReleasePath: string);
var
  ResultCode: Integer;
  BinPath: string;
  CommandLine: string;
begin
  BinPath := '"' + ReleasePath + '\FSM.Api.exe" --urls http://127.0.0.1:8080';

  if ServiceExists('{#ServiceName}') then
  begin
    CommandLine := '/C sc config {#ServiceName} binPath= "' + BinPath + '" start= auto DisplayName= "Fleet Safety Manager RC20"';
    if not Exec(ExpandConstant('{cmd}'), CommandLine, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Could not update the Fleet Safety Manager service.');
  end
  else
  begin
    CommandLine := '/C sc create {#ServiceName} binPath= "' + BinPath + '" start= auto DisplayName= "Fleet Safety Manager RC20"';
    if not Exec(ExpandConstant('{cmd}'), CommandLine, '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Could not create the Fleet Safety Manager service.');
  end;

  Exec(ExpandConstant('{cmd}'),
    '/C sc description {#ServiceName} "Fleet Safety Manager {#MyEdition} Edition RC20"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'),
    '/C sc failure {#ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/30000',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not Exec(ExpandConstant('{cmd}'), '/C sc start {#ServiceName}', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) or ((ResultCode <> 0) and (ResultCode <> 1056)) then
    RaiseException('The Fleet Safety Manager RC20 service could not start.');
end;

procedure RemoveObsoleteRC19Releases(const ActiveReleasePath: string);
var
  FindRec: TFindRec;
  ReleasesPath: string;
  Candidate: string;
begin
  ReleasesPath := ExpandConstant('{app}\Releases');
  if FindFirst(ReleasesPath + '\RC19_*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Candidate := ReleasesPath + '\' + FindRec.Name;
          if CompareText(Candidate, ActiveReleasePath) <> 0 then
            DelTree(Candidate, True, True, True);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  NewReleasePath: string;
  OldReleasePath: string;
begin
  if CurStep = ssPostInstall then
  begin
    NewReleasePath := ExpandConstant('{app}\Releases\{#ReleaseName}');
    OldReleasePath := CurrentReleasePath;
    UpdateEditionConfiguration;
    VerifyRelease(NewReleasePath);
    CopyPersistentData(OldReleasePath, NewReleasePath);
    InstallOrUpdateService(NewReleasePath);
    if not SaveStringToFile(ExpandConstant('{app}\current.txt'),
      'Releases\{#ReleaseName}', False) then
      RaiseException('Could not update current.txt to the RC20 release.');
    if not SaveStringToFile(ExpandConstant('{app}\edition.txt'),
      '{#MyEdition}', False) then
      RaiseException('Could not update edition.txt.');
    RemoveObsoleteRC19Releases(NewReleasePath);
  end;
end;
