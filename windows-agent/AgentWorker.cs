using System.Management;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

public sealed class AgentWorker : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _logger;
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RentDeviceAgent", "state.json");
    private readonly string _unboundPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RentDeviceAgent", "unbound.flag");
    private readonly string _refreshRequestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RentDeviceAgent", "refresh-request");
    private readonly string _cleanupFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RentDeviceAgent", "cleanup-done.flag");
    private readonly string _updatingFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RentDeviceAgent", "updating.flag");
    private string? _token;
    private string _deviceMode = "normal";
    private bool _beforeSnapshotSent;
    private string _statusText = "正在连接";
    private JsonElement? _rental;
    private DateTime _lastUpdateCheck = DateTime.MinValue;
    private bool _forceUpdateCheck;
    private string? _latestVersion;
    private string? _updateDownloadUrl;
    private int _consecutiveFailures;
    private bool _bindingRevoked;
    private string? _deviceId;
    private string? _registeredSerialNumber;
    private string? _detectedSerialNumber;
    private string? _lastInspectionType;
    private string? _messageTitle;
    private string? _messageBody;
    private static Mutex? _workerSingleton;

    public AgentWorker(IHttpClientFactory httpClientFactory, IOptions<AgentOptions> options, ILogger<AgentWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

        // Exactly one worker may register, heartbeat, check for updates and own
        // state.json on this machine. The Windows service is the primary; a
        // UI-hosted fallback (or a second logon session) must stand down rather
        // than run a duplicate that double-registers and fights over the files.
        if (!TryBecomeSingletonWorker())
        {
            _logger.LogWarning("Another Rent Device Agent worker already holds the singleton lock; this instance stays idle.");
            WriteAgentLog("检测到已有客户端后台在运行，本实例保持空闲，避免重复注册与双进程。");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
            return;
        }

        LoadState();
        _bindingRevoked = File.Exists(_unboundPath);
        _logger.LogInformation("Rent Device Agent started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleFailed = false;

            try
            {
                if (!_bindingRevoked && string.IsNullOrWhiteSpace(_token)) await RegisterAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                cycleFailed = true;
                _logger.LogWarning(ex, "Rent device agent registration failed; will retry");
                WriteAgentLog($"注册失败：{ex.GetType().Name}: {ex.Message}");
            }

            if (!_bindingRevoked && !string.IsNullOrWhiteSpace(_token))
            {
                // Heartbeat + state share a try: a transient failure here should
                // back the whole loop off, but must NOT stop us from pulling
                // remote commands in the same cycle.
                try
                {
                    await SendHeartbeatAsync(stoppingToken);
                    await ReadStateAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    cycleFailed = true;
                    _logger.LogWarning(ex, "Rent device agent heartbeat/state sync failed; will retry");
                    WriteAgentLog($"心跳/状态同步失败：{ex.GetType().Name}: {ex.Message}");
                }

                // Command delivery runs on its own try and does NOT feed the
                // exponential backoff: an admin who just submitted
                // CREATE_RENTAL_USER expects it applied within seconds, so keep
                // polling at the base interval even if this endpoint is unhappy.
                try
                {
                    await ProcessCommandsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Rent device agent command sync failed; will retry next cycle");
                    WriteAgentLog($"拉取远程指令失败：{ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    await CheckForUpdateAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Rent device agent update check failed");
                    WriteAgentLog($"检查更新失败：{ex.GetType().Name}: {ex.Message}");
                }
            }

            _consecutiveFailures = cycleFailed ? Math.Min(_consecutiveFailures + 1, 6) : 0;

            // Keep the website roster responsive while still avoiding a tight loop.
            // Remote commands should be picked up promptly after an admin
            // submits them, even when the normal heartbeat is configured
            // longer for production traffic.
            var baseSeconds = Math.Clamp(_options.HeartbeatIntervalSeconds, 5, 10);
            var seconds = _consecutiveFailures == 0
                ? baseSeconds
                : Math.Min(300, Math.Max(5, 5 * (1 << Math.Min(_consecutiveFailures - 1, 5))));
            try { await WaitForNextCycleAsync(seconds, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private static bool TryBecomeSingletonWorker()
    {
        try
        {
            // Kept in a static field for the process lifetime; the OS releases it on exit.
            _workerSingleton = new Mutex(false, @"Global\RentDeviceAgent.Worker");
            try { return _workerSingleton.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { return true; } // previous holder crashed; it's ours now
        }
        catch
        {
            // Never block the only worker over a mutex/ACL failure.
            _workerSingleton = null;
            return true;
        }
    }

    private async Task WaitForNextCycleAsync(int seconds, CancellationToken cancellationToken)
    {
        for (var elapsed = 0; elapsed < seconds; elapsed += 1)
        {
            if (File.Exists(_refreshRequestPath))
            {
                File.Delete(_refreshRequestPath);
                _forceUpdateCheck = true;
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl))
            throw new InvalidOperationException("ApiBaseUrl is required");
        _detectedSerialNumber = ReadDeviceSerialNumber();

        var client = _httpClientFactory.CreateClient("rent");
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/device-agent/register"));
        request.Content = JsonContent.Create(new { serialNumber = _detectedSerialNumber, setupCode = _options.SetupCode });
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            WriteAgentLog($"绑定失败 HTTP {(int)response.StatusCode}：{error}");
            throw new InvalidOperationException($"服务器拒绝绑定（HTTP {(int)response.StatusCode}）：{error}");
        }
        var result = await response.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(result?.Token)) throw new InvalidOperationException("Registration response did not contain a token");
        _token = result.Token;
        _deviceId = result.DeviceId;
        _registeredSerialNumber = result.SerialNumber;
        if (string.IsNullOrWhiteSpace(_token)) throw new InvalidOperationException("Registration token is empty");
        SaveState();
        _options.SerialNumber = result.SerialNumber;
        _options.SetupCode = "";
        _bindingRevoked = false;
        File.Delete(_unboundPath);
        WriteAgentLog($"绑定成功：设备 ID={_deviceId}，网站序列号={_registeredSerialNumber}，本机序列号={_detectedSerialNumber}");
        WriteDashboardSnapshot("已连接", null, null, null, null, false, ReadMemoryMb() / 1024d, GetStorageGb());
        _logger.LogInformation("Device registered as {DeviceId}", result.DeviceId);
    }

    private string ReadDeviceSerialNumber()
    {
        foreach (var query in new[] { "SELECT SerialNumber FROM Win32_BIOS", "SELECT SerialNumber FROM Win32_BaseBoard" })
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(query);
                var value = searcher.Get().Cast<ManagementObject>().Select(item => item["SerialNumber"]?.ToString()?.Trim()).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item) && !item.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }
        }
        return _options.SerialNumber;
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var client = AuthenticatedClient();
        var inspectionType = _deviceMode == "return" ? "after_return" : (_beforeSnapshotSent ? "automated_health" : "before_rental");
        var snapshot = BuildInspectionSnapshot();
        var payload = new
        {
            hostname = Environment.MachineName,
            osVersion = Environment.OSVersion.VersionString,
            cpu = ReadWmiValue("Win32_Processor", "Name"),
            memoryMb = ReadMemoryMb(),
            storageFreeBytes = GetStorageFreeBytes(),
            version = _options.Version,
            serialNumber = _detectedSerialNumber,
            inspectionType,
            snapshot
        };
        using var response = await client.PostAsJsonAsync(Url("/api/device-agent/heartbeat"), payload, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { MarkUnbound(); WriteAgentLog("网站已解绑本机，已立即切换为未绑定状态"); return; }
        response.EnsureSuccessStatusCode();
        var heartbeatBody = await response.Content.ReadAsStringAsync(cancellationToken);
        AgentState? heartbeatResult = null;
        try { heartbeatResult = JsonSerializer.Deserialize<AgentState>(heartbeatBody, new JsonSerializerOptions(JsonSerializerDefaults.Web)); } catch { }
        if (heartbeatResult is null) throw new InvalidOperationException($"网站心跳响应无法解析：{Truncate(heartbeatBody, 300)}");
        if (!heartbeatResult.Ok) throw new InvalidOperationException($"网站未确认心跳：{Truncate(heartbeatBody, 300)}");
        WriteAgentLog($"心跳成功：HTTP {(int)response.StatusCode}，设备 ID={_deviceId}");
        if (!string.IsNullOrWhiteSpace(heartbeatResult.DeviceMode)) _deviceMode = heartbeatResult.DeviceMode;
        await MaybeRunLeaseEndCleanupAsync(heartbeatResult.CleanupRequested);
        if (_lastInspectionType != inspectionType && inspectionType != "automated_health")
        {
            await SendInspectionAsync(inspectionType, snapshot, cancellationToken);
            _lastInspectionType = inspectionType;
        }
        _beforeSnapshotSent = true;
    }

    private async Task SendInspectionAsync(string inspectionType, object snapshot, CancellationToken cancellationToken)
    {
        using var response = await AuthenticatedClient().PostAsJsonAsync(Url("/api/device-agent/inspection"), new { inspectionType, snapshot }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
    {
        using var response = await AuthenticatedClient().GetAsync(Url("/api/device-agent/commands"), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { MarkUnbound(); WriteAgentLog("网站已解绑本机（拉取指令返回 401）"); return; }
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        DeviceCommand[] commands;
        try { commands = ParseCommands(raw); }
        catch (Exception ex)
        {
            WriteAgentLog($"远程指令响应无法解析：{ex.Message}；原始内容：{Truncate(raw, 600)}");
            return;
        }
        if (commands.Length == 0) return;
        WriteAgentLog($"收到 {commands.Length} 条待执行指令：{string.Join(", ", commands.Select(c => $"{c.CommandType}#{c.Id}"))}");
        foreach (var command in commands)
        {
            var success = false;
            var resultCode = "FAILED";
            var message = "命令执行失败";
            try
            {
                // The device token already scopes /commands to this machine, so a
                // blank or mismatched-format deviceId from the website must not
                // silently drop the command. Only reject when both sides carry a
                // concrete id and they genuinely differ.
                if (!string.IsNullOrWhiteSpace(command.DeviceId) && !string.IsNullOrWhiteSpace(_deviceId)
                    && !command.DeviceId.Equals(_deviceId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"命令目标设备 {command.DeviceId} 与本机 {_deviceId} 不一致");
                // Missing / unparseable expiry means "no expiry", not "expired" -
                // otherwise a website that stops sending expiresAt silently drops
                // every command.
                if (!string.IsNullOrWhiteSpace(command.ExpiresAt)
                    && DateTimeOffset.TryParse(command.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiresAt)
                    && expiresAt <= DateTimeOffset.UtcNow) { resultCode = "EXPIRED"; message = "命令已过期"; }
                else if (!Enum.TryParse<AgentCommandType>(command.CommandType, true, out var type)) { resultCode = "UNSUPPORTED"; message = "不支持的命令类型"; }
                else
                {
                    JsonElement commandPayload;
                    try { commandPayload = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(command.Payload) ? "{}" : command.Payload); }
                    catch { commandPayload = JsonSerializer.Deserialize<JsonElement>("{}"); }
                    switch (type)
                    {
                        case AgentCommandType.SYNC:
                            await WriteRefreshRequestAsync(); resultCode = "SYNC_REQUESTED"; message = "已请求立即同步"; success = true; break;
                        case AgentCommandType.REFRESH_DEVICE_INFO:
                            await SendInspectionAsync("automated_health", BuildInspectionSnapshot(), cancellationToken); resultCode = "REFRESHED"; message = "设备信息已刷新"; success = true; break;
                        case AgentCommandType.CHECK_UPDATE:
                            _lastUpdateCheck = DateTime.MinValue; resultCode = "UPDATE_CHECK_REQUESTED"; message = "已请求检查更新"; success = true; break;
                        case AgentCommandType.PAUSE_RENTAL:
                            _deviceMode = "maintenance"; WriteDashboardSnapshotFromCurrentState(); resultCode = "PAUSED"; message = "设备已进入维护状态"; success = true; break;
                        case AgentCommandType.RESUME_RENTAL:
                            _deviceMode = "normal"; WriteDashboardSnapshotFromCurrentState(); resultCode = "RESUMED"; message = "设备已恢复正常状态"; success = true; break;
                        case AgentCommandType.SHOW_MESSAGE:
                            var title = commandPayload.TryGetProperty("title", out var titleValue) ? titleValue.ToString() : "租赁通知";
                            var body = commandPayload.TryGetProperty("message", out var bodyValue) ? bodyValue.ToString() : "您收到一条租赁通知。";
                            _messageTitle = title[..Math.Min(title.Length, 120)]; _messageBody = body[..Math.Min(body.Length, 500)]; WriteDashboardSnapshotFromCurrentState(); WriteAgentLog($"收到通知：{title} - {body}".Replace("\r", " ").Replace("\n", " ")); resultCode = "MESSAGE_RECEIVED"; message = "通知已显示"; success = true; break;
                        case AgentCommandType.CREATE_RENTAL_USER:
                        case AgentCommandType.UPDATE_RENTAL_USER:
                            await ApplyRentalUserAsync(commandPayload, type == AgentCommandType.CREATE_RENTAL_USER);
                            resultCode = type == AgentCommandType.CREATE_RENTAL_USER ? "RENTAL_USER_CREATED" : "RENTAL_USER_UPDATED";
                            message = "Windows 租户账户已更新"; success = true; break;
                        case AgentCommandType.DELETE_RENTAL_USER:
                            await DeleteRentalUserAsync(commandPayload);
                            resultCode = "RENTAL_USER_DELETED"; message = "Windows 租户账户已删除"; success = true; break;
                        case AgentCommandType.CLEANUP_RENTAL_DATA:
                            if (!_options.DataCleanup.Enabled) { resultCode = "CLEANUP_DISABLED"; message = "设备端已禁用数据清理"; break; }
                            var cleanupUser = commandPayload.TryGetProperty("username", out var cleanupName) ? cleanupName.ToString() : null;
                            var cleanupReport = await RentalDataCleaner.RunAsync(
                                string.IsNullOrWhiteSpace(cleanupUser) ? null : cleanupUser,
                                BuildCleanupSpec(commandPayload, removeProfile: false), WriteAgentLog);
                            resultCode = cleanupReport.Failed == 0 ? "RENTAL_DATA_CLEANED" : "RENTAL_DATA_CLEANED_WITH_ERRORS";
                            message = cleanupReport.Summary; success = cleanupReport.Failed == 0; break;
                    }
                }
            }
            catch (Exception ex) { message = ex.Message; WriteAgentLog($"命令 {command.Id} 执行失败：{ex.Message}"); }
            await ReportCommandResultAsync(command.Id, success, resultCode, message, cancellationToken);
        }
    }

    // Tolerate the several shapes the website's /commands response has shipped in:
    // a bare array, or { commands: [...] } / { data: { commands: [...] } } /
    // { items: [...] }, with either camelCase or snake_case keys, and a payload
    // that is either a JSON string or an inline JSON object.
    private static DeviceCommand[] ParseCommands(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<DeviceCommand>();
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array) array = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("commands", out var c) && c.ValueKind == JsonValueKind.Array) array = c;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("commands", out var dc) && dc.ValueKind == JsonValueKind.Array) array = dc;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var da) && da.ValueKind == JsonValueKind.Array) array = da;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array) array = it;
        else return Array.Empty<DeviceCommand>();

        var list = new List<DeviceCommand>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            string? Field(params string[] names)
            {
                foreach (var name in names)
                    if (element.TryGetProperty(name, out var value))
                        return value.ValueKind switch
                        {
                            JsonValueKind.String => value.GetString(),
                            JsonValueKind.Null or JsonValueKind.Undefined => null,
                            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                            _ => value.ToString(),
                        };
                return null;
            }
            list.Add(new DeviceCommand(
                Id: Field("id", "commandId", "command_id") ?? "",
                DeviceId: Field("deviceId", "device_id") ?? "",
                CommandType: Field("commandType", "command_type", "type") ?? "",
                Payload: Field("payload", "parameters", "params") ?? "{}",
                Status: Field("status") ?? "",
                CreatedAt: Field("createdAt", "created_at") ?? "",
                ExpiresAt: Field("expiresAt", "expires_at") ?? ""));
        }
        return list.ToArray();
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";

    private static async Task ApplyRentalUserAsync(JsonElement payload, bool create)
    {
        var username = SafeWindowsUsername(payload.TryGetProperty("username", out var name) ? name.ToString() : "");
        var password = payload.TryGetProperty("password", out var pass) ? pass.ToString() : "";
        if (string.IsNullOrWhiteSpace(username) || username.Equals("Admin", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("租户账户资料无效");
        var accountExisted = await UserExistsAsync(username);
        if (create || !accountExisted)
            await RunNetAsync("user", username, password, "/add", "/y");
        else
            await RunNetAsync("user", username, password);
        // Explicitly remove elevated membership even when Windows reused an existing
        // local account or an old provisioning attempt added it to Administrators.
        await RunNetBestEffortAsync("localgroup", "Administrators", username, "/delete");
        // net.exe already puts a freshly created account in the local Users group,
        // so a second explicit /add returns error 1378 ("already a member"). Keep
        // it as a best-effort safety net instead of letting it fail the whole
        // CREATE_RENTAL_USER command.
        await RunNetBestEffortAsync("localgroup", "Users", username, "/add");
        // Only seed the tenant desktop when the account is first created. A plain
        // password change (UPDATE_RENTAL_USER on an existing account) must never
        // re-copy the template desktop, or the tenant's own desktop changes get
        // wiped every time the website rotates the password.
        if (!accountExisted)
            await InstallRentalShortcutsAsync(username);
    }

    private async Task DeleteRentalUserAsync(JsonElement payload)
    {
        var username = SafeWindowsUsername(payload.TryGetProperty("username", out var name) ? name.ToString() : "");
        if (string.IsNullOrWhiteSpace(username) || username.Equals("Admin", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("禁止删除管理员账户");
        // Wipe the tenant's browser / WeChat / recycle-bin traces before the account and profile disappear.
        if (_options.DataCleanup.Enabled)
        {
            try
            {
                var report = await RentalDataCleaner.RunAsync(username, BuildCleanupSpec(payload, removeProfile: false), WriteAgentLog);
                WriteAgentLog($"删除租户前清理：{report.Summary}");
            }
            catch (Exception ex) { WriteAgentLog($"删除租户前清理失败：{ex.Message}"); }
        }
        await RunNetAsync("user", username, "/delete");
        await RemoveSeedDesktopTaskAsync(username);
        if (_options.DataCleanup.Enabled && _options.DataCleanup.RemoveUserProfileOnDelete)
            RentalDataCleaner.RemoveUserProfile(username, WriteAgentLog);
    }

    private CleanupSpec BuildCleanupSpec(JsonElement? payload, bool removeProfile)
    {
        var c = _options.DataCleanup;
        bool Flag(string key, bool fallback) =>
            payload is { } p && p.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : fallback;
        return new CleanupSpec(
            Browsers: Flag("wipeBrowsers", c.WipeBrowsers),
            WeChat: Flag("wipeWeChat", c.WipeWeChat),
            RecycleBin: Flag("wipeRecycleBin", c.WipeRecycleBin),
            RemoveProfile: removeProfile,
            ExtraPaths: c.ExtraPaths ?? Array.Empty<string>());
    }

    private async Task MaybeRunLeaseEndCleanupAsync(bool cleanupRequested)
    {
        if (!cleanupRequested || !_options.DataCleanup.Enabled || !_options.DataCleanup.RunOnLeaseEnd) return;
        var rentalId = _rental?.TryGetProperty("id", out var id) == true ? id.ToString() : "current";
        var marker = $"lease-end:{rentalId}";
        try { if (File.Exists(_cleanupFlagPath) && File.ReadAllText(_cleanupFlagPath).Trim() == marker) return; }
        catch { }
        WriteAgentLog($"网站判定租约已结束（rental={rentalId}），开始自动清理承租人数据");
        var report = await RentalDataCleaner.RunAsync(null, BuildCleanupSpec(null, removeProfile: false), WriteAgentLog);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cleanupFlagPath)!);
            await File.WriteAllTextAsync(_cleanupFlagPath, marker);
        }
        catch (Exception ex) { WriteAgentLog($"写入清理标记失败：{ex.Message}"); }
        WriteAgentLog($"租约到期自动清理完成：{report.Summary}");
    }

    private static string SafeWindowsUsername(string value)
    {
        var clean = new string(value.Trim().Where(char.IsLetterOrDigit).ToArray());
        return clean.Length switch { 0 => "RentalUser", _ => clean[..Math.Min(clean.Length, 20)] };
    }

    private static async Task RunNetAsync(params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("net.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new InvalidOperationException("无法启动 Windows 账户命令");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException((await process.StandardError.ReadToEndAsync()).Trim() is { Length: > 0 } error ? error : "Windows 账户命令执行失败");
    }

    private static async Task RunNetBestEffortAsync(params string[] args)
    {
        try { await RunNetAsync(args); } catch { }
    }

    private static async Task<bool> UserExistsAsync(string username)
    {
        try
        {
            await RunNetAsync("user", username);
            return true;
        }
        catch { return false; }
    }

    // Task Scheduler name that seeds a given tenant's desktop on their first logon.
    private static string SeedDesktopTaskName(string username) => "RentDeviceAgent-SeedDesktop-" + username;

    private static async Task InstallRentalShortcutsAsync(string username)
    {
        // The tenant's user profile does not exist until their first interactive
        // logon. Creating C:\Users\<user>\Desktop from the service beforehand makes
        // the Windows profile service reject the folder and sign the tenant into a
        // temporary profile ("无法正常使用"). So instead of copying anything now,
        // register a one-shot logon task that runs inside the tenant session -
        // after the real profile is built - copies the template desktop, then
        // deletes itself.
        var seedScript = @"
$ErrorActionPreference = 'SilentlyContinue'
# Seed the desktop exactly once. The marker guards against the task lingering
# (a standard user may lack rights to delete a SYSTEM-registered task) so a
# second logon never re-copies the template over the tenant's own changes.
$marker = Join-Path $env:USERPROFILE '.rent-desktop-seeded'
if (Test-Path $marker) { Unregister-ScheduledTask -TaskName $env:SEED_TASK_NAME -Confirm:$false; return }
$desktop = Join-Path $env:USERPROFILE 'Desktop'
New-Item -ItemType Directory -Path $desktop -Force | Out-Null
$templateDesktops = @(
  (Join-Path $env:SystemDrive 'Users\Admin\Desktop'),
  (Join-Path $env:SystemDrive 'Users\Administrator\Desktop')
)
# Use the first available administrator desktop as the tenant desktop template.
foreach ($template in $templateDesktops) {
  if (Test-Path $template) {
    Get-ChildItem -Path $template -Force | ForEach-Object {
      Copy-Item $_.FullName (Join-Path $desktop $_.Name) -Recurse -Force
    }
    break
  }
}
$roots = @(
  (Join-Path $env:PUBLIC 'Desktop'),
  (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs')
)
$apps = @(
  @{ Name = 'ToDesk'; Patterns = @('*ToDesk*.lnk', '*ToDesk*.url') },
  @{ Name = '微信'; Patterns = @('*微信*.lnk', '*WeChat*.lnk', '*Weixin*.lnk') },
  @{ Name = 'Safe Exam Browser'; Patterns = @('*Safe*Exam*Browser*.lnk', '*SEB*.lnk') },
  @{ Name = 'Google Chrome'; Patterns = @('*Google Chrome*.lnk', '*Chrome*.lnk') }
)
foreach ($app in $apps) {
  $source = $null
  foreach ($root in $roots) {
    foreach ($pattern in $app.Patterns) {
      $source = Get-ChildItem -Path $root -Filter $pattern -File -Recurse | Select-Object -First 1
      if ($source) { break }
    }
    if ($source) { break }
  }
  if ($source) { Copy-Item $source.FullName (Join-Path $desktop $source.Name) -Force }
}
Set-Content -Path $marker -Value (Get-Date -Format o) -Force
attrib +h $marker
Unregister-ScheduledTask -TaskName $env:SEED_TASK_NAME -Confirm:$false
";
        var seedEncoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(seedScript));

        // Runs as the local service; registers a task that itself runs as the
        // tenant (non-elevated) only while they are interactively logged on, so no
        // password needs to be stored.
        var registerScript = @"
$ErrorActionPreference = 'Stop'
$user = $env:RENTAL_USER
$name = $env:SEED_TASK_NAME
$arg  = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $env:SEED_ENCODED
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
";
        var registerEncoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(registerScript));
        using var process = new Process { StartInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(registerEncoded);
        process.StartInfo.Environment["RENTAL_USER"] = username;
        process.StartInfo.Environment["SEED_TASK_NAME"] = SeedDesktopTaskName(username);
        process.StartInfo.Environment["SEED_ENCODED"] = seedEncoded;
        if (!process.Start()) return;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            WriteAgentLog($"登记租户桌面初始化任务失败：{(await process.StandardError.ReadToEndAsync()).Trim()}");
        else
            WriteAgentLog($"已登记租户 {username} 首次登录时初始化桌面（模板：Admin 桌面）");
    }

    private static async Task RemoveSeedDesktopTaskAsync(string username)
    {
        // Best-effort: drop a still-pending seed task if the tenant never logged in
        // before the account was deleted.
        using var process = new Process { StartInfo = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("/Delete");
        process.StartInfo.ArgumentList.Add("/TN");
        process.StartInfo.ArgumentList.Add(SeedDesktopTaskName(username));
        process.StartInfo.ArgumentList.Add("/F");
        try { if (process.Start()) await process.WaitForExitAsync(); } catch { }
    }

    private async Task WriteRefreshRequestAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_refreshRequestPath)!);
        await File.WriteAllTextAsync(_refreshRequestPath, DateTime.UtcNow.ToString("O"));
    }

    private async Task ReportCommandResultAsync(string commandId, bool success, string resultCode, string message, CancellationToken cancellationToken)
    {
        await AuthenticatedClient().PostAsJsonAsync(Url("/api/device-agent/command-results"), new { commandId, success, resultCode, message, executedAt = DateTime.UtcNow.ToString("O") }, cancellationToken);
        WriteAgentLog($"命令 {commandId}：{resultCode} - {message}");
    }

    private void WriteDashboardSnapshotFromCurrentState()
    {
        var startDate = _rental?.TryGetProperty("start_date", out var start) == true ? start.ToString() : null;
        var endDate = _rental?.TryGetProperty("end_date", out var end) == true ? end.ToString() : null;
        var rentalId = _rental?.TryGetProperty("id", out var id) == true ? id.ToString() : null;
        var rentalStatus = _rental?.TryGetProperty("status", out var status) == true ? status.ToString() : null;
        WriteDashboardSnapshot(_statusText, rentalStatus, startDate, endDate, rentalId, false, null, null);
    }

    private object BuildInspectionSnapshot()
    {
        var battery = ReadBatteryInfo();
        return new
        {
        hostname = Environment.MachineName,
        osVersion = Environment.OSVersion.VersionString,
        cpu = ReadWmiValue("Win32_Processor", "Name"),
        memoryMb = ReadMemoryMb(),
        storageFreeBytes = GetStorageFreeBytes(),
        version = _options.Version,
        screen = HasWmiDevice("SELECT Name FROM Win32_DesktopMonitor") ? "已识别" : "未识别",
        keyboard = HasWmiDevice("SELECT Name FROM Win32_Keyboard") ? "已识别" : "未识别",
        touchpad = HasWmiDevice("SELECT Name FROM Win32_PointingDevice WHERE Name LIKE '%Touchpad%'") ? "已识别" : "未识别",
        body = "需人工目检",
        camera = HasWmiDevice("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%Camera%' OR Name LIKE '%Webcam%'") ? "已识别" : "未识别",
        wifi = HasWmiDevice("SELECT Name FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE AND (Name LIKE '%Wi-Fi%' OR Name LIKE '%Wireless%')") ? "已连接" : "未连接",
        power = "通过（客户端正在运行）",
        batteryCycles = battery.Cycles,
        batteryHealth = battery.Health
        };
    }

    private async Task ReadStateAsync(CancellationToken cancellationToken)
    {
        using var response = await AuthenticatedClient().GetAsync(Url("/api/device-agent/state"), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { MarkUnbound(); WriteAgentLog("网站已解绑本机，已立即切换为未绑定状态"); return; }
        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        if (state.TryGetProperty("rental", out var rental))
            _rental = rental.ValueKind == JsonValueKind.Object ? rental : null;
        if (state.TryGetProperty("serverTime", out var serverTime))
            _trustedServerTime = serverTime.ToString();
        if (state.TryGetProperty("inspectionRequested", out var inspectionRequested) && inspectionRequested.ValueKind == JsonValueKind.True)
        {
            await SendInspectionAsync("automated_health", BuildInspectionSnapshot(), cancellationToken);
            WriteAgentLog("收到网站验机请求，已上报最新自动巡检");
        }
        _statusText = "已连接";
        var memory = ReadMemoryMb() / 1024d;
        var storage = GetStorageFreeBytes() / 1073741824d;
        var startDate = _rental?.TryGetProperty("start_date", out var start) == true ? start.ToString() : null;
        var endDate = _rental?.TryGetProperty("end_date", out var end) == true ? end.ToString() : null;
        var rentalId = _rental?.TryGetProperty("id", out var id) == true ? id.ToString() : null;
        var rentalStarted = _rental.HasValue && string.Equals(_rental.Value.GetProperty("status").ToString(), "active", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(startDate, out var rentalStart) && rentalStart.Date <= RentalToday();
        var rentalStatus = _rental?.TryGetProperty("rental_status", out var rentalStatusValue) == true ? rentalStatusValue.ToString() : (_rental?.TryGetProperty("status", out var legacyStatus) == true ? legacyStatus.ToString() : null);
        WriteDashboardSnapshot(_statusText, rentalStatus, startDate, endDate, rentalId, rentalStarted, memory, storage);
        SaveState();
        _logger.LogDebug("Current device state: {State}", state.ToString());
    }

    private void MarkUnbound()
    {
        _token = null;
        _bindingRevoked = true;
        SaveState();
        File.WriteAllText(_unboundPath, DateTime.UtcNow.ToString("O"));
        var dashboardPath = Path.Combine(Path.GetDirectoryName(_statePath)!, "dashboard.json");
        File.WriteAllText(dashboardPath, JsonSerializer.Serialize(new { Status = "未绑定", BindingStatus = "unbound", DeviceMode = "normal", ProtocolRequired = false, Version = _options.Version, UpdatedAt = DateTime.Now }));
        _logger.LogWarning("Device binding was revoked by the server; automatic re-registration is disabled until a new installation/binding.");
    }

    private void WriteDashboardSnapshot(string status, string? rentalStatus, string? startDate, string? endDate, string? rentalId, bool protocolRequired, double? memoryGb, double? storageGb)
    {
        var dashboardPath = Path.Combine(Path.GetDirectoryName(_statePath)!, "dashboard.json");
        var customerName = _rental?.TryGetProperty("customer_name", out var customer) == true ? customer.ToString() : null;
        File.WriteAllText(dashboardPath, JsonSerializer.Serialize(new { Status = status, RentalStatus = rentalStatus, DeviceMode = _deviceMode, CustomerName = customerName, StartDate = startDate, EndDate = endDate, RentalId = rentalId, ServerTime = _trustedServerTime, ProtocolRequired = protocolRequired, MemoryGb = memoryGb ?? 0, StorageGb = storageGb ?? 0, MessageTitle = _messageTitle, MessageBody = _messageBody, Version = _options.Version, LatestVersion = _latestVersion, UpdateDownloadUrl = _updateDownloadUrl, DeviceId = _deviceId, RegisteredSerialNumber = _registeredSerialNumber, DetectedSerialNumber = _detectedSerialNumber, ApiBaseUrl = _options.ApiBaseUrl, UpdatedAt = DateTime.Now }));
    }

    private static double GetStorageGb() => new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace / 1073741824d;

    private DateTime RentalToday()
    {
        return DateTimeOffset.TryParse(_trustedServerTime, out var serverTime) ? serverTime.UtcDateTime.Date : DateTime.Now.Date;
    }

    private static long GetStorageFreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace; }
        catch { return 0; }
    }

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        var forced = _forceUpdateCheck;
        _forceUpdateCheck = false;
        if (!forced && (DateTime.UtcNow - _lastUpdateCheck).TotalHours < Math.Max(1, _options.UpdateCheckIntervalHours)) return;
        _lastUpdateCheck = DateTime.UtcNow;
        try
        {
            var client = _httpClientFactory.CreateClient("rent");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RentDeviceAgent/1.0");
            var release = await client.GetFromJsonAsync<GitHubRelease>($"https://api.github.com/repos/{_options.GitHubRepository}/releases/latest", cancellationToken);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName)) return;
            var version = release.TagName.TrimStart('v', 'V');
            if (!IsNewerVersion(version, _options.Version)) return;
            var architectureAsset = Environment.Is64BitOperatingSystem ? "RentDeviceAgent-x64.exe" : "RentDeviceAgent-x86.exe";
            var asset = release.Assets?.FirstOrDefault(item => string.Equals(item.Name, architectureAsset, StringComparison.OrdinalIgnoreCase))
                ?? release.Assets?.FirstOrDefault(item => string.Equals(item.Name, _options.GitHubReleaseAsset, StringComparison.OrdinalIgnoreCase))
                ?? release.Assets?.FirstOrDefault(item => item.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl)) return;
            _latestVersion = version;
            _updateDownloadUrl = $"https://github.com/{_options.GitHubRepository}/releases/tag/v{version}";
            WriteDashboardSnapshotFromCurrentState();
            if (!forced) return;
            var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "Updates");
            Directory.CreateDirectory(updateDirectory);
            var pending = Path.Combine(updateDirectory, "RentDeviceAgent.new.exe");
            await using var source = await client.GetStreamAsync(asset.BrowserDownloadUrl, cancellationToken);
            await using var target = File.Create(pending);
            await source.CopyToAsync(target, cancellationToken);
            if (!File.Exists(pending) || new FileInfo(pending).Length < 100_000)
                throw new InvalidOperationException("下载的更新文件无效或不完整");
            await using (var header = File.OpenRead(pending))
            {
                if (header.ReadByte() != 'M' || header.ReadByte() != 'Z')
                    throw new InvalidOperationException("下载内容不是 Windows EXE 文件");
            }
            var processPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RentDeviceAgent.exe");
            var isService = Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase));
            var updaterPath = Path.Combine(AppContext.BaseDirectory, "RentDeviceAgent.Updater.exe");
            if (!File.Exists(updaterPath)) throw new InvalidOperationException("找不到独立更新器，请重新安装客户端");
            var updaterArgs = $"--pending \"{pending}\" --target \"{processPath}\" --version \"{version}\" --source \"https://github.com/{_options.GitHubRepository}/releases/tag/v{version}\"" + (isService ? " --service" : "");
            // Signal the UI window that this shutdown is a sanctioned update so it
            // lets itself be closed; the updater clears the flag when it finishes.
            try { File.WriteAllText(_updatingFlagPath, DateTime.UtcNow.ToString("O")); } catch { }
            Process.Start(new ProcessStartInfo(updaterPath, updaterArgs) { CreateNoWindow = false, UseShellExecute = true });
            _statusText = $"发现新版本 {version}，正在更新";
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            WriteAgentLog($"自动更新失败：{ex.GetType().Name}: {ex.Message}");
            _logger.LogWarning(ex, "Update check failed");
        }
    }

    private static bool IsNewerVersion(string candidate, string current)
    {
        var candidateParts = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var currentParts = current.TrimStart('v', 'V').Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < Math.Max(candidateParts.Length, currentParts.Length); index++)
        {
            _ = int.TryParse(index < candidateParts.Length ? candidateParts[index] : "0", out var next);
            _ = int.TryParse(index < currentParts.Length ? currentParts[index] : "0", out var now);
            if (next != now) return next > now;
        }
        return false;
    }

    private HttpClient AuthenticatedClient()
    {
        var client = _httpClientFactory.CreateClient("rent");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    private string Url(string path) => _options.ApiBaseUrl.TrimEnd('/') + path;

    private void LoadState()
    {
        if (!File.Exists(_statePath)) return;
        try
        {
            var encrypted = Convert.FromBase64String(File.ReadAllText(_statePath));
            var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            var state = JsonSerializer.Deserialize<State>(plaintext);
            _token = state?.Token;
            _deviceId = state?.DeviceId;
            _registeredSerialNumber = state?.RegisteredSerialNumber;
            _detectedSerialNumber = state?.DetectedSerialNumber;
            _trustedServerTime = state?.TrustedServerTime;
            if (!string.IsNullOrWhiteSpace(state?.RentalJson)) _rental = JsonSerializer.Deserialize<JsonElement>(state.RentalJson);
        }
        catch { _token = null; }
    }

    private void SaveState()
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new State(_token, _trustedServerTime, _rental?.ToString(), _deviceId, _registeredSerialNumber, _detectedSerialNumber));
        var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.LocalMachine);
        File.WriteAllText(_statePath, Convert.ToBase64String(encrypted));
    }

    private static string? ReadWmiValue(string table, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {table}");
            return searcher.Get().Cast<ManagementObject>().FirstOrDefault()?[property]?.ToString();
        }
        catch { return null; }
    }

    private static bool HasWmiDevice(string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            return searcher.Get().Count > 0;
        }
        catch { return false; }
    }

    private static long? ReadMemoryMb()
    {
        var value = ReadWmiValue("Win32_ComputerSystem", "TotalPhysicalMemory");
        if (long.TryParse(value, out var bytes) && bytes > 0) return bytes / (1024 * 1024);
        var fallback = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return fallback > 0 ? fallback / (1024 * 1024) : null;
    }

    private static BatteryInfo ReadBatteryInfo()
    {
        var cycles = ReadWmiNamespaceUInt("root\\WMI", "SELECT CycleCount FROM BatteryCycleCount", "CycleCount");
        var fullCapacity = ReadWmiNamespaceUInt("root\\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
        var designCapacity = ReadWmiNamespaceUInt("root\\WMI", "SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
        var health = fullCapacity is > 0 && designCapacity is > 0
            ? $"{Math.Clamp((int)Math.Round(fullCapacity.Value * 100d / designCapacity.Value), 0, 100)}%"
            : null;
        return new BatteryInfo(cycles, health);
    }

    private static int? ReadWmiNamespaceUInt(string scopePath, string query, string property)
    {
        try
        {
            var scope = new ManagementScope($"\\\\.\\{scopePath}");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
            var value = searcher.Get().Cast<ManagementObject>().FirstOrDefault()?[property];
            return value is null ? null : Convert.ToInt32(value);
        }
        catch { return null; }
    }

    private static void WriteAgentLog(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "agent.log");
            if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024)
            {
                for (var index = 9; index >= 1; index--)
                {
                    var source = index == 1 ? path : $"{path}.{index - 1}";
                    var target = $"{path}.{index}";
                    if (File.Exists(source)) File.Copy(source, target, true);
                }
                File.WriteAllText(path, string.Empty);
            }
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private string? _trustedServerTime;
    private sealed record State(string? Token, string? TrustedServerTime, string? RentalJson, string? DeviceId = null, string? RegisteredSerialNumber = null, string? DetectedSerialNumber = null);
    private sealed record BatteryInfo(int? Cycles, string? Health);
    private sealed record RegisterResponse(bool Ok, string DeviceId, string SerialNumber, string Token);
    private sealed record AgentState(bool Ok, string DeviceId, string? DeviceMode, bool RemoteLockEnabled, string? LockMessage, bool CleanupRequested);
    private sealed record CommandEnvelope(bool Ok, DeviceCommand[] Commands);
    private sealed record DeviceCommand(string Id, string DeviceId, string CommandType, string Payload, string Status, string CreatedAt, string ExpiresAt);
    private enum AgentCommandType { SYNC, SHOW_MESSAGE, PAUSE_RENTAL, RESUME_RENTAL, REFRESH_DEVICE_INFO, CHECK_UPDATE, CREATE_RENTAL_USER, UPDATE_RENTAL_USER, DELETE_RENTAL_USER, CLEANUP_RENTAL_DATA }
    private sealed record GitHubRelease(string TagName, GitHubAsset[]? Assets);
    private sealed record GitHubAsset(string Name, string BrowserDownloadUrl);

}
