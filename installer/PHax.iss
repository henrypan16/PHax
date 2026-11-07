; PHax installer (Inno Setup 6). Built by scripts\build.ps1, which publishes to ..\out first and passes
; /DAppVersion=x.y.z. Produces out\installer\PHax-Setup-<version>.exe, or PHax-<Edition>-Setup-<version>.exe when
; /DEdition=<Name> is passed (the integration DLL is already in out\App). Both editions share the AppId, so
; installing one over the other switches edition in place.
;
; Network role: new installs get a page to choose it. Upgrades keep the existing choice (change it in Settings > Network).
; Unattended installs can set it on the command line:
;   /NETWORKMODE=server   this PC has the modem, nothing listens on the network (the default)
;   /NETWORKMODE=shared   this PC has the modem and other PCs may connect; /PORT=n to change the port
;   /NETWORKMODE=client   uses another PC's modem; /SERVER=name-or-ip and /PAIRCODE=code from Add a PC on the server

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "PHax"
#ifdef Edition
  #define EditionPrefix Edition + "-"
  #define EditionLabel " " + Edition
  #define StartupNote " (needed for " + Edition + " faxes)"
#else
  #define EditionPrefix ""
  #define EditionLabel ""
  #define StartupNote ""
#endif
#define Root ".."

[Setup]
; Keep AppId unchanged forever: it is how upgrades find the existing installation.
; (Installs from before the rename used {6D0C3F5E-...} as "FTIFax"; see RemoveLegacyFTIFax below.)
AppId={{A7980E80-1D40-4932-9DBD-C95001E6E75F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName}{#EditionLabel} {#AppVersion}
AppPublisher=Phan Software
AppCopyright=Copyright (C) 2026 Henry Phan (Phan Software)
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Phan Software
LicenseFile={#Root}\LICENSE
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile={#Root}\PHax.App\Assets\PHax.ico
WizardSmallImageFile={#Root}\PHax.App\Assets\wizard-small.bmp
UninstallDisplayIcon={app}\App\PHax.exe
UninstallDisplayName={#AppName}
OutputDir={#Root}\out\installer
OutputBaseFilename=PHax-{#EditionPrefix}Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start PHax automatically when a user signs in{#StartupNote}"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[InstallDelete]
; Integrations from a previous install; this edition's own (if any) is reinstalled from out\App.
Type: files; Name: "{app}\App\PHax.Integration.*"

[Files]
Source: "{#Root}\out\App\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Root}\out\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "service-setup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#Root}\NOTICE"; DestDir: "{app}"; DestName: "NOTICE.txt"; Flags: ignoreversion
Source: "{#Root}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Dirs]
; Shared database, documents and logs: the service (SYSTEM) and every user's app read and write here.
Name: "{commonappdata}\PHax"; Permissions: users-modify; Flags: uninsneveruninstall

[Icons]
; AppUserModelID must match AppNotifications.AppUserModelId so notifications and taskbar grouping belong to PHax.
Name: "{group}\PHax"; Filename: "{app}\App\PHax.exe"; WorkingDir: "{app}\App"; AppUserModelID: "PHax.App"
Name: "{autodesktop}\PHax"; Filename: "{app}\App\PHax.exe"; WorkingDir: "{app}\App"; AppUserModelID: "PHax.App"; Tasks: desktopicon
Name: "{commonstartup}\PHax"; Filename: "{app}\App\PHax.exe"; Parameters: "--minimized"; WorkingDir: "{app}\App"; AppUserModelID: "PHax.App"; Tasks: startup

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\service-setup.ps1"" -Action install -InstallDir ""{app}"""; StatusMsg: "Installing and starting the PHax service..."; Flags: runhidden waituntilterminated; BeforeInstall: ApplyNetworkRole; AfterInstall: PairWithServer
Filename: "{app}\App\PHax.exe"; Description: "Open PHax now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PHax.exe"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\service-setup.ps1"" -Action remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
var
  RolePage: TInputOptionWizardPage;
  ClientPage: TInputQueryWizardPage;

function NetworkConfigPath(): String;
begin
  Result := ExpandConstant('{commonappdata}\PHax\network.json');
end;

{ What to apply: "server", "shared" or "client", or empty to leave an existing configuration alone. }
function ChosenRole(): String;
begin
  Result := Lowercase(ExpandConstant('{param:NETWORKMODE|}'));
  if Result <> '' then Exit;
  if FileExists(NetworkConfigPath()) then Exit;
  case RolePage.SelectedValueIndex of
    0: Result := 'server';
    1: Result := 'shared';
    2: Result := 'client';
  end;
end;

{ Quotes would end the command-line argument early; neither value can legitimately contain one. }
function Clean(const Value: String): String;
begin
  Result := Trim(Value);
  StringChangeEx(Result, '"', '', True);
end;

function ServerAddressValue(): String;
begin
  Result := ExpandConstant('{param:SERVER|}');
  if Result = '' then Result := ClientPage.Values[0];
  Result := Clean(Result);
end;

function PairingCodeValue(): String;
begin
  Result := ExpandConstant('{param:PAIRCODE|}');
  if Result = '' then Result := ClientPage.Values[1];
  Result := Clean(Result);
end;

procedure InitializeWizard();
begin
  RolePage := CreateInputOptionPage(wpSelectTasks, 'Network', 'How will this PC send and receive faxes?',
    'You can change this later in PHax under Settings > Network.', True, False);
  RolePage.Add('This PC has the fax modem and is the only PC using it');
  RolePage.Add('This PC has the fax modem, and other PCs on the local network will use it');
  RolePage.Add('This PC will use the fax modem on another PC running PHax');
  RolePage.SelectedValueIndex := 0;

  ClientPage := CreateInputQueryPage(RolePage.ID, 'Fax server', 'Which PC has the fax modem?',
    'On that PC, open PHax, go to Settings > Network and click Add a PC. The code can also be entered later in Settings > Network.');
  ClientPage.Add('Server address (computer name or IP address):', False);
  ClientPage.Add('Pairing code (optional):', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = RolePage.ID then
    Result := (ExpandConstant('{param:NETWORKMODE|}') <> '') or FileExists(NetworkConfigPath())
  else if PageID = ClientPage.ID then
    Result := ChosenRole() <> 'client';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ClientPage.ID) and (Clean(ClientPage.Values[1]) <> '') and (Clean(ClientPage.Values[0]) = '') then
  begin
    MsgBox('Enter the server address that goes with this pairing code.', mbError, MB_OK);
    Result := False;
  end;
end;

{ Runs before the service starts, so it comes up in the right role the first time. }
procedure ApplyNetworkRole();
var
  ResultCode: Integer;
  Args, Role, Port: String;
begin
  Role := ChosenRole();
  if Role = '' then Exit;
  Args := '--configure-network --mode ';
  if Role = 'client' then
  begin
    Args := Args + 'client';
    if ServerAddressValue() <> '' then Args := Args + ' --server "' + ServerAddressValue() + '"';
  end
  else
  begin
    Args := Args + 'server';
    if Role = 'shared' then Args := Args + ' --allow-clients';
    Port := Clean(ExpandConstant('{param:PORT|}'));
    if Port <> '' then Args := Args + ' --port "' + Port + '"';
  end;
  if not Exec(ExpandConstant('{app}\Service\PHax.Service.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Log('Could not apply the network role, exit code ' + IntToStr(ResultCode));
end;

{ A failed pairing must not fail the install: the PC is set up as a client either way and can pair from Settings. }
procedure PairWithServer();
var
  ResultCode: Integer;
begin
  if (ChosenRole() <> 'client') or (ServerAddressValue() = '') or (PairingCodeValue() = '') then Exit;
  if Exec(ExpandConstant('{app}\Service\PHax.Service.exe'), '--pair --server "' + ServerAddressValue() + '" --code "' + PairingCodeValue() + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then Exit;
  Log('Pairing failed, exit code ' + IntToStr(ResultCode));
  if not WizardSilent() then
    MsgBox('PHax could not connect to the fax server. Check that the server PC is on, then enter the address and a new pairing code in PHax under Settings > Network.', mbInformation, MB_OK);
end;

procedure StopRunningCopies();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PHax.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "Stop-Service PHax -Force -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PHax.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

const
  LegacyUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6D0C3F5E-7A2B-4F0E-9B8B-4F7A1C2E5D11}_is1';

{ Before the rename the product was "FTIFax" (service FTIFax, C:\Program Files\FTIFax). Stop it, remove its service
  and run its uninstaller silently; a silent uninstall keeps all data, which the PHax service then moves from
  ProgramData\FTIFax to ProgramData\PHax on its first start. }
procedure RemoveLegacyFTIFax();
var
  ResultCode: Integer;
  Uninstaller: String;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM FTIFax.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "Stop-Service FTIFax -Force -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM FTIFax.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete FTIFax', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if RegQueryStringValue(HKLM, LegacyUninstallKey, 'UninstallString', Uninstaller) then
    Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Upgrades: the service and app must not hold the files that are about to be replaced. }
  StopRunningCopies();
  RemoveLegacyFTIFax();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() then
  begin
    if MsgBox('Also delete all received and sent faxes, settings and logs in ' + ExpandConstant('{commonappdata}\PHax') + '?' + #13#10#13#10 +
              'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{commonappdata}\PHax'), True, True, True);
  end;
end;
