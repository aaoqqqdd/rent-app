using System.Diagnostics;
using System.Text.Json;

var rawArgs = Environment.GetCommandLineArgs();
string Get(string name) => Array.FindIndex(rawArgs, x => x.Equals(name, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 && i + 1 < rawArgs.Length ? rawArgs[i + 1] : "";
var pending = Get("--pending");
var target = Get("--target");
var version = Get("--version");
var source = Get("--source");
var service = rawArgs.Any(x => x.Equals("--service", StringComparison.OrdinalIgnoreCase));
if (string.IsNullOrWhiteSpace(pending) || string.IsNullOrWhiteSpace(target)) return;
try
{
    await Task.Delay(1500);
    if (service)
    {
        using var stop = Process.Start(new ProcessStartInfo("sc.exe", "stop RentDeviceAgent") { CreateNoWindow = true, UseShellExecute = false });
        stop?.WaitForExit(15000);
        await Task.Delay(2500);
    }
    for (var i = 0; i < 15; i++)
    {
        try { File.Copy(pending, target, true); File.Delete(pending); break; }
        catch when (i < 14) { await Task.Delay(1000); }
    }
    var settingsPath = Path.Combine(Path.GetDirectoryName(target)!, "appsettings.json");
    if (File.Exists(settingsPath) && !string.IsNullOrWhiteSpace(version))
    {
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(settingsPath)) ?? new();
        if (settings.TryGetValue("RentDeviceAgent", out var agent))
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, object>>(agent.GetRawText()) ?? new();
            values["Version"] = version;
            settings["RentDeviceAgent"] = JsonSerializer.SerializeToElement(values);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    if (service) Process.Start(new ProcessStartInfo("sc.exe", "start RentDeviceAgent") { CreateNoWindow = true, UseShellExecute = false });
    else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
catch (Exception ex)
{
    MessageBox.Show($"更新失败：{ex.Message}", "PC Rental 软件更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
