#ifndef SourceDir
  #error SourceDir must point to the published win-x64 directory.
#endif
#ifndef OutputDir
  #error OutputDir must point to the installer output directory.
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef WorkspaceRoot
  #error WorkspaceRoot must point to the repository root.
#endif
#ifndef InstallerLogo
  #error InstallerLogo must point to the generated PNG logo.
#endif

#define AppName "Vision Studio"
#define AppExeName "VisionStudio.exe"
#define AppPublisher "Vision Studio"
#define ProjectProgId "VisionStudio.Project"

[Setup]
AppId={{5B45F77B-B49B-48D7-A85B-47AE36CD39A8}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Installer
VersionInfoProductName={#AppName}
DefaultDirName={localappdata}\Programs\Vision Studio
DefaultGroupName=Vision Studio
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=VisionStudio-{#AppVersion}-win-x64-setup
SetupIconFile={#WorkspaceRoot}\src\VisionStudio.App\Assets\VisionStudio.ico
WizardSmallImageFile={#InstallerLogo}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dark polar includetitlebar
WizardSizePercent=110,110
WizardImageBackColor=$1E1E1E
WizardSmallImageBackColor=$1E1E1E
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Vision Studio"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\Vision Studio"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\.vstudio"; ValueType: string; ValueName: ""; ValueData: "{#ProjectProgId}"; Flags: uninsdeletevalue; Check: not IsTestInstall
Root: HKCU; Subkey: "Software\Classes\{#ProjectProgId}"; ValueType: string; ValueName: ""; ValueData: "Vision Studio Project"; Flags: uninsdeletekey; Check: not IsTestInstall
Root: HKCU; Subkey: "Software\Classes\{#ProjectProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Check: not IsTestInstall
Root: HKCU; Subkey: "Software\Classes\{#ProjectProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Check: not IsTestInstall

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function IsTestInstall: Boolean;
begin
  Result := CompareText(ExpandConstant('{param:TESTINSTALL|0}'), '1') = 0;
end;

procedure InitializeWizard;
begin
  WizardForm.Color := $001E1E1E;
  WizardForm.WelcomeLabel1.Font.Color := $00FFFFFF;
  WizardForm.WelcomeLabel2.Font.Color := $00CCCCCC;
  WizardForm.FinishedHeadingLabel.Font.Color := $00FFFFFF;
  WizardForm.FinishedLabel.Font.Color := $00CCCCCC;
end;
