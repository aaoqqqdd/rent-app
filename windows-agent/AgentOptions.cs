public sealed class AgentOptions
{
    public string ApiBaseUrl { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string SetupCode { get; set; } = "";
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    public string Version { get; set; } = "1.0.0";
    public string UpdateManifestUrl { get; set; } = "";
    public int UpdateCheckIntervalHours { get; set; } = 6;
    public int DashboardPort { get; set; } = 47821;
}
