; PHax installer (Inno Setup 6). Built by scripts\build.ps1, which publishes to ..\out first and passes
; /DAppVersion=x.y.z. Produces out\installer\PHax-Setup-<version>.exe, or PHax-<Edition>-Setup-<version>.exe when
; /DEdition=<Name> is passed (the integration DLL is already in out\App). Both editions share the AppId, so
; installing one over the other switches edition in place.

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

[Dirs]
; Shared database, documents and logs: the service (SYSTEM) and every user's app read and write here.
Name: "{commonappdata}\PHax"; Permissions: users-modify; Flags: uninsneveruninstall

[Icons]
; AppUserModelID must match AppNotifications.AppUserModelId so notifications and taskbar grouping belong to PHax.
Name: "{group}\PHax"; Filename: "{app}\App\PHax.exe"; WorkingDir: "{app}\App"; AppUserModelID: "PHax.App"
Name: "{autodesktop}\PHax"; Filename: "{app}\App\PHax.exe"; WorkingDir: "{app}\App"; AppUserModelID: "PHax.App"; Tasks: desktopicon
Name: "{commonstartup}\PHax"; Filename: "{app}\App\PHax.exe"; Parameters: "--minimized"; WorkingDir: "{app}\App"; AppUserModelID: "PHax.App"; Tasks: startup

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\service-setup.ps1"" -Action install -InstallDir ""{app}"""; StatusMsg: "Installing and starting the PHax service..."; Flags: runhidden waituntilterminated
Filename: "{app}\App\PHax.exe"; Description: "Open PHax now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PHax.exe"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\service-setup.ps1"" -Action remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
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
