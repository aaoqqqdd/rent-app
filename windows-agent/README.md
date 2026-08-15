# rent-app Windows Device Agent

Windows 后台客户端，安装在出租设备上。它会注册到租赁网站、定时发送设备信息和心跳，并读取当前租期状态。

## 功能

- Windows Service 开机自动启动
- 使用一次性注册码绑定指定设备
- 自动上报主机名、系统版本、CPU、内存和剩余磁盘空间
- 定时发送心跳并读取设备模式、远程锁定状态和合同链接
- 网络中断时记录本地同步队列，恢复后自动补传
- 注册令牌保存在 `C:\ProgramData\RentDeviceAgent\state.json`

客户端只通过 HTTPS API 访问网站，不直接连接 D1 数据库。

## 配置

安装前由管理员在网站设备详情页生成 6 位一次性注册码。首次运行 `RentDeviceAgent.exe` 时直接输入注册码，客户端会自动从网站完成设备绑定并保存配置；无需手动输入序列号或长 Token。

首次运行前只需在 `appsettings.json` 设置网站地址：

- `ApiBaseUrl`：租赁网站地址
- `SerialNumber`：可留空，注册码会自动识别设备
- `SetupCode`：可留空，首次运行时由客户端提示输入 6 位注册码

注册成功后，设备 Token 会保存到 Windows 的 `ProgramData\RentDeviceAgent\state.json`，之后无需重复注册。

## 生成安装包

## 安装包

普通用户不需要安装 .NET SDK。请从 GitHub Releases 下载最新的
`RentDeviceAgent-Setup.exe`，双击运行即可。安装程序会自动注册并启动
`RentDeviceAgent` Windows Service。

开发者如需自行构建，在 Windows PowerShell 中执行：

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
.
build-installer.ps1 `
  -ApiBaseUrl "https://你的租赁网站地址" `
  -SerialNumber "设备序列号" `
  -EnrollmentKey "网站的注册密钥"
```

执行完成后会生成 `output\RentDeviceAgent-Setup.exe`。程序是 self-contained 发布的。

## 卸载

在 Windows“应用和功能”中卸载 PC Rental Device Agent。卸载前请确认设备仍可由管理员在网站后台管理。

## 安全说明

- 注册码只显示一次，并在注册成功后失效
- 网站只保存设备令牌哈希
- 不要把 `appsettings.json` 或 `state.json` 提交到公开仓库
- 远程锁定等高风险操作应只由授权管理员执行
