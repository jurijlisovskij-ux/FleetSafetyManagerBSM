#ifndef MyEdition
  #define MyEdition "Office"
#endif

#ifndef OutputBase
  #define OutputBase "FleetSafetyManager_Setup"
#endif

#define AppName "Fleet Safety Manager"
#define AppVersion "1.0.0-rc1"
#define ServiceName "FleetSafetyManager"

[Setup]
AppId={{0A8B6776-5D99-4AE8-BC42-BAD495455E68}
AppName={#AppName} {#MyEdition}
AppVersion={#AppVersion}
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
UninstallDisplayName={#AppName} {#MyEdition}
SetupLogging=yes

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "appsettings.json"
Source: "..\deployment\appsettings.Production.template.json"; DestDir: "{commonappdata}\Fleet Safety Manager\{#MyEdition}"; DestName: "appsettings.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
Name: "{commonappdata}\Fleet Safety Manager\{#MyEdition}\Data"
Name: "{commonappdata}\Fleet Safety Manager\{#MyEdition}\Documents"
Name: "{commonappdata}\Fleet Safety Manager\{#MyEdition}\Backups"
Name: "{commonappdata}\Fleet Safety Manager\{#MyEdition}\Logs"

[Icons]
Name: "{group}\Open Fleet Safety Manager"; Filename: "http://localhost:8080"
Name: "{group}\Fleet Safety Manager Data"; Filename: "{commonappdata}\Fleet Safety Manager\{#MyEdition}"
Name: "{commondesktop}\Fleet Safety Manager"; Filename: "http://localhost:8080"

[Run]
Filename: "{cmd}"; Parameters: "/C sc stop {#ServiceName}"; Flags: runhidden waituntilterminated; Check: ServiceExists('{#ServiceName}')
Filename: "{cmd}"; Parameters: "/C sc delete {#ServiceName}"; Flags: runhidden waituntilterminated; Check: ServiceExists('{#ServiceName}')
Filename: "{cmd}"; Parameters: "/C sc create {#ServiceName} binPath= ""{app}\FSM.Api.exe --urls http://0.0.0.0:8080"" start= auto DisplayName= ""Fleet Safety Manager"""; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc description {#ServiceName} ""Fleet Safety Manager {#MyEdition} Edition"""; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall delete rule name=""Fleet Safety Manager"""; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall add rule name=""Fleet Safety Manager"" dir=in action=allow protocol=TCP localport=8080"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc start {#ServiceName}"; Flags: runhidden waituntilterminated
Filename: "http://localhost:8080"; Description: "Open Fleet Safety Manager"; Flags: postinstall shellexec skipifsilent

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\{#ServiceName}"; ValueType: multisz; ValueName: "Environment"; ValueData: "FSM_CONFIG_PATH={commonappdata}\Fleet Safety Manager\{#MyEdition}\appsettings.json"; Flags: uninsdeletevalue

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C sc stop {#ServiceName}"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C sc delete {#ServiceName}"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C netsh advfirewall firewall delete rule name=""Fleet Safety Manager"""; Flags: runhidden waituntilterminated

[Code]
function ServiceExists(const ServiceName: string): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{cmd}'),
      '/C sc query "' + ServiceName + '"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode
    ) and (ResultCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigPath: string;
  ConfigText: AnsiString;
  UpdatedConfigText: String;
begin
  if CurStep = ssPostInstall then
  begin
    ConfigPath := ExpandConstant('{commonappdata}\Fleet Safety Manager\{#MyEdition}\appsettings.json');
    if LoadStringFromFile(ConfigPath, ConfigText) then
    begin
      UpdatedConfigText := String(ConfigText);
      StringChangeEx(
        UpdatedConfigText,
        '"Edition": "Office"',
        '"Edition": "{#MyEdition}"',
        True
      );
      StringChangeEx(UpdatedConfigText, '\Office\', '\{#MyEdition}\', True);
      SaveStringToFile(
        ConfigPath,
        AnsiString(UpdatedConfigText),
        False
      );
    end;
  end;
end;
