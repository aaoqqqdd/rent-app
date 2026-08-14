public sealed class AgentOptions
{
    public string ApiBaseUrl { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string SetupCode { get; set; } = "";
    public int HeartbeatIntervalSeconds { get; set; } = 60;
}
