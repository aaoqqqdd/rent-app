using System.Management;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Options;

public sealed class AgentWorker : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _logger;
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RentDeviceAgent", "state.json");
    private string? _token;
    private string _deviceMode = "normal";
    private bool _beforeSnapshotSent;
    private string _statusText = "正在连接";
    private JsonElement? _rental;
    private DateTime _lastUpdateCheck = DateTime.MinValue;
    private int _consecutiveFailures;

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
        _logger.LogInformation("Rent Device Agent started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_token)) await RegisterAsync(stoppingToken);
                if (!string.IsNullOrWhiteSpace(_token))
                {
                    await SendHeartbeatAsync(stoppingToken);
                    await ReadStateAsync(stoppingToken);
                    await CheckForUpdateAsync(stoppingToken);
                }
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 6);
                _logger.LogWarning(ex, "Rent device agent sync failed; will retry");
            }

            var baseSeconds = Math.Clamp(_options.HeartbeatIntervalSeconds, 30, 3600);
            var seconds = _consecutiveFailures == 0
                ? baseSeconds
                : Math.Min(300, Math.Max(5, 5 * (1 << Math.Min(_consecutiveFailures - 1, 5))));
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl))
            throw new InvalidOperationException("ApiBaseUrl is required");
        if (string.IsNullOrWhiteSpace(_options.SetupCode) && Environment.UserInteractive)
        {
            Console.Write("请输入 6 位设备注册码：");
            _options.SetupCode = (Console.ReadLine() ?? "").Trim();
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(_options.SetupCode, "^\\d{6}$"))
            throw new InvalidOperationException("请输入 6 位数字注册码");

        var client = _httpClientFactory.CreateClient("rent");
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/device-agent/register"));
        request.Content = JsonContent.Create(new { serialNumber = _options.SerialNumber, setupCode = _options.SetupCode });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RegisterResponse>(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(result?.Token)) throw new InvalidOperationException("Registration response did not contain a token");
        _token = result.Token;
        if (string.IsNullOrWhiteSpace(_token)) throw new InvalidOperationException("Registration token is empty");
        SaveState();
        _options.SerialNumber = result.SerialNumber;
        _options.SetupCode = "";
        _logger.LogInformation("Device registered as {DeviceId}", result.DeviceId);
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var client = AuthenticatedClient();
        var payload = new
        {
            hostname = Environment.MachineName,
            osVersion = Environment.OSVersion.VersionString,
            cpu = ReadWmiValue("Win32_Processor", "Name"),
            memoryMb = ReadMemoryMb(),
            storageFreeBytes = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace,
            inspectionType = _deviceMode == "return" ? "after_return" : (_beforeSnapshotSent ? "automated_health" : "before_rental"),
            snapshot = new { hostname = Environment.MachineName, osVersion = Environment.OSVersion.VersionString, cpu = ReadWmiValue("Win32_Processor", "Name"), memoryMb = ReadMemoryMb(), storageFreeBytes = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace }
        };
        using var response = await client.PostAsJsonAsync(Url("/api/device-agent/heartbeat"), payload, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { _token = null; SaveState(); return; }
        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<AgentState>(cancellationToken: cancellationToken);
        if (state?.RemoteLockEnabled == true) LockWorkStation();
        if (!string.IsNullOrWhiteSpace(state?.DeviceMode)) _deviceMode = state.DeviceMode;
        _beforeSnapshotSent = true;
    }

    private async Task ReadStateAsync(CancellationToken cancellationToken)
    {
        using var response = await AuthenticatedClient().GetAsync(Url("/api/device-agent/state"), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) { _token = null; SaveState(); return; }
        response.EnsureSuccessStatusCode();
        var state = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        if (state.TryGetProperty("rental", out var rental)) _rental = rental;
        if (state.TryGetProperty("serverTime", out var serverTime))
            _trustedServerTime = serverTime.ToString();
        _statusText = "已连接";
        var memory = ReadMemoryMb() / 1024d;
        var storage = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace / 1073741824d;
        var startDate = _rental?.TryGetProperty("start_date", out var start) == true ? start.ToString() : null;
        var endDate = _rental?.TryGetProperty("end_date", out var end) == true ? end.ToString() : null;
        var rentalStarted = _rental.HasValue && string.Equals(_rental.Value.GetProperty("status").ToString(), "active", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(startDate, out var rentalStart) && rentalStart.Date <= DateTime.Now.Date;
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(_statePath)!, "dashboard.json"), JsonSerializer.Serialize(new { Status = _statusText, DeviceMode = _deviceMode, StartDate = startDate, EndDate = endDate, ProtocolRequired = rentalStarted, Version = _options.Version, ApiBaseUrl = _options.ApiBaseUrl, UpdatedAt = DateTime.Now }), cancellationToken);
        SaveState();
        if (state.TryGetProperty("cleanupRequested", out var cleanup) && cleanup.GetBoolean()) await CleanupNonAdminProfilesAsync(cancellationToken);
        _logger.LogDebug("Current device state: {State}", state.ToString());
    }

    private async Task CleanupNonAdminProfilesAsync(CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        try
        {
            foreach (var processName in new[] { "chrome", "msedge", "firefox", "微信", "WeChat", "WeChatApp" })
                foreach (var process in Process.GetProcessesByName(processName)) try { process.Kill(true); } catch { }
            var administrators = GetAdministratorNames();
            var usersRoot = Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "Users");
            foreach (var profile in Directory.Exists(usersRoot) ? Directory.GetDirectories(usersRoot) : Array.Empty<string>())
            {
                var name = Path.GetFileName(profile.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name) || new[] { "Public", "Default", "Default User", "All Users" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (administrators.Contains(name, StringComparer.OrdinalIgnoreCase)) { skipped.Add(name); continue; }
                foreach (var target in new[] {
                    Path.Combine(profile, "Downloads"),
                    Path.Combine(profile, "AppData", "Local", "Google", "Chrome", "User Data"),
                    Path.Combine(profile, "AppData", "Local", "Microsoft", "Edge", "User Data"),
                    Path.Combine(profile, "AppData", "Roaming", "Mozilla", "Firefox"),
                    Path.Combine(profile, "AppData", "Roaming", "Tencent", "WeChat"),
                    Path.Combine(profile, "Documents", "WeChat Files")
                })
                {
                    if (!Directory.Exists(target)) continue;
                    try { Directory.Delete(target, true); removed.Add(target); } catch (Exception error) { errors.Add($"{target}: {error.Message}"); }
                }
            }
        }
        catch (Exception error) { errors.Add(error.Message); }
        using var response = await AuthenticatedClient().PostAsJsonAsync(Url("/api/device-agent/cleanup-result"), new { ok = errors.Count == 0, removed, skipped, errors }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static HashSet<string> GetAdministratorNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Administrator", "admin" };
        try
        {
            using var groupSearcher = new ManagementObjectSearcher("SELECT __PATH FROM Win32_Group WHERE SID = 'S-1-5-32-544'");
            var group = groupSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
            var groupPath = group?["__PATH"]?.ToString();
            if (string.IsNullOrWhiteSpace(groupPath)) return names;
            using var memberSearcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{{groupPath}}} WHERE AssocClass=Win32_GroupUser Role=GroupComponent");
            foreach (ManagementObject item in memberSearcher.Get())
            {
                var accountName = item["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(accountName)) names.Add(accountName);
            }
        }
        catch { }
        return names;
    }

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        if ((DateTime.UtcNow - _lastUpdateCheck).TotalHours < Math.Max(1, _options.UpdateCheckIntervalHours)) return;
        _lastUpdateCheck = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(_options.UpdateManifestUrl)) return;
        try
        {
            var client = _httpClientFactory.CreateClient("rent");
            var update = await client.GetFromJsonAsync<UpdateInfo>(_options.UpdateManifestUrl, cancellationToken);
            if (update is null || string.IsNullOrWhiteSpace(update.Version) || string.IsNullOrWhiteSpace(update.DownloadUrl) || update.Version == _options.Version) return;
            var pending = Path.Combine(AppContext.BaseDirectory, "RentDeviceAgent.new.exe");
            await using var source = await client.GetStreamAsync(update.DownloadUrl, cancellationToken);
            await using var target = File.Create(pending);
            await source.CopyToAsync(target, cancellationToken);
            var script = Path.Combine(Path.GetTempPath(), "RentDeviceAgent-update.ps1");
            await File.WriteAllTextAsync(script, $"Start-Sleep -Seconds 3; Move-Item -Force '{pending}' '{Environment.ProcessPath}'; Start-Process '{Environment.ProcessPath}'", cancellationToken);
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { CreateNoWindow = true, UseShellExecute = false });
            _statusText = $"发现新版本 {update.Version}，正在更新";
            Environment.Exit(0);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Update check failed"); }
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
            _trustedServerTime = state?.TrustedServerTime;
            if (!string.IsNullOrWhiteSpace(state?.RentalJson)) _rental = JsonSerializer.Deserialize<JsonElement>(state.RentalJson);
        }
        catch { _token = null; }
    }

    private void SaveState()
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new State(_token, _trustedServerTime, _rental?.ToString()));
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

    private static long? ReadMemoryMb()
    {
        var value = ReadWmiValue("Win32_ComputerSystem", "TotalPhysicalMemory");
        return long.TryParse(value, out var bytes) ? bytes / (1024 * 1024) : null;
    }

    private string? _trustedServerTime;
    private sealed record State(string? Token, string? TrustedServerTime, string? RentalJson);
    private sealed record RegisterResponse(bool Ok, string DeviceId, string SerialNumber, string Token);
    private sealed record AgentState(bool Ok, string DeviceId, string? DeviceMode, bool RemoteLockEnabled, string? LockMessage, bool CleanupRequested);
    private sealed record UpdateInfo(string Version, string DownloadUrl);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();
}
