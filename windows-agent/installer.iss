#define AppName "Rent Device Agent"
#define AppVersion "1.0.0"
#define AppPublisher "Rent"
#define AppExeName "RentDeviceAgent.exe"

[Setup]
AppId={{A3AA4A62-40EA-4A54-9B7B-7C6E2E4A1D91}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\RentDeviceAgent
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=RentDeviceAgent-Setup
Compression=lzma
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "README-install.txt"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File \"{app}\install-service.ps1\" -InstallPath \"{app}\""; Verb: runas; Flags: waituntilterminated

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop RentDeviceAgent"; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete RentDeviceAgent"; Flags: runhidden waituntilterminated
