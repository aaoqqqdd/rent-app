using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

static IHost CreateAgentHost(string[] arguments)
{
    var builder = Host.CreateApplicationBuilder(arguments);
    builder.Services.AddWindowsService(options => options.ServiceName = "PC Rental Device Agent");
    builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("RentDeviceAgent"));
    builder.Services.AddHttpClient("rent", client => client.Timeout = TimeSpan.FromSeconds(20));
    builder.Services.AddHostedService<AgentWorker>();
    return builder.Build();
}

static bool IsAgentServiceRunning()
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", "query RentDeviceAgent") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        var output = process?.StandardOutput.ReadToEnd() ?? "";
        process?.WaitForExit(3000);
        return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}

if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
{
    await CreateAgentHost(args).RunAsync();
    return;
}

ApplicationConfiguration.Initialize();
using var uiMutex = new Mutex(true, "Local\\RentDeviceAgent.UI", out var isFirstUiInstance);
if (!isFirstUiInstance) return;
IHost? localHost = null;
if (!IsAgentServiceRunning())
{
    localHost = CreateAgentHost(args);
}

var overlay = new AgentLeaseOverlayForm();
if (localHost is not null)
{
    overlay.Shown += async (_, _) =>
    {
        try { await localHost.StartAsync(); }
        catch { }
    };
}
Application.Run(overlay);
if (localHost is not null)
{
    await localHost.StopAsync();
    localHost.Dispose();
}
