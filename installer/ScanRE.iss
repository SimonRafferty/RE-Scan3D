; ScanRE installer (Inno Setup 6). Built by installer\build.ps1, which compiles into build\app first.
;
; Installs the add-in DLLs, the command-line tool and the user guide, then registers the add-in
; (RegAsm /codebase), which adds the "Scan RE" CommandManager tab to SOLIDWORKS 2026 and later (older
; versions see the registration but the add-in stays dormant in them). Uninstall unregisters and
; removes everything it installed. Scan caches (<part>.scanre folders) are the user's data and are kept.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{2C8AAAAA-3125-4601-8483-EC6518E1A7D2}
AppName=ScanRE
AppVersion={#AppVersion}
AppVerName=ScanRE {#AppVersion}
AppPublisher=ScanRE
DefaultDirName={autopf}\ScanRE
DefaultGroupName=ScanRE
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=ScanRE-Setup-{#AppVersion}
UninstallDisplayName=ScanRE (scan reverse engineering for SOLIDWORKS)
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
SetupLogging=yes

[Files]
Source: "build\app\ScanRE.AddIn.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\ScanRE.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\ScanRE.Cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\USER-GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\USER-GUIDE.html"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\ScanRE user guide"; Filename: "{app}\USER-GUIDE.html"; Check: FileExists(ExpandConstant('{app}\USER-GUIDE.html'))
Name: "{group}\ScanRE user guide (text)"; Filename: "{app}\USER-GUIDE.md"
Name: "{group}\Uninstall ScanRE"; Filename: "{uninstallexe}"

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

{ SOLIDWORKS 2026 or later installed? Each version has HKLM\SOFTWARE\SolidWorks\SOLIDWORKS <year> }
function HasSolidWorks2026: Boolean;
var
  year: Integer;
begin
  Result := False;
  for year := 2026 to 2040 do
    if RegKeyExists(HKLM64, 'SOFTWARE\SolidWorks\SOLIDWORKS ' + IntToStr(year)) then
    begin
      Result := True;
      Exit;
    end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not HasSolidWorks2026 then
    if MsgBox('SOLIDWORKS 2026 or later was not found on this computer. ScanRE only loads in SOLIDWORKS 2026 and later.' + #13#10#13#10 +
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
  if not Exec(ExpandConstant('{dotnet4064}\RegAsm.exe'), '/codebase "' + ExpandConstant('{app}\ScanRE.AddIn.dll') + '"', '', SW_HIDE,
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
  dll := ExpandConstant('{app}\ScanRE.AddIn.dll');
  if FileExists(dll) then
    Exec(ExpandConstant('{dotnet4064}\RegAsm.exe'), '/u "' + dll + '"', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;
