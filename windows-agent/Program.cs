using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (!args.Contains("--service", StringComparer.OrdinalIgnoreCase))
{
    ApplicationConfiguration.Initialize();
    Application.Run(new AgentLeaseOverlayForm());
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "PC Rental Device Agent");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("RentDeviceAgent"));
builder.Services.AddHttpClient("rent", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
