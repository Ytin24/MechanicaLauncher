[Setup]
AppName=Mechanica Launcher
AppVersion={#AppVersion}
AppPublisher=Ytin24
AppPublisherURL=https://github.com/Ytin24/MechanicaLauncher
DefaultDirName={autopf}\MechanicaLauncher
DefaultGroupName=Mechanica Launcher
OutputDir=..\output
OutputBaseFilename=MechanicaLauncher-{#AppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\MechanicaLauncher.exe
WizardStyle=modern
DisableProgramGroupPage=yes
PrivilegesRequired=lowest

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Mechanica Launcher"; Filename: "{app}\MechanicaLauncher.exe"
Name: "{autodesktop}\Mechanica Launcher"; Filename: "{app}\MechanicaLauncher.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\WindowsAppRuntimeInstall.exe"; Parameters: "--quiet"; StatusMsg: "Installing Windows App Runtime..."; Flags: waituntilterminated skipifnotexists
Filename: "{app}\MechanicaLauncher.exe"; Description: "Launch Mechanica Launcher"; Flags: nowait postinstall skipifsilent
