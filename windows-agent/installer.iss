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
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\RentDeviceAgent.exe

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "README-install.txt"; DestDir: "{app}"; Flags: ignoreversion

[Code]
var
  CodePage: TInputQueryWizardPage;
  ExistingInstall: Boolean;

procedure InitializeWizard;
begin
  CodePage := CreateInputQueryPage(wpSelectDir, '设备绑定', '输入管理员生成的 6 位访问码', '请从网站设备详情页复制 6 位访问码。安装程序会自动绑定本机设备并注册 Windows Service。');
  CodePage.Add('6 位访问码:', False);
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
    if (Length(CodePage.Values[0]) <> 6) or (CodePage.Values[0] < '000000') or (CodePage.Values[0] > '999999') then
    begin
      MsgBox('请输入恰好 6 位数字访问码。', mbError, MB_OK);
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
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('客户端服务安装失败，请确认使用管理员权限后重试。错误代码: ' + IntToStr(ResultCode), mbError, MB_OK);
  end;
end;

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop RentDeviceAgent"; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete RentDeviceAgent"; Flags: runhidden waituntilterminated

[Icons]
Name: "{autodesktop}\Rent Device Agent"; Filename: "{app}\RentDeviceAgent.exe"
Name: "{group}\Rent Device Agent"; Filename: "{app}\RentDeviceAgent.exe"
Name: "{userstartup}\Rent Device Agent"; Filename: "{app}\RentDeviceAgent.exe"; WorkingDir: "{app}"
