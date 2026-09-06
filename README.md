# rent-app

出租设备的 **Windows 设备管理客户端**，与 [PC Rental 租赁网站](https://github.com/aaoqqqdd/rent)（本地 `../rent` 目录，下称"网站"）联动。

客户端安装在每一台出租的 Windows 设备上，负责识别设备、上报运行状态与硬件信息、接收当前租赁状态、显示租期与到期提醒，并执行经授权的远程管理指令。它本身**不是**独立的租赁后台——设备、客户、订单、租期、付款、合同仍以网站为唯一管理入口。

> 需求背景见 [docs/windows-device-management-requirements.md](docs/windows-device-management-requirements.md)。

## 与 rent 网站的关系

```text
        Cloudflare D1（统一数据库，归网站所有）
                     │
              网站统一后端 API
                 │        │
        ┌────────┘        └────────┐
   租赁网站 / 管理后台        rent-app Windows 客户端
   （github.com/aaoqqqdd/rent）   （本仓库）
```

- 客户端**只**通过 HTTPS 调用网站的 `/api/device-agent/*` 接口，**禁止**直接连接 Cloudflare D1。
- 网站是事实来源：设备绑定、租期、设备模式、远程指令都由网站下发，客户端只上报与执行。
- 网站地址默认指向 `appsettings.json` 中的 `ApiBaseUrl`（当前为 `https://rent.ydnw6zt6vj.workers.dev`）。
- 自动更新从 GitHub Releases（`aaoqqqdd/rent-app`）拉取，更新清单由网站的 `/api/device-agent/update` 提供。

调用的网站接口：

| 接口 | 用途 |
| --- | --- |
| `POST /api/device-agent/register` | 用 6 位一次性注册码绑定设备，换取设备 Token |
| `POST /api/device-agent/heartbeat` | 定时心跳，回传设备模式、锁定状态、合同链接等 |
| `POST /api/device-agent/inspection` | 上报验机快照（租前 / 归还后 / 自动健康巡检） |
| `GET /api/device-agent/commands` | 拉取待执行的远程指令 |
| `POST /api/device-agent/command-results` | 回报指令执行结果 |
| `GET /api/device-agent/state` | 读取最新租赁 / 设备状态 |
| `GET /api/device-agent/update` | 版本更新清单 |

## 仓库结构

```text
windows-agent/        .NET 8 客户端源码（见下）
docs/                 需求与设计文档
.github/workflows/    windows-release.yml：打 tag、构建、发布 GitHub Release
```

### windows-agent/

```text
Program.cs                     入口：--service 以 Windows Service 运行；否则弹出租期悬浮窗并本地托管
AgentWorker.cs                 核心：注册、心跳、验机、指令执行、自动更新、状态加解密（最大的文件）
AgentOptions.cs                appsettings.json 中 RentDeviceAgent 段的强类型配置
AgentLeaseOverlayForm.cs       租期 / 到期提醒悬浮窗
AgentDashboardForm.cs          本地状态面板（端口 DashboardPort，默认 47821）
AgentBindingForm.cs            首次运行输入 6 位注册码的绑定界面
AgentSoftwareAgreementForm.cs  软件使用协议弹窗
UpdaterProgram.cs              独立更新器（单独编译为 RentDeviceAgent.Updater.exe）
RentDeviceAgent.csproj         主程序：net8.0-windows，WinExe，单文件自包含，含 *.cs（不含 UpdaterProgram.cs）
RentDeviceAgent.Updater.csproj 更新器：只编译 UpdaterProgram.cs
appsettings.json               运行配置（Version 由发布流水线回写）
installer.iss                  Inno Setup 安装脚本
install-service.ps1            安装 / 注册 RentDeviceAgent Windows Service
build-installer.ps1            本地一键 publish + 打包安装程序
publish.ps1                    本地一键 publish 到 dist/
```

## 运行方式

- **Windows Service**：`RentDeviceAgent.exe --service`，服务名 `PC Rental Device Agent` / `RentDeviceAgent`，开机自启，无界面。
- **桌面 UI**：直接运行 `RentDeviceAgent.exe`，显示租期悬浮窗；若检测到服务未运行，会在进程内临时托管同步逻辑。
- 绑定状态与设备 Token 经 DPAPI 加密后存于 `C:\ProgramData\RentDeviceAgent\state.json`；另有 `unbound.flag`、`refresh-request`、`Updates\` 等运行时文件。

## 配置

`appsettings.json` 的 `RentDeviceAgent` 段：

| 键 | 说明 |
| --- | --- |
| `ApiBaseUrl` | 租赁网站地址 |
| `SerialNumber` | 可留空，注册码会自动识别设备 |
| `SetupCode` | 可留空，首次运行时提示输入 6 位一次性注册码 |
| `HeartbeatIntervalSeconds` | 心跳间隔（默认 10） |
| `Version` | 当前客户端版本（发布流水线回写，勿手改） |
| `UpdateManifestUrl` | 网站的更新清单地址 |
| `GitHubRepository` / `GitHubReleaseAsset` | 自动更新下载源（`aaoqqqdd/rent-app` / 按架构选 `RentDeviceAgent-x64.exe` 或 `-x86.exe`） |
| `UpdateCheckIntervalHours` | 检查更新间隔（默认 1） |
| `DashboardPort` | 本地状态面板端口（默认 47821） |
| `DataCleanup` | 租约结束 / 归还时自动清除承租人痕迹的开关（见下）。`Enabled` 总开关、`RunOnLeaseEnd` 到期自动清理、`WipeBrowsers` / `WipeWeChat` / `WipeRecycleBin` 分项、`RemoveUserProfileOnDelete` 删除租户时一并删用户目录、`ExtraPaths` 额外路径 |

管理员在网站的设备详情页生成 6 位一次性注册码；注册成功后注册码立即失效，网站只保存设备 Token 的哈希。

## 远程指令

网站通过 `GET /api/device-agent/commands` 下发，客户端执行后回报结果：

`SYNC`、`SHOW_MESSAGE`、`PAUSE_RENTAL`、`RESUME_RENTAL`、`REFRESH_DEVICE_INFO`、`CHECK_UPDATE`、`CREATE_RENTAL_USER`、`UPDATE_RENTAL_USER`、`DELETE_RENTAL_USER`、`CLEANUP_RENTAL_DATA`。

设备模式：`normal`（正常）、`maintenance`（维护 / 暂停）、`return`（归还）。租户账户指令会通过 `net.exe` 创建 / 更新 / 删除本地标准用户（强制移出 Administrators 组，禁止操作 `Admin`）。

### 租约结束后的数据清理

为保护上一位承租人的隐私、并让设备干净地交给下一位，客户端会在租约结束时清除承租人本地账户里的个人痕迹：

- **浏览器记录**：Chrome / Edge / Firefox / Chromium / Brave / 360 / QQ / 搜狗等的整个用户数据目录（历史、Cookie、缓存、保存的密码、自动填充），以及 `Recent`、`INetCache`、`WebCache` 等系统级历史。
- **微信记录**：`AppData\Roaming\Tencent\WeChat`、`xwechat`（微信 4.x），以及 `文档\WeChat Files` / `xwechat_files` 聊天文件。
- **回收站**，以及 `ExtraPaths` 里配置的任意额外路径。

触发方式：

1. **自动**：网站心跳返回 `cleanupRequested: true`（网站判定租约已结束）时，客户端对所有非管理员账户执行一次清理，并用 `cleanup-done.flag` 记录已处理的租约，避免重复擦除。
2. **手动**：网站下发 `CLEANUP_RENTAL_DATA` 指令，`payload` 可带 `username` 只清理指定账户，或带 `wipeBrowsers` / `wipeWeChat` / `wipeRecycleBin` 覆盖分项开关。
3. **删除租户账户时**：`DELETE_RENTAL_USER` 会先清理痕迹，再 `net user /delete`，最后按 `RemoveUserProfileOnDelete` 删除用户目录与 `ProfileList` 注册表项。

清理只针对承租人账户，**永不触碰** `Admin` / `Administrator` / 系统账户；可在 `appsettings.json` 的 `RentDeviceAgent:DataCleanup` 段整体关闭或按分项关闭。

## 本地构建

需要 Windows + [.NET 8 SDK](https://dotnet.microsoft.com/)，打包安装程序另需 [Inno Setup 6](https://jrsoftware.org/isinfo.php)。

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup

# 仅发布可执行文件
./windows-agent/publish.ps1

# 发布 + 打包安装程序（写入 appsettings.json 后构建）
./windows-agent/build-installer.ps1 `
  -ApiBaseUrl "https://你的租赁网站地址" `
  -SerialNumber "设备序列号" `
  -SetupCode "6位注册码"
```

产物：`windows-agent/output/RentDeviceAgent-Setup.exe`。

## 发布

`.github/workflows/windows-release.yml`：推送到 `main` 或手动触发时，

1. 根据现有 `v*` tag 计算下一个版本（`feat:` → minor，`BREAKING CHANGE` / `release-major` → major，其余 patch；手动触发可指定级别）；
2. 打 `vX.Y.Z` tag，并把版本回写 `windows-agent/appsettings.json`（提交信息带 `[skip ci]`）；
3. 用 .NET 8 发布 x64 / x86 单文件可执行文件与独立更新器；
4. 用 Inno Setup（含简体中文语言包）打包 `RentDeviceAgent-Setup.exe`；
5. 发布 GitHub Release，附带安装程序与两个架构的裸可执行文件。

普通用户从 GitHub Releases 下载 `RentDeviceAgent-Setup.exe` 双击安装即可，无需 .NET SDK。

## 安装与卸载

1. 双击安装包完成安装；向导会显示"设备绑定"页。
2. 粘贴管理员在网站生成的 6 位访问码，安装程序会安装到 Program Files 并注册为开机自启的 Windows Service。
3. 访问码只用一次，绑定成功后立即失效。
4. 卸载：Windows"应用和功能"中卸载 *PC Rental Device Agent*；卸载前确认设备仍可在网站后台管理。

## 安全说明

- 注册码只显示一次，注册成功后失效；网站只保存设备 Token 哈希。
- 不要把 `appsettings.json`、`state.json` 或任何设备 Token 提交到公开仓库。
- 客户端只采集设备管理所需的运行状态与基础硬件信息；**不做**键盘记录、录屏、隐蔽截图、剪贴板 / 摄像头 / 麦克风监控等（详见需求文档"非目标"）。
- 远程锁定、暂停、账户操作、数据清理等高风险指令应仅由授权管理员在网站侧发起。
- 租约结束后的数据清理是**数据最小化 / 隐私保护**行为（清除上一位承租人的浏览器与微信痕迹），不是监控；只擦除承租人账户，永不触碰 `Admin` / 系统账户，且可在配置里关闭。
- 时间以网站返回的可信服务器时间为准，避免本地改时间绕过租期控制。

## AI 编码代理

见 [AGENTS.md](AGENTS.md)。
