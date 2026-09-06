public sealed class AgentOptions
{
    public string ApiBaseUrl { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string SetupCode { get; set; } = "";
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    public string Version { get; set; } = "1.0.0";
    public string UpdateManifestUrl { get; set; } = "";
    public string GitHubRepository { get; set; } = "aaoqqqdd/rent-app";
    public string GitHubReleaseAsset { get; set; } = "RentDeviceAgent-x64.exe";
    public int UpdateCheckIntervalHours { get; set; } = 1;
    public int DashboardPort { get; set; } = 47821;
    public DataCleanupOptions DataCleanup { get; set; } = new();
}

/// <summary>
/// 租约结束 / 归还时自动清除承租人个人痕迹的开关，对应 appsettings.json 的 RentDeviceAgent:DataCleanup 段。
/// </summary>
public sealed class DataCleanupOptions
{
    /// <summary>总开关。关闭后 CLEANUP_RENTAL_DATA 指令与租约到期自动清理都会跳过。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>心跳返回 cleanupRequested=true（网站判定租约已结束）时自动清理一次。</summary>
    public bool RunOnLeaseEnd { get; set; } = true;
    public bool WipeBrowsers { get; set; } = true;
    public bool WipeWeChat { get; set; } = true;
    public bool WipeRecycleBin { get; set; } = true;
    /// <summary>DELETE_RENTAL_USER 时一并删除承租人用户目录与配置注册表项。</summary>
    public bool RemoveUserProfileOnDelete { get; set; } = true;
    /// <summary>额外要清理的路径；相对路径按承租人用户目录展开，绝对路径原样使用。</summary>
    public string[] ExtraPaths { get; set; } = System.Array.Empty<string>();
}
