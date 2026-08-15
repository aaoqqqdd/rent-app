using System.Diagnostics;
using System.Text.Json;

public sealed class AgentBindingForm : Form
{
    private static readonly Color Navy = Color.FromArgb(13, 27, 42);
    private static readonly Color Panel = Color.FromArgb(20, 39, 56);
    private static readonly Color Blue = Color.FromArgb(59, 130, 246);
    private static readonly Color Mint = Color.FromArgb(91, 214, 173);
    private readonly TextBox _code = new() { MaxLength = 6, Width = 180, Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold), TextAlign = HorizontalAlignment.Center };
    private readonly Label _message = new() { AutoSize = true, ForeColor = Color.FromArgb(166, 184, 198), MaximumSize = new Size(430, 60) };
    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public AgentBindingForm()
    {
        Text = "PC Rental · 手动绑定设备"; Width = 520; Height = 360; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; BackColor = Navy; ForeColor = Color.White; AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Microsoft YaHei UI", 10);
        var title = new Label { Text = "绑定 Windows 设备", AutoSize = true, Font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold), ForeColor = Mint, Location = new Point(34, 28) };
        var hint = new Label { Text = "请输入网站设备详情页生成的 6 位访问码。", AutoSize = true, ForeColor = Color.FromArgb(166, 184, 198), Location = new Point(36, 72) };
        var card = new Panel { BackColor = Panel, Location = new Point(32, 112), Size = new Size(440, 135), Padding = new Padding(20) };
        var label = new Label { Text = "访问码", AutoSize = true, ForeColor = Color.White, Location = new Point(20, 22) }; _code.Location = new Point(20, 52);
        var bind = new Button { Text = "保存并连接", AutoSize = true, Height = 36, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, BackColor = Blue, ForeColor = Color.White, Location = new Point(220, 51), Padding = new Padding(14, 0, 14, 0) }; bind.Click += (_, _) => Bind(); _code.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Bind(); };
        card.Controls.AddRange([label, _code, bind]); _message.Location = new Point(36, 270); Controls.AddRange([title, hint, card, _message]); AcceptButton = bind;
    }

    private void Bind()
    {
        var code = _code.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit)) { _message.ForeColor = Color.FromArgb(248, 113, 113); _message.Text = "访问码必须是 6 位数字。"; return; }
        try
        {
            if (!File.Exists(SettingsPath)) throw new InvalidOperationException("找不到客户端配置文件。");
            var settings = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(SettingsPath));
            var json = JsonSerializer.Deserialize<Dictionary<string, object>>(settings.GetRawText()) ?? new();
            var agent = JsonSerializer.Deserialize<Dictionary<string, object>>(settings.GetProperty("RentDeviceAgent").GetRawText()) ?? new(); agent["SetupCode"] = code; json["RentDeviceAgent"] = agent;
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            _message.ForeColor = Color.FromArgb(91, 214, 173); _message.Text = "访问码已保存，正在重启客户端服务……";
            try { RunServiceCommand("stop"); Thread.Sleep(2500); RunServiceCommand("start"); } catch { _message.Text = "访问码已保存，请以管理员身份重启 RentDeviceAgent 服务。"; return; }
            Close();
        }
        catch (UnauthorizedAccessException) { _message.ForeColor = Color.FromArgb(248, 113, 113); _message.Text = "无法写入配置，请右键客户端并选择“以管理员身份运行”。"; }
        catch (Exception ex) { _message.ForeColor = Color.FromArgb(248, 113, 113); _message.Text = $"绑定配置失败：{ex.Message}"; }
    }

    private static void RunServiceCommand(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", $"{command} RentDeviceAgent") { Verb = "runas", UseShellExecute = true });
        process?.WaitForExit(15000);
        if (process is null || process.ExitCode != 0) throw new InvalidOperationException("服务重启失败");
    }
}
