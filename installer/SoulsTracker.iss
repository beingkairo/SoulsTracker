#ifndef AppVersion
  #error AppVersion must be provided by scripts/Build-Release.ps1.
#endif

#ifndef BuildOutput
  #error BuildOutput must be provided by scripts/Build-Release.ps1.
#endif

#define AppName "SoulsTracker"
#define AppPublisher "SoulsTracker"

[Setup]
AppId={{B4485F7A-7828-447D-9B55-4CA4A9A3851A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
SetupIconFile=..\assets\branding\souls-tracker-skull.ico
DefaultDirName={localappdata}\Programs\SoulsTracker
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=SoulsTrackerV1.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "{#BuildOutput}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\SoulsTracker.Desktop.exe"

[Run]
Filename: "{app}\SoulsTracker.Desktop.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\SoulsTracker"; Check: ShouldDeleteLocalSettings

[Code]
var
  DeleteLocalSettings: Boolean;

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

function ShouldDeleteLocalSettings(): Boolean;
begin
  Result := DeleteLocalSettings;
end;
