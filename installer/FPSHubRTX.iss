; FPS Hub RTX — Inno Setup 6 script
; Build:  ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\artifacts\publish /O..\artifacts FPSHubRTX.iss
; (build\build.ps1 does this for you.)
;
; - Per-user install by default (no administrator rights needed); "Install for all users" is offered
;   and then asks for elevation.
; - Upgrades install over the previous version (same AppId); the running app is closed first.
; - User data (%LOCALAPPDATA%\FPSHubRTX: library, settings, statistics) is never touched by upgrades.
; - Uninstalling never touches installed games. Deleting FPS Hub's own data is optional and off by default.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

#define AppName "FPS Hub RTX"
#define AppPublisher "Clickys Gaming Optimizer"
#define AppExeName "FPSHubRTX.exe"

[Setup]
AppId={{6B2E7A61-3F1C-4C8E-9C1D-6A0E5B9F2D41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=FPSHubRTX-Setup
SetupIconFile=..\assets\FPSHubRTX.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
AppMutex=FPSHubRTX.SingleInstance.v1
SetupLogging=yes
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "Shortcuts:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; PresentMon (Intel, MIT licence) — the frame-presentation monitor used for FPS measurement.
Source: "{#SourceDir}\tools\PresentMon\*"; DestDir: "{app}\tools\PresentMon"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion isreadme
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion
Source: "..\docs\TROUBLESHOOTING.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\FPS-TELEMETRY.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Remove the optional "Start with Windows" entry the app may have created (per-user, never created here).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "FPSHubRTX"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only files the app itself writes next to the exe in portable mode. Games are never touched.
Type: filesandordirs; Name: "{app}\Data"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\FPSHubRTX');
    if DirExists(DataDir) and (not UninstallSilent) then
    begin
      if MsgBox('Also delete your FPS Hub RTX library, settings and play statistics?' + #13#10 + #13#10 +
                'Folder: ' + DataDir + #13#10 + #13#10 +
                'Your installed games are NOT affected either way.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        if not DelTree(DataDir, True, True, True) then
          MsgBox('Some files could not be deleted. You can remove the folder manually: ' + DataDir, mbInformation, MB_OK);
      end;
    end;
  end;
end;
