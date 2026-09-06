#define AppName "PC Rental 设备管理"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "PC Rental"
#define AppExeName "RentDeviceAgent.exe"
#define AppSupportURL "https://rent.ydnw6zt6vj.workers.dev"

[Setup]
AppId={{A3AA4A62-40EA-4A54-9B7B-7C6E2E4A1D91}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppSupportURL}
AppSupportURL={#AppSupportURL}
AppUpdatesURL={#AppSupportURL}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} 安装程序
DefaultDirName={autopf}\RentDeviceAgent
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=RentDeviceAgent-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
WizardSizePercent=120
WizardResizable=no
ShowLanguageDialog=no
SetupLogging=yes
; An update must replace RentDeviceAgent.exe even though the service and the per-user
; overlay (which cancels WM_CLOSE while the device is bound) keep it locked. Force-close
; anything still holding the files; install-service.ps1 recreates the service afterwards.
CloseApplications=force
RestartApplications=no
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\RentDeviceAgent.exe
#ifexist "Assets\app.ico"
SetupIconFile=Assets\app.ico
#endif
#ifexist "Assets\wizard-large.bmp"
WizardImageFile=Assets\wizard-large.bmp
WizardImageStretch=yes
#endif
#ifexist "Assets\wizard-small.bmp"
WizardSmallImageFile=Assets\wizard-small.bmp
#endif

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
chinesesimp.WelcomeLabel1=欢迎安装 [name]
chinesesimp.WelcomeLabel2=此向导会把 PC Rental 设备管理客户端安装到本机，并注册为开机自启的后台服务，用于连接租赁网站、显示租期、执行经授权的远程管理指令。%n%n建议先关闭其他正在运行的程序，然后点击“下一步”。
chinesesimp.FinishedHeadingLabel=PC Rental 设备管理已安装完成
chinesesimp.FinishedLabelNoIcons=客户端后台服务已启动，设备会自动连接租赁网站并显示租期信息。
chinesesimp.FinishedLabel=客户端后台服务已启动，设备会自动连接租赁网站并显示租期信息。
chinesesimp.BeveledLabel=PC Rental · 设备管理

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "README-install.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "logo.svg"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即打开 PC Rental 设备管理"; Flags: nowait postinstall skipifsilent

[Registry]
; HKLM Run applies to every Windows user who signs in, not only the administrator who installed it.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PC Rental Device Agent UI"; ValueData: """{app}\RentDeviceAgent.exe"" --ui"; Flags: uninsdeletevalue

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop RentDeviceAgent"; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete RentDeviceAgent"; Flags: runhidden waituntilterminated
Filename: "schtasks.exe"; Parameters: "/delete /tn ""PC Rental Device Agent UI"" /f"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\RentDeviceAgent"
Type: files; Name: "{commonstartup}\PC Rental 设备管理.lnk"

[Code]
var
  CodePage: TInputQueryWizardPage;
  CodeHint: TNewStaticText;
  UpdatePage: TOutputMsgWizardPage;
  ExistingInstallation: Boolean;

function IsSixDigits(const S: String): Boolean;
var
  I: Integer;
begin
  Result := Length(S) = 6;
  if Result then
    for I := 1 to 6 do
      if (S[I] < '0') or (S[I] > '9') then
      begin
        Result := False;
        Break;
      end;
end;

function CodeIsAcceptable: Boolean;
var
  S: String;
begin
  S := Trim(CodePage.Values[0]);
  Result := (S = '') or IsSixDigits(S);
end;

procedure RefreshCodePage;
var
  S: String;
begin
  S := Trim(CodePage.Values[0]);
  if S = '' then
  begin
    CodeHint.Font.Color := clGray;
    CodeHint.Caption := '留空：安装完成后用 BIOS 序列号自动绑定设备。';
  end
  else if IsSixDigits(S) then
  begin
    CodeHint.Font.Color := clGreen;
    CodeHint.Caption := '✓ 访问码格式正确，可以继续。';
  end
  else
  begin
    CodeHint.Font.Color := clRed;
    CodeHint.Caption := '✗ 访问码必须是恰好 6 位数字。';
  end;
  if (CodePage <> nil) and (WizardForm.CurPageID = CodePage.ID) then
    WizardForm.NextButton.Enabled := CodeIsAcceptable;
end;

procedure CodeEditChanged(Sender: TObject);
begin
  RefreshCodePage;
end;

procedure InitializeWizard;
begin
  ExistingInstallation := FileExists(ExpandConstant('{autopf}\RentDeviceAgent\RentDeviceAgent.exe')) or
    DirExists(ExpandConstant('{autopf}\RentDeviceAgent'));

  WizardForm.Font.Name := 'Microsoft YaHei UI';
  WizardForm.WelcomeLabel1.Font.Name := 'Microsoft YaHei UI';
  WizardForm.WelcomeLabel1.Font.Size := 15;
  WizardForm.PageNameLabel.Font.Style := [fsBold];

  CodePage := CreateInputQueryPage(wpSelectDir,
    '设备绑定', '把这台设备接入 PC Rental 租赁网站',
    '客户端会先读取 BIOS 序列号自动绑定；如果网站里没有相同序列号的设备，请填写管理员在网站“设备详情”页生成的 6 位一次性访问码。');
  CodePage.Add('6 位访问码（自动绑定时可留空）:', False);

  CodeHint := TNewStaticText.Create(CodePage);
  CodeHint.Parent := CodePage.Surface;
  CodeHint.Left := CodePage.Edits[0].Left;
  CodeHint.Top := CodePage.Edits[0].Top + CodePage.Edits[0].Height + ScaleY(8);
  CodeHint.Width := CodePage.SurfaceWidth - CodePage.Edits[0].Left;
  CodeHint.AutoSize := False;

  CodePage.Edits[0].OnChange := @CodeEditChanged;

  UpdatePage := CreateOutputMsgPage(wpSelectDir,
    '更新 Windows 客户端', '检测到已安装的客户端',
    '本次将更新程序文件并保留现有设备绑定，无需再次输入访问码。点击“下一步”开始更新。');

  RefreshCodePage;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (ExistingInstallation and (PageID = CodePage.ID)) or
    ((not ExistingInstallation) and (PageID = UpdatePage.ID));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = CodePage.ID then
    RefreshCodePage
  else
    WizardForm.NextButton.Enabled := True;

  if CurPageID = wpReady then
  begin
    if ExistingInstallation then
      WizardForm.NextButton.Caption := '更新'
    else
      WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
  end
  else if CurPageID <> wpFinished then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (not ExistingInstallation) and (CurPageID = CodePage.ID) and (not CodeIsAcceptable) then
  begin
    MsgBox('访问码必须为空，或填写恰好 6 位数字。', mbError, MB_OK);
    Result := False;
  end;
end;

procedure RunHidden(const FileName, Params: String);
var
  ResultCode: Integer;
begin
  Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;
  if not ExistingInstallation then
    Exit;

  // Stop the service cleanly first.
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop RentDeviceAgent');
  Sleep(1500);

  // Disable auto-start and failure-recovery so a kill cannot respawn the service
  // while Setup copies files. install-service.ps1 deletes and recreates the service
  // with StartupType=Automatic and the restart actions afterwards.
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'config RentDeviceAgent start= disabled');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'failure RentDeviceAgent reset= 0 actions= ""');

  // The overlay UI cancels WM_CLOSE while the device is bound, so a graceful close is
  // ignored. Force-terminate every session's copy of the agent and the standalone updater.
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM RentDeviceAgent.exe');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM RentDeviceAgent.Updater.exe');
  Sleep(1500);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
begin
  if CurStep = ssPostInstall then
  begin
    Params := '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(ExpandConstant('{app}\install-service.ps1')) +
      ' -InstallPath ' + AddQuotes(ExpandConstant('{app}'));
    if (not ExistingInstallation) and (Length(Trim(CodePage.Values[0])) <> 0) then
      Params := Params + ' -SetupCode ' + AddQuotes(Trim(CodePage.Values[0]));
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('客户端服务安装失败，错误代码: ' + IntToStr(ResultCode) + #13#10 +
        '详细日志：' + ExpandConstant('{commonappdata}\RentDeviceAgent\install-service.log'), mbError, MB_OK);
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
