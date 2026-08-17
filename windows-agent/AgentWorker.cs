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

    public AgentWorker(IHttpClientFactory httpClientFactory, IOptions<AgentOptions> options, ILogger<AgentWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        LoadState();
        _bindingRevoked = File.Exists(_unboundPath);
        _logger.LogInformation("Rent Device Agent started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_bindingRevoked && string.IsNullOrWhiteSpace(_token)) await RegisterAsync(stoppingToken);
                if (!_bindingRevoked && !string.IsNullOrWhiteSpace(_token))
                {
                    await SendHeartbeatAsync(stoppingToken);
                    await ReadStateAsync(stoppingToken);
                    await ProcessCommandsAsync(stoppingToken);
                    await CheckForUpdateAsync(stoppingToken);
                }
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 6);
                _logger.LogWarning(ex, "Rent device agent sync failed; will retry");
                WriteAgentLog($"同步失败：{ex.GetType().Name}: {ex.Message}");
            }

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
        WriteDashboardSnapshot("已连接", null, null, null, false, ReadMemoryMb() / 1024d, GetStorageGb());
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
        var heartbeatResult = await response.Content.ReadFromJsonAsync<AgentState>(cancellationToken: cancellationToken);
        if (heartbeatResult?.Ok != true) throw new InvalidOperationException("网站未确认心跳");
        WriteAgentLog($"心跳成功：HTTP {(int)response.StatusCode}，设备 ID={_deviceId}");
        if (!string.IsNullOrWhiteSpace(heartbeatResult.DeviceMode)) _deviceMode = heartbeatResult.DeviceMode;
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
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<CommandEnvelope>(cancellationToken: cancellationToken);
        foreach (var command in envelope?.Commands ?? Array.Empty<DeviceCommand>())
        {
            var success = false;
            var resultCode = "FAILED";
            var message = "命令执行失败";
            try
            {
                if (command.DeviceId != _deviceId) throw new InvalidOperationException("命令不属于当前设备");
                if (!DateTimeOffset.TryParse(command.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow) { resultCode = "EXPIRED"; message = "命令已过期"; }
                else if (!Enum.TryParse<AgentCommandType>(command.CommandType, true, out var type)) { resultCode = "UNSUPPORTED"; message = "不支持的命令类型"; }
                else
                {
                    var commandPayload = JsonSerializer.Deserialize<JsonElement>(command.Payload ?? "{}" );
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
                    }
                }
            }
            catch (Exception ex) { message = ex.Message; WriteAgentLog($"命令 {command.Id} 执行失败：{ex.Message}"); }
            await ReportCommandResultAsync(command.Id, success, resultCode, message, cancellationToken);
        }
    }

    private static async Task ApplyRentalUserAsync(JsonElement payload, bool create)
    {
        var username = SafeWindowsUsername(payload.TryGetProperty("username", out var name) ? name.ToString() : "");
        var password = payload.TryGetProperty("password", out var pass) ? pass.ToString() : "";
        if (string.IsNullOrWhiteSpace(username) || username.Equals("Admin", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("租户账户资料无效");
        if (!create) await RunNetAsync("user", username, "/delete");
        await RunNetAsync("user", username, password, "/add", "/y");
        // Explicitly remove elevated membership even when Windows reused an existing
        // local account or an old provisioning attempt added it to Administrators.
        await RunNetBestEffortAsync("localgroup", "Administrators", username, "/delete");
        await RunNetAsync("localgroup", "Users", username, "/add");
        await InstallRentalShortcutsAsync(username);
    }

    private static async Task DeleteRentalUserAsync(JsonElement payload)
    {
        var username = SafeWindowsUsername(payload.TryGetProperty("username", out var name) ? name.ToString() : "");
        if (string.IsNullOrWhiteSpace(username) || username.Equals("Admin", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("禁止删除管理员账户");
        await RunNetAsync("user", username, "/delete");
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

    private static async Task InstallRentalShortcutsAsync(string username)
    {
        // Copy installed shortcuts from the public desktop/start menu. Missing apps are
        // intentionally ignored so account provisioning remains successful.
        const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$user = $env:RENTAL_USER
$desktop = Join-Path $env:SystemDrive ('Users\' + $user + '\Desktop')
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
  (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
  (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')
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
";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        using var process = new Process { StartInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(encoded);
        process.StartInfo.Environment["RENTAL_USER"] = username;
        if (!process.Start()) return;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) WriteAgentLog($"租户桌面快捷方式复制失败：{(await process.StandardError.ReadToEndAsync()).Trim()}");
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
        WriteDashboardSnapshot(_statusText, startDate, endDate, rentalId, false, null, null);
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
        WriteDashboardSnapshot(_statusText, startDate, endDate, rentalId, rentalStarted, memory, storage);
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

    private void WriteDashboardSnapshot(string status, string? startDate, string? endDate, string? rentalId, bool protocolRequired, double? memoryGb, double? storageGb)
    {
        var dashboardPath = Path.Combine(Path.GetDirectoryName(_statePath)!, "dashboard.json");
        File.WriteAllText(dashboardPath, JsonSerializer.Serialize(new { Status = status, DeviceMode = _deviceMode, StartDate = startDate, EndDate = endDate, RentalId = rentalId, ServerTime = _trustedServerTime, ProtocolRequired = protocolRequired, MemoryGb = memoryGb ?? 0, StorageGb = storageGb ?? 0, MessageTitle = _messageTitle, MessageBody = _messageBody, Version = _options.Version, LatestVersion = _latestVersion, UpdateDownloadUrl = _updateDownloadUrl, DeviceId = _deviceId, RegisteredSerialNumber = _registeredSerialNumber, DetectedSerialNumber = _detectedSerialNumber, ApiBaseUrl = _options.ApiBaseUrl, UpdatedAt = DateTime.Now }));
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
    private enum AgentCommandType { SYNC, SHOW_MESSAGE, PAUSE_RENTAL, RESUME_RENTAL, REFRESH_DEVICE_INFO, CHECK_UPDATE, CREATE_RENTAL_USER, UPDATE_RENTAL_USER, DELETE_RENTAL_USER }
    private sealed record GitHubRelease(string TagName, GitHubAsset[]? Assets);
    private sealed record GitHubAsset(string Name, string BrowserDownloadUrl);

}
