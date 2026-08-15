#define AppName "PC Rental 设备管理"
#ifndef AppVersion
  #define AppVersion "0.6.2"
#endif
#define AppPublisher "PC Rental"
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
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\RentDeviceAgent.exe

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "README-install.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "logo.svg"; DestDir: "{app}"; Flags: ignoreversion

[Code]
var
  CodePage: TInputQueryWizardPage;
  ExistingInstall: Boolean;

procedure InitializeWizard;
begin
  CodePage := CreateInputQueryPage(wpSelectDir, '设备绑定', '设备绑定方式', '客户端会先读取 BIOS 序列号自动绑定；如果网站没有相同序列号，请填写管理员生成的 6 位访问码。');
  CodePage.Add('6 位访问码（自动绑定时可留空）:', False);
  ExistingInstall := FileExists(ExpandConstant('{commonappdata}\RentDeviceAgent\state.json'));
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ExistingInstall and (PageID = CodePage.ID);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = CodePage.ID) and not ExistingInstall then
  begin
    if (Length(CodePage.Values[0]) <> 0) and ((Length(CodePage.Values[0]) <> 6) or (CodePage.Values[0] < '000000') or (CodePage.Values[0] > '999999')) then
    begin
      MsgBox('访问码必须为空，或填写恰好 6 位数字。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var ResultCode: Integer; Params: String;
begin
  if CurStep = ssPostInstall then
  begin
    Params := '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(ExpandConstant('{app}\install-service.ps1')) + ' -InstallPath ' + AddQuotes(ExpandConstant('{app}'));
    if not ExistingInstall then Params := Params + ' -SetupCode ' + AddQuotes(CodePage.Values[0]);
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('客户端服务安装失败，错误代码: ' + IntToStr(ResultCode) + #13#10 + '详细日志：' + ExpandConstant('{commonappdata}\RentDeviceAgent\install-service.log'), mbError, MB_OK);
    if FileExists(ExpandConstant('{app}\RentDeviceAgent.exe')) then
      Exec(ExpandConstant('{app}\RentDeviceAgent.exe'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  // A never-bound installation has no state.json and may be removed directly.
  Result := (not FileExists(ExpandConstant('{commonappdata}\RentDeviceAgent\state.json'))) or
    FileExists(ExpandConstant('{commonappdata}\RentDeviceAgent\unbound.flag'));
  if not Result then
    MsgBox('请先在网站“绑定设备”页面解绑此设备。解绑成功后，客户端界面显示“设备未绑定”，才能卸载。', mbError, MB_OK);
end;

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop RentDeviceAgent"; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete RentDeviceAgent"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\RentDeviceAgent"

[Registry]
; HKLM Run applies to every Windows user who signs in, not only the administrator who installed it.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PC Rental Device Agent UI"; ValueData: "{app}\RentDeviceAgent.exe"; Flags: uninsdeletevalue

[Icons]
Name: "{autodesktop}\PC Rental 设备管理"; Filename: "{app}\RentDeviceAgent.exe"
Name: "{group}\PC Rental 设备管理"; Filename: "{app}\RentDeviceAgent.exe"
Name: "{commonstartup}\PC Rental 设备管理"; Filename: "{app}\RentDeviceAgent.exe"; WorkingDir: "{app}"
