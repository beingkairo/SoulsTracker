#ifndef AppVersion
  #error AppVersion must be provided by scripts/Build-Release.ps1.
#endif

#ifndef BuildOutput
  #error BuildOutput must be provided by scripts/Build-Release.ps1.
#endif

#ifndef WebView2Bootstrapper
  #error WebView2Bootstrapper must be provided by scripts/Build-Release.ps1.
#endif

#ifndef WebView2BootstrapperSha256
  #error WebView2BootstrapperSha256 must be provided by scripts/Build-Release.ps1.
#endif

#define AppName "SoulsTracker"
#define AppPublisher "SoulsTracker"

[Setup]
AppId={{B4485F7A-7828-447D-9B55-4CA4A9A3851A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
SetupIconFile=..\assets\branding\souls-tracker-skull-compact.ico
DefaultDirName={localappdata}\Programs\SoulsTracker
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
LicenseFile=..\docs\WEBVIEW2_RUNTIME_LICENSE.txt
OutputDir=Output
OutputBaseFilename=SoulsTrackerV{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "{#BuildOutput}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "{#WebView2Bootstrapper}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\SoulsTracker.Desktop.exe"

[Run]
Filename: "{app}\SoulsTracker.Desktop.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteLocalSettings: Boolean;

const
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Absent = 0;
  WebView2Present = 1;
  WebView2Malformed = 2;
  WebView2PollIntervalMilliseconds = 250;
  WebView2PollMaximumAttempts = 480;

function IsValidWebView2Version(Value: String): Boolean;
var
  Component: String;
  ComponentCount: Integer;
  DotPosition: Integer;
  Number: Integer;
  SawPositive: Boolean;
begin
  Value := Trim(Value);
  ComponentCount := 0;
  SawPositive := False;

  while Value <> '' do
  begin
    DotPosition := Pos('.', Value);
    if DotPosition = 0 then
    begin
      Component := Value;
      Value := '';
    end
    else
    begin
      Component := Copy(Value, 1, DotPosition - 1);
      Delete(Value, 1, DotPosition);
    end;

    ComponentCount := ComponentCount + 1;
    Number := StrToIntDef(Component, -1);
    if (Component = '') or (Number < 0) then
    begin
      Result := False;
      exit;
    end;
    if Number > 0 then
      SawPositive := True;
  end;

  Result := (ComponentCount = 4) and SawPositive;
end;

procedure InspectWebView2Registration(RootKey: Integer; var SawValid: Boolean; var SawMalformed: Boolean);
var
  Version: String;
begin
  if not RegValueExists(RootKey, WebView2ClientKey, 'pv') then
    exit;

  if not RegQueryStringValue(RootKey, WebView2ClientKey, 'pv', Version) then
  begin
    SawMalformed := True;
    exit;
  end;

  Version := Trim(Version);
  if (Version = '') or (Version = '0.0.0.0') then
    exit;

  if IsValidWebView2Version(Version) then
    SawValid := True
  else
    SawMalformed := True;
end;

function DetectWebView2Runtime(): Integer;
var
  SawValid: Boolean;
  SawMalformed: Boolean;
begin
  SawValid := False;
  SawMalformed := False;
  InspectWebView2Registration(HKLM32, SawValid, SawMalformed);
  InspectWebView2Registration(HKLM64, SawValid, SawMalformed);
  InspectWebView2Registration(HKCU32, SawValid, SawMalformed);
  InspectWebView2Registration(HKCU64, SawValid, SawMalformed);

  if SawValid then
    Result := WebView2Present
  else if SawMalformed then
    Result := WebView2Malformed
  else
    Result := WebView2Absent;
end;

function WaitForWebView2Runtime(): Integer;
var
  Attempt: Integer;
  ProgressPage: TOutputProgressWizardPage;
begin
  Result := WebView2Absent;
  ProgressPage := CreateOutputProgressPage(
    'Installing Microsoft WebView2 Runtime',
    'Waiting for Microsoft WebView2 Runtime registration to complete.');
  ProgressPage.SetText('Finishing Microsoft WebView2 Runtime installation...', '');
  ProgressPage.SetProgress(0, WebView2PollMaximumAttempts);
  ProgressPage.Show;
  try
    for Attempt := 1 to WebView2PollMaximumAttempts do
    begin
      Result := DetectWebView2Runtime();
      ProgressPage.SetProgress(Attempt, WebView2PollMaximumAttempts);
      if Result = WebView2Present then
        exit;
      if Attempt < WebView2PollMaximumAttempts then
        Sleep(WebView2PollIntervalMilliseconds);
    end;
  finally
    ProgressPage.Hide;
    ProgressPage.Free;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  BootstrapperPath: String;
  Detection: Integer;
  ExitCode: Integer;
begin
  Result := '';
  Detection := DetectWebView2Runtime();
  if Detection = WebView2Present then
    exit;
  if Detection = WebView2Malformed then
  begin
    Result := 'Microsoft WebView2 Runtime detection returned invalid registration data. Setup cannot continue.';
    exit;
  end;

  WizardForm.StatusLabel.Caption := 'Installing Microsoft WebView2 Runtime...';
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  BootstrapperPath := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
  if CompareText(GetSHA256OfFile(BootstrapperPath), '{#WebView2BootstrapperSha256}') <> 0 then
  begin
    Result := 'The Microsoft WebView2 Runtime installer is invalid. Setup cannot continue.';
    exit;
  end;

  if not Exec(BootstrapperPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Microsoft WebView2 Runtime installation could not start. Setup cannot continue.';
    exit;
  end;
  if ExitCode <> 0 then
  begin
    Result := 'Microsoft WebView2 Runtime installation failed. Setup cannot continue.';
    exit;
  end;

  Detection := WaitForWebView2Runtime();
  if Detection = WebView2Malformed then
    Result := 'Microsoft WebView2 Runtime detection returned invalid registration data after installation. Setup cannot continue.'
  else if Detection <> WebView2Present then
    Result := 'Microsoft WebView2 Runtime is still unavailable after installation. Setup cannot continue.';
end;

function InitializeUninstall(): Boolean;
begin
  if UninstallSilent() then
    DeleteLocalSettings := False
  else
    DeleteLocalSettings := MsgBox(
      'Delete SoulsTracker local settings and overlay configuration?' + #13#10 +
      'Choose No to retain settings for a later reinstall or upgrade.',
      mbConfirmation,
      MB_YESNO
    ) = IDYES;
  Result := True;
end;

procedure DeleteSoulsTrackerSettings;
var
  LocalRoot: String;
  RoamingRoot: String;
begin
  LocalRoot := ExpandConstant('{localappdata}\SoulsTracker');
  RoamingRoot := ExpandConstant('{userappdata}\SoulsTracker');
  DelTree(LocalRoot + '\tracker.db', False, True, False);
  DelTree(LocalRoot + '\tracker.db-wal', False, True, False);
  DelTree(LocalRoot + '\tracker.db-shm', False, True, False);
  DelTree(LocalRoot + '\tracker.db-journal', False, True, False);
  DelTree(LocalRoot + '\tracker.db.writer.lock', False, True, False);
  DelTree(LocalRoot + '\tracker.db.pre-migration-*.bak', False, True, False);
  DelTree(LocalRoot + '\hosted-pairing.private', False, True, False);
  DelTree(LocalRoot + '\overlay-setup.private', False, True, False);
  DelTree(LocalRoot + '\AppearancePreview', True, True, True);
  DelTree(RoamingRoot + '\state.json', False, True, False);
  DelTree(RoamingRoot + '\soulstracker-legacy-backup-*.json', False, True, False);
  RemoveDir(RoamingRoot);
  RemoveDir(LocalRoot);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // [UninstallDelete] checks run while Setup records the uninstall log, before
  // InitializeUninstall can receive the user's choice.
  if (CurUninstallStep = usPostUninstall) and DeleteLocalSettings then
    DeleteSoulsTrackerSettings;
  // Retry the empty root after the rest of uninstall has finished. RemoveDir
  // leaves any directory containing unrelated user files untouched.
  if (CurUninstallStep = usDone) and DeleteLocalSettings then
    RemoveDir(ExpandConstant('{localappdata}\SoulsTracker'));
end;
