; RE-Scan3D installer (Inno Setup 6). Built by installer\build.ps1, which compiles into build\app first.
;
; Installs the add-in DLLs, the command-line tool and the user guide, then registers the add-in
; (RegAsm /codebase). SOLIDWORKS keeps ONE add-in registration and ONE load-at-startup flag for all its installed
; versions, so this single install adds the "RE-Scan3D" CommandManager tab to EVERY SOLIDWORKS 2018 or later on
; the PC (older versions see the registration but the add-in stays dormant in them). The versions found are listed
; on the Ready and Finished pages. Uninstall unregisters and removes everything it installed. Scan caches
; (<part>.res3d folders) are the user's data and are kept.

#ifndef AppVersion
  #define AppVersion "0.2.1"
#endif

[Setup]
AppId={{2C8AAAAA-3125-4601-8483-EC6518E1A7D2}
AppName=RE-Scan3D
AppVersion={#AppVersion}
AppVerName=RE-Scan3D {#AppVersion}
AppPublisher=RE-Scan3D
DefaultDirName={autopf}\RE-Scan3D
DefaultGroupName=RE-Scan3D
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=RE-Scan3D-Setup-{#AppVersion}
UninstallDisplayName=RE-Scan3D (scan reverse engineering for SOLIDWORKS)
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
SetupLogging=yes
; the load-at-startup flag is per user (HKCU) by SOLIDWORKS' design; the add-in's own registration writes it too
UsedUserAreasWarning=no

[Files]
Source: "build\app\RES3D.AddIn.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\RES3D.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\RES3D.Cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\USER-GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\USER-GUIDE.html"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Registry]
; load at start-up in every SOLIDWORKS version (the flag is shared by all of them)
Root: HKCU; Subkey: "Software\SolidWorks\AddInsStartup\{{5dc170a8-86cf-42c8-8397-a024ac0657e0}"; ValueType: dword; ValueName: ""; ValueData: 1; Flags: uninsdeletekey
; a development-only setting that limits the add-in to one SOLIDWORKS version: an installed copy runs in all of them
Root: HKCU; Subkey: "Software\RES3D"; ValueType: none; ValueName: "OnlyRevision"; Flags: deletevalue

[Icons]
Name: "{group}\RE-Scan3D user guide"; Filename: "{app}\USER-GUIDE.html"; Check: FileExists(ExpandConstant('{app}\USER-GUIDE.html'))
Name: "{group}\RE-Scan3D user guide (text)"; Filename: "{app}\USER-GUIDE.md"
Name: "{group}\Uninstall RE-Scan3D"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\USER-GUIDE.html"; Description: "Open the user guide"; Flags: postinstall shellexec skipifsilent nowait skipifdoesntexist

[Code]
function SolidWorksRunning: Boolean;
var
  rc: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq SLDWORKS.exe" | find /I "SLDWORKS.exe"',
                 '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
end;

{ Is SOLIDWORKS <year> really installed? Its registry key can outlive it (an older version's key may even point at a
  newer version's folder), so the SLDWORKS.exe in its folder must be that version: file version major = year - 1992. }
function VersionInstalled(year: Integer): Boolean;
var
  dir, ver: String;
begin
  Result := False;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\SolidWorks\SOLIDWORKS ' + IntToStr(year) + '\Setup', 'SolidWorks Folder', dir) then Exit;
  if not GetVersionNumbersString(AddBackslash(dir) + 'SLDWORKS.exe', ver) then Exit;
  Result := StrToIntDef(Copy(ver, 1, Pos('.', ver) - 1), 0) = year - 1992;
end;

{ The SOLIDWORKS versions from 2018 on that are installed }
function FoundVersions: String;
var
  year: Integer;
begin
  Result := '';
  for year := 2018 to 2040 do
    if VersionInstalled(year) then
    begin
      if Result <> '' then Result := Result + ', ';
      Result := Result + IntToStr(year);
    end;
end;

function HasSolidWorks2018: Boolean;
begin
  Result := FoundVersions <> '';
end;

{ the Ready page: which SOLIDWORKS versions get the add-in }
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + 'RE-Scan3D will be added to:' + NewLine;
  if FoundVersions <> '' then
    Result := Result + Space + 'SOLIDWORKS ' + FoundVersions + NewLine
  else
    Result := Result + Space + '(no SOLIDWORKS 2018 or later found - it will load once one is installed)' + NewLine;
end;

{ the Finished page says where to find it }
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    if FoundVersions <> '' then
      WizardForm.FinishedLabel.Caption := 'RE-Scan3D is installed for SOLIDWORKS ' + FoundVersions + '.' + #13#10#13#10 +
        'Start SOLIDWORKS and open a part: the RE-Scan3D tab is in the CommandManager.'
    else
      WizardForm.FinishedLabel.Caption := 'RE-Scan3D is installed. It loads in SOLIDWORKS 2018 or later.';
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not HasSolidWorks2018 then
    if MsgBox('SOLIDWORKS 2018 or later was not found on this computer. RE-Scan3D only loads in SOLIDWORKS 2018 and later.' + #13#10#13#10 +
              'Install anyway?', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  while SolidWorksRunning do
    if MsgBox('Please close SOLIDWORKS first (it locks the add-in files and only loads new add-ins at start-up).' + #13#10#13#10 +
              'Close it, then press Retry.', mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  rc: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := 'Registering the SOLIDWORKS add-in...';
  if not Exec(ExpandConstant('{dotnet4064}\RegAsm.exe'), '/codebase "' + ExpandConstant('{app}\RES3D.AddIn.dll') + '"', '', SW_HIDE,
              ewWaitUntilTerminated, rc) or (rc <> 0) then
    MsgBox(Format('Registering the add-in failed (RegAsm exit code %d).', [rc]) + #13#10#13#10 +
           'Run the installer again as administrator. The setup log is ' + ExpandConstant('{log}') + '.', mbCriticalError, MB_OK);
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  while SolidWorksRunning do
    if MsgBox('Please close SOLIDWORKS first, then press Retry.', mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  dll: String;
  rc: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  dll := ExpandConstant('{app}\RES3D.AddIn.dll');
  if FileExists(dll) then
    Exec(ExpandConstant('{dotnet4064}\RegAsm.exe'), '/u "' + dll + '"', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;
