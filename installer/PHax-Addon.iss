; PHax add-on installer (Inno Setup 6). Built by scripts\build-addon.ps1, which passes:
;   /DAddonName=<Name>  /DAddonVersion=x.y.z  /DAddonSource=<folder with the add-on's files>
;   /DMinPHaxVersion=x.y.z (the PHax it was built against)  /DOutputDir=<folder>
; Produces PHax-<Name>-Addon-<version>.exe. It installs into {PHax}\Addons\<Name>, which PHax setup never replaces,
; so the add-on survives PHax updates. It has its own entry in Windows Settings > Apps; uninstalling PHax removes it too.
; /RELAUNCH reopens PHax after a silent install.

#ifndef AddonName
  #error AddonName is required (build with scripts\build-addon.ps1)
#endif
#ifndef AddonVersion
  #define AddonVersion "1.0.0"
#endif
#ifndef MinPHaxVersion
  #define MinPHaxVersion "0.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "..\out\addons"
#endif
#define Root ".."
; Must match AppId in PHax.iss (there with the escaping brace doubled).
#define PHaxUninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7980E80-1D40-4932-9DBD-C95001E6E75F}_is1"

[Setup]
; One AppId per add-on, stable across its versions; PHax.iss removes "PHax.Addon.*" entries when PHax is uninstalled.
AppId=PHax.Addon.{#AddonName}
AppName=PHax {#AddonName} add-on
AppVersion={#AddonVersion}
AppVerName=PHax {#AddonName} add-on {#AddonVersion}
AppPublisher=Phan Software
VersionInfoVersion={#AddonVersion}
DefaultDirName={code:AddonDir}
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile={#Root}\PHax.App\Assets\PHax.ico
WizardSmallImageFile={#Root}\PHax.App\Assets\wizard-small.bmp
UninstallDisplayName=PHax {#AddonName} add-on
UninstallDisplayIcon={app}\..\..\App\PHax.exe
OutputDir={#OutputDir}
OutputBaseFilename=PHax-{#AddonName}-Addon-{#AddonVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no

[InstallDelete]
; Files of the previous version of this add-on (not its uninstaller); {app} is always {PHax}\Addons\{#AddonName}.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"

[Files]
Source: "{#AddonSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\..\..\App\PHax.exe"; Description: "Open PHax now"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\..\..\App\PHax.exe"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[UninstallRun]
; PHax keeps the add-on's DLL loaded; it reopens without the add-on.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PHax.exe"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
var
  PHaxDir: String;

function AddonDir(Param: String): String;
begin
  Result := PHaxDir + '\Addons\{#AddonName}';
end;

{ Compares dotted versions numerically: -1, 0 or 1. }
function CompareVersions(A, B: String): Integer;
var
  PartA, PartB: Integer;
  DotA, DotB: Integer;
begin
  Result := 0;
  while (Result = 0) and ((A <> '') or (B <> '')) do
  begin
    DotA := Pos('.', A); DotB := Pos('.', B);
    if DotA = 0 then begin PartA := StrToIntDef(A, 0); A := ''; end
    else begin PartA := StrToIntDef(Copy(A, 1, DotA - 1), 0); Delete(A, 1, DotA); end;
    if DotB = 0 then begin PartB := StrToIntDef(B, 0); B := ''; end
    else begin PartB := StrToIntDef(Copy(B, 1, DotB - 1), 0); Delete(B, 1, DotB); end;
    if PartA < PartB then Result := -1
    else if PartA > PartB then Result := 1;
  end;
end;

procedure ShowFailure(Message: String);
begin
  Log(Message);
  if not WizardSilent() then MsgBox(Message, mbError, MB_OK);
end;

function InitializeSetup(): Boolean;
var
  InstalledVersion: String;
begin
  Result := False;
  if not RegQueryStringValue(HKLM64, '{#PHaxUninstallKey}', 'InstallLocation', PHaxDir) then PHaxDir := '';
  PHaxDir := RemoveBackslashUnlessRoot(PHaxDir);
  if (PHaxDir = '') or not FileExists(PHaxDir + '\App\PHax.exe') then
  begin
    ShowFailure('PHax is not installed on this PC. Install PHax first, then run this add-on installer again.');
    Exit;
  end;
  if not RegQueryStringValue(HKLM64, '{#PHaxUninstallKey}', 'DisplayVersion', InstalledVersion) then InstalledVersion := '0';
  if CompareVersions(InstalledVersion, '{#MinPHaxVersion}') < 0 then
  begin
    ShowFailure('The {#AddonName} add-on needs PHax {#MinPHaxVersion} or later, but PHax ' + InstalledVersion +
         ' is installed. Update PHax first (Settings > General > Check for updates), then run this installer again.');
    Exit;
  end;
  Result := True;
end;

function RelaunchRequested(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  { The app has the previous version of the add-on loaded; the service does not use add-ons. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PHax.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
