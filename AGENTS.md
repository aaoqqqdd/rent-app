# AGENTS.md

面向 AI 编码代理（Claude Code、Cursor、Copilot 等）的项目指引。人类贡献者请优先阅读 [README.md](README.md)。

## 项目概览

`rent-app` 是出租 Windows 设备上的**设备管理客户端**，与 [PC Rental 租赁网站](https://github.com/aaoqqqdd/rent)（本地同级目录 `../rent`，git remote `rent` / `rent-web`）联动。本仓库 git remote 为 `rent-app`（`github.com/aaoqqqdd/rent-app`）。

- 网站是事实来源：设备、客户、订单、租期、付款、合同、管理操作都在网站侧管理。
- 客户端只做：识别设备、上报状态与硬件信息、接收租赁状态、显示租期 / 到期提醒、执行授权的远程指令、断网时维持本地租期状态。
- **客户端只通过 HTTPS 调用网站的 `/api/device-agent/*` 接口，禁止直接访问 Cloudflare D1。** 需要新数据流时，先在 `../rent` 加接口，再在这里调用。
- 需求文档：[docs/windows-device-management-requirements.md](docs/windows-device-management-requirements.md)，其"非目标"一节（禁止键盘记录、录屏、隐蔽截图、剪贴板 / 摄像头 / 麦克风监控等）是硬约束。

## 技术栈

- **语言 / 运行时**：C# / .NET 8（`net8.0-windows`），`Nullable` + `ImplicitUsings` 开启
- **主程序**：`Microsoft.NET.Sdk.Worker` + WinForms，Windows Service（`Microsoft.Extensions.Hosting.WindowsServices`）
- **依赖**：`Microsoft.Extensions.Http`、`System.Management`（WMI 读硬件）、`System.Security.Cryptography.ProtectedData`（DPAPI 加密本地状态）
- **发布**：单文件、`win-x64` / `win-x86`；CI 用 `--self-contained false`，本地脚本用自包含
- **安装程序**：Inno Setup 6（`installer.iss`，简体中文）
- **无 .sln、无单元测试、无 LICENSE 文件**（网站仓库用 PolyForm Noncommercial）

## 常用命令

在 **Windows + .NET 8 SDK** 环境下（打包另需 Inno Setup 6）：

| 命令 | 用途 |
| --- | --- |
| `dotnet build windows-agent/RentDeviceAgent.csproj` | 编译主程序 |
| `dotnet build windows-agent/RentDeviceAgent.Updater.csproj` | 编译独立更新器 |
| `dotnet publish windows-agent/RentDeviceAgent.csproj -c Release -r win-x64 -p:PublishSingleFile=true` | 单文件发布 |
| `./windows-agent/publish.ps1` | 发布到 `windows-agent/dist/` |
| `./windows-agent/build-installer.ps1 -ApiBaseUrl <url> -SerialNumber <sn> -SetupCode <code>` | 写 `appsettings.json` + 发布 + 打包安装程序到 `windows-agent/output/` |
| `powershell windows-agent/install-service.ps1 -InstallPath <dir>` | 注册 `RentDeviceAgent` 服务 |

没有测试套件；改完代码至少确保 `dotnet build` 两个 csproj 都通过。macOS / Linux 上可用 `dotnet build -p:EnableWindowsTargeting=true` 做编译检查，但无法运行。

## 目录结构

```text
windows-agent/
  Program.cs                     入口。含 "--service" → RunAsync 无界面服务；否则 ApplicationConfiguration + AgentLeaseOverlayForm，
                                 服务未运行时在进程内临时 CreateAgentHost 托管同步逻辑。UI 单实例互斥 "Local\RentDeviceAgent.UI"
  AgentWorker.cs                 BackgroundService。注册 / 心跳 / 验机 / 拉取执行指令 / 自动更新 / state.json 的 DPAPI 加解密。最大的文件
  RentalDataCleaner.cs           租约结束 / 归还时擦除承租人浏览器 / 微信 / 回收站痕迹的静态助手（被 AgentWorker 调用）
  AgentOptions.cs                appsettings.json → "RentDeviceAgent" 段的强类型映射（含 DataCleanupOptions）
  AgentLeaseOverlayForm.cs       租期 / 到期提醒悬浮窗
  AgentDashboardForm.cs          本地状态面板（读 AgentWorker 写出的 dashboard 快照 JSON，端口 DashboardPort 默认 47821）
  AgentBindingForm.cs            首次运行输入 6 位注册码
  AgentSoftwareAgreementForm.cs  软件协议弹窗
  UpdaterProgram.cs              独立更新器入口，仅由 RentDeviceAgent.Updater.csproj 编译（主 csproj 显式 Exclude）
  RentDeviceAgent.csproj         EnableDefaultCompileItems=false；<Compile Include="*.cs" Exclude="UpdaterProgram.cs" />
  RentDeviceAgent.Updater.csproj 仅 <Compile Include="UpdaterProgram.cs" />
  appsettings.json               运行配置；Version 由发布流水线回写，不要手动改
  installer.iss / install-service.ps1 / build-installer.ps1 / publish.ps1   打包与安装脚本
  tools/make-installer-assets.ps1  用 GDI+ 生成 Inno Setup 的品牌向导图（Assets/，git-ignore，installer.iss 用 #ifexist 引用）
  logo.svg  README-install.txt  README-agent-foundation.md  README.md（子目录级说明）
  bin/ obj/ publish/ output/ release/  构建产物，已在 .gitignore
docs/windows-device-management-requirements.md  需求文档（中文）
.github/workflows/windows-release.yml           发布流水线
```

## 关键约定与注意事项

- **不碰数据库**：任何设备 / 客户 / 租赁数据都走网站 API。新增交互 = 先在 `../rent` 增加 `/api/device-agent/*` 路由，再在 `AgentWorker.cs` 用 `Url("/api/device-agent/...")` 调用。
- **网站契约**：现有接口见 README 表格。请求带设备 Token（`AuthenticatedClient()`）；`401` 视为网站已解绑，调用 `MarkUnbound()` 切到未绑定状态。
- **`--service` 与 UI 双形态**：改 `Program.cs` 的启动逻辑时，两条路径（服务 / 桌面）都要照顾到；服务名固定为 `PC Rental Device Agent`（`sc.exe` 查询用 `RentDeviceAgent`）。
- **本地状态**：`C:\ProgramData\RentDeviceAgent\` 下 `state.json`（DPAPI 加密：Token、可信服务器时间、设备 ID、序列号）、`unbound.flag`、`refresh-request`、`Updates\`、dashboard 快照。改结构要兼容旧文件或做迁移。
- **时间**：以网站心跳返回的 `_trustedServerTime` 为准，不信任本地系统时间，防止改时间绕过租期。
- **远程指令**：枚举 `AgentCommandType` = `SYNC` / `SHOW_MESSAGE` / `PAUSE_RENTAL` / `RESUME_RENTAL` / `REFRESH_DEVICE_INFO` / `CHECK_UPDATE` / `CREATE_RENTAL_USER` / `UPDATE_RENTAL_USER` / `DELETE_RENTAL_USER` / `CLEANUP_RENTAL_DATA`。新增指令：加枚举 + `switch` 分支 + 回报 `resultCode` / `message`，并与网站侧同步。
- **设备模式**：`normal` / `maintenance` / `return`，由心跳或指令设置，影响验机类型与悬浮窗展示。
- **租户账户指令**：`ApplyRentalUserAsync` / `DeleteRentalUserAsync` 通过 `net.exe` 操作本地账户——始终强制移出 `Administrators`、加入 `Users`，禁止操作 `Admin`，用户名经 `SafeWindowsUsername` 清洗（仅字母数字，≤20）。保持这些安全约束。
- **数据清理**：`RentalDataCleaner`（`RunAsync` / `RemoveUserProfile`）在租约结束、`DELETE_RENTAL_USER`、或 `CLEANUP_RENTAL_DATA` 指令时擦除承租人账户的浏览器 / 微信 / 回收站痕迹。开关在 `AgentOptions.DataCleanupOptions`（`appsettings.json` 的 `RentDeviceAgent:DataCleanup`）。安全边界与账户指令一致：只处理非管理员用户目录，`ProtectedProfiles` 里的 `admin` / `administrator` / 系统账户永不触碰。心跳自动触发依赖网站在 `/api/device-agent/heartbeat` 响应里返回 `cleanupRequested`（`AgentState.CleanupRequested`），改这条契约要同步 `../rent`。
- **自动更新**：按架构选 `RentDeviceAgent-x64.exe` / `-x86.exe`，从 `https://github.com/{GitHubRepository}/releases/tag/v{version}` 下载到 `Updates\`，再交给同目录的 `RentDeviceAgent.Updater.exe`（找不到就报错要求重装）。更新器随主程序一起发布。
- **版本号**：`appsettings.json` 的 `Version` 由 CI 回写，`.csproj` 不写死版本；本地调试不要提交版本变更。
- **隐私边界**：只采集设备管理必需的信息。不要添加需求文档"非目标"里列出的任何监控能力。
- **密钥 / 凭据**：不要提交 `appsettings.json` 里的真实地址以外的东西，尤其不要提交 `state.json`、设备 Token、注册码。

## 代码风格

- 跟随所在文件的既有风格：紧凑写法，单行 `switch` 分支带 `break`，`async` HTTP 调用，`WriteAgentLog(...)` 记录关键事件。
- HTTP 请求统一用 `Url(path)` 拼接 `ApiBaseUrl`；带鉴权用 `AuthenticatedClient()`。
- 新增 WinForms 界面：`sealed class XxxForm : Form`，构造函数里建控件，与现有 Form 一致。
- 面向用户的文案用简体中文。
- 新增 `*.cs` 会被主 csproj 自动纳入（`*.cs` glob）；若是更新器专用文件，记得在主 csproj 的 `Exclude` 里排除并加进 `RentDeviceAgent.Updater.csproj`。

## 提交与发布

- 提交信息遵循 Conventional Commits，可用中文；历史里 `feat(windows):` / `fix(windows):` / `chore(windows-agent):` 为常见前缀。
- 发布流水线 `.github/workflows/windows-release.yml`：push 到 `main` 或手动触发 →
  1. 按现有 `v*` tag 算版本（`^feat(...)?:` → minor，`BREAKING CHANGE` / `release-major` / `foo(x)!:` → major，其余 patch）；
  2. 打 `vX.Y.Z` tag，回写 `windows-agent/appsettings.json` 并提交（`chore(windows-agent): sync app version X.Y.Z [skip ci]`）；
  3. 发布 x64 / x86 单文件 exe + 独立更新器；
  4. Inno Setup 打包 `RentDeviceAgent-Setup.exe`；
  5. 建 GitHub Release，附安装程序与两个架构的裸 exe。
- 想跳过发布，提交信息带 `[skip ci]`。
- 改了网站接口契约的改动，务必在 `../rent` 仓库同步对应的 `/api/device-agent/*` 实现与迁移。
