# Rent Device Agent

Windows 后台客户端，安装在出租设备上。它会注册到租赁网站、定时发送设备信息和心跳，并读取当前租期状态。

## 配置

编辑 `appsettings.json`：

- `ApiBaseUrl`：租赁网站地址
- `SerialNumber`：网站中对应设备的序列号
- `SetupCode`：管理员在网站设备详情页生成的一次性注册码

注册成功后，设备 Token 会保存到 Windows 的 `ProgramData\RentDeviceAgent\state.json`，之后无需重复注册。

## 生成安装包

在 Windows PowerShell 中执行：

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
.
build-installer.ps1 `
  -ApiBaseUrl "https://你的租赁网站地址" `
  -SerialNumber "设备序列号" `
  -EnrollmentKey "网站的注册密钥"
```

执行完成后会生成 `output\RentDeviceAgent-Setup.exe`。用户只需要双击这个安装包，不需要安装 .NET SDK；程序是 self-contained 发布的。

安装程序会自动注册 Windows Service，并设置为开机自动启动。客户端只使用设备 Token 访问网站 API，不直接连接 D1。
