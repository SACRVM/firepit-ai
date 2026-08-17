; Firepit installer — Inno Setup script
;
; Build: ISCC.exe installer\firepit.iss
;        (Defaults to current source-tree version. Override per build via
;         /DAppVersion=1.12.0 on the ISCC command line — used by CI.)
;
; Inputs (must exist before ISCC runs):
;   - bin/win-x64/Firepit.exe   (from `dotnet publish` single-file)
;   - src/Firepit/firepit.ico
;
; Output:
;   - bin/installer/FirepitSetup-<version>-win-x64.exe

#ifndef AppVersion
  #define AppVersion "0.23.0"
#endif

[Setup]
AppId={{8E0E9F4C-5C9A-4A3E-9F5A-FIREPITAPPID01}}
AppName=Firepit
AppVersion={#AppVersion}
AppPublisher=SACRVM
AppPublisherURL=https://github.com/SACRVM/firepit-ai
AppSupportURL=https://github.com/SACRVM/firepit-ai/issues
AppUpdatesURL=https://github.com/SACRVM/firepit-ai/releases
DefaultDirName={localappdata}\Programs\Firepit
DefaultGroupName=Firepit
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\bin\installer
OutputBaseFilename=FirepitSetup-{#AppVersion}-win-x64
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Firepit\firepit.ico
UninstallDisplayIcon={app}\Firepit.exe
UninstallDisplayName=Firepit {#AppVersion}
WizardImageStretch=no
ShowLanguageDialog=no
DisableWelcomePage=no
; Triggers a WM_SETTINGCHANGE broadcast at install/uninstall completion so
; newly-spawned shells (and Firepit's own ConPTY children) pick up the
; updated PATH without a logoff.
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce
Name: "addtopath";   Description: "Add Firepit to PATH (so Claude Code can reach firepit-mcp)"; GroupDescription: "Integration:"; Flags: checkedonce

[Files]
Source: "..\bin\win-x64\Firepit.exe";   DestDir: "{app}"; Flags: ignoreversion
; firepit-mcp.exe is the stdio bridge that lets Claude Code talk to Firepit
; via MCP. Lives next to Firepit.exe; the "addtopath" task in [Tasks] is what
; makes the bare filename in projects' .claude/settings.json resolvable.
Source: "..\bin\win-x64\firepit-mcp.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Firepit"; Filename: "{app}\Firepit.exe"
Name: "{group}\Uninstall Firepit"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Firepit"; Filename: "{app}\Firepit.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Firepit.exe"; Description: "Launch Firepit"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Leave user data in %APPDATA%\Firepit and %LOCALAPPDATA%\Firepit alone by
; default — projects and settings survive an uninstall.

[Code]
const
  EnvironmentKey = 'Environment';

var
  ProjectsPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  ProjectsPage := CreateInputDirPage(
    wpSelectDir,
    'Where do your projects live?',
    'Firepit scans this folder for projects with a CLAUDE.md or .claude/ marker.',
    'Pick a folder. You can change this later in Firepit''s settings.',
    False, '');
  ProjectsPage.Add('Projects root:');
  ProjectsPage.Values[0] := ExpandConstant('{userdocs}');
end;

function GetProjectsRoot(Param: string): string;
begin
  Result := ProjectsPage.Values[0];
end;

// PATH manipulation — adds/removes the install directory to/from the user's
// PATH (HKCU\Environment). Necessary so `firepit-mcp` is resolvable from any
// shell or ConPTY child without absolute paths in committed settings.json
// files. Idempotent on add; tolerant on remove.
procedure EnvAddPath(Path: string);
var
  Paths: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Paths) then
    Paths := '';

  if Pos(';' + Uppercase(Path) + ';', ';' + Uppercase(Paths) + ';') > 0 then
    exit;

  if Paths = '' then
    Paths := Path
  else
    Paths := Paths + ';' + Path;

  RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Paths);
end;

procedure EnvRemovePath(Path: string);
var
  Paths: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Paths) then
    exit;

  P := Pos(';' + Uppercase(Path) + ';', ';' + Uppercase(Paths) + ';');
  if P = 0 then
    exit;

  Delete(Paths, P - 1, Length(Path) + 1);
  RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDataDir: string;
  MarkerPath: string;
  SettingsPath: string;
  Root: AnsiString;
begin
  if CurStep = ssPostInstall then
  begin
    AppDataDir := ExpandConstant('{userappdata}\Firepit');
    SettingsPath := AppDataDir + '\settings.json';
    MarkerPath := AppDataDir + '\first-run-projects-root.txt';

    if not DirExists(AppDataDir) then
      ForceDirectories(AppDataDir);

    // Only write the marker on a clean install (no settings.json yet). On
    // upgrade-over-install the user already has a configured projects root
    // and we must not stomp it.
    if not FileExists(SettingsPath) then
    begin
      Root := AnsiString(GetProjectsRoot(''));
      SaveStringToFile(MarkerPath, Root, False);
    end;

    if WizardIsTaskSelected('addtopath') then
      EnvAddPath(ExpandConstant('{app}'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    EnvRemovePath(ExpandConstant('{app}'));
end;
