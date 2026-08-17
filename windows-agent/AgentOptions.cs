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
}
