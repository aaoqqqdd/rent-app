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

// Whether the Windows service is *installed* — not whether it happens to be RUNNING
// this millisecond. During a user switch / logon storm the SCM is busy and
// "sc query" often reports START_PENDING or times out; treating that as "no service"
// makes the --ui process spin up a second AgentWorker that fights the real one.
// If the service exists, the SCM (with its restart-on-failure actions) owns keeping
// it alive and the UI must never host its own worker.
static bool IsAgentServiceInstalled()
{
    for (var attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe", "query RentDeviceAgent")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            var output = (process?.StandardOutput.ReadToEnd() ?? "") + (process?.StandardError.ReadToEnd() ?? "");
            process?.WaitForExit(5000);
            // 1060 = ERROR_SERVICE_DOES_NOT_EXIST. Anything else (any STATE line, even
            // "access denied") means the service is installed.
            if (output.Contains("1060")) return false;
            if (output.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)
                || output.Contains("STATE", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        Thread.Sleep(1000);
    }
    // Inconclusive every time: assume installed. A UI with no local worker is safe
    // (the service catches up); a duplicate worker is not.
    return true;
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
if (!IsAgentServiceInstalled())
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
