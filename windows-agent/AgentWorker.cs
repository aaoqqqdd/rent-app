using System.Management;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rent device agent sync failed; will retry");
            }

            var seconds = Math.Clamp(_options.HeartbeatIntervalSeconds, 30, 3600);
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
        _logger.LogDebug("Current device state: {State}", state.ToString());
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
            _token = JsonSerializer.Deserialize<State>(plaintext)?.Token;
        }
        catch { _token = null; }
    }

    private void SaveState()
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new State(_token));
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

    private sealed record State(string? Token);
    private sealed record RegisterResponse(bool Ok, string DeviceId, string SerialNumber, string Token);
    private sealed record AgentState(bool Ok, string DeviceId, string? DeviceMode, bool RemoteLockEnabled, string? LockMessage);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();
}
