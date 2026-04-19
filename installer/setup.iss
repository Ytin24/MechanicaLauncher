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
Name: "associatemrpack"; Description: "Associate Modrinth modpacks (.mrpack) with Mechanica Launcher"; GroupDescription: "File associations:"

[Registry]
Root: HKA; Subkey: "Software\Classes\.mrpack"; ValueType: string; ValueName: ""; ValueData: "MechanicaLauncher.Modpack"; Flags: uninsdeletevalue; Tasks: associatemrpack
Root: HKA; Subkey: "Software\Classes\.mrpack\OpenWithProgids"; ValueType: string; ValueName: "MechanicaLauncher.Modpack"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatemrpack
Root: HKA; Subkey: "Software\Classes\MechanicaLauncher.Modpack"; ValueType: string; ValueName: ""; ValueData: "Modrinth Modpack"; Flags: uninsdeletekey; Tasks: associatemrpack
Root: HKA; Subkey: "Software\Classes\MechanicaLauncher.Modpack\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\MechanicaLauncher.exe,0"; Flags: uninsdeletekey; Tasks: associatemrpack
Root: HKA; Subkey: "Software\Classes\MechanicaLauncher.Modpack\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\MechanicaLauncher.exe"" ""%1"""; Flags: uninsdeletekey; Tasks: associatemrpack

[Run]
Filename: "{app}\WindowsAppRuntimeInstall.exe"; Parameters: "--quiet"; StatusMsg: "Installing Windows App Runtime..."; Flags: waituntilterminated skipifdoesntexist
Filename: "{app}\MechanicaLauncher.exe"; Description: "Launch Mechanica Launcher"; Flags: nowait postinstall skipifsilent
