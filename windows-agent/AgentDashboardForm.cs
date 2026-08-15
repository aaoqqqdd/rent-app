using System.Text.Json;
using System.Diagnostics;

public sealed class AgentDashboardForm : Form
{
    private readonly Label _status = LabelFor("正在读取客户端状态");
    private readonly Label _rental = LabelFor("租期：—");
    private readonly Label _hardware = LabelFor("内存：—    剩余存储：—");
    private readonly Label _version = LabelFor("版本：—");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly NotifyIcon _notify = new() { Icon = SystemIcons.Application, Visible = false, Text = "Rent 设备管理" };
    private string _customerPanelUrl = "";
    private bool _expiryNotified;
    private static readonly string SnapshotPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "dashboard.json");

    public AgentDashboardForm()
    {
        Text = "Rent 设备管理";
        Width = 920;
        Height = 640;
        FormBorderStyle = FormBorderStyle.Sizable;
        TopMost = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(16, 24, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 11);
        var title = LabelFor("Rent Device Agent", 24, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(113, 224, 181);
        var subtitle = LabelFor("设备租赁客户端", 10, FontStyle.Regular);
        subtitle.ForeColor = Color.LightGray;
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var customer = new Button { Text = "客户面板  [F1]", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(113, 224, 181), BackColor = Color.FromArgb(24, 40, 50), Margin = new Padding(0, 2, 0, 10) };
        customer.Click += (_, _) => OpenCustomerPanel();
        layout.Controls.Add(title); layout.Controls.Add(subtitle); layout.Controls.Add(_status); layout.Controls.Add(_rental); layout.Controls.Add(_hardware); layout.Controls.Add(_version); layout.Controls.Add(customer);
        panel.Controls.Add(layout); Controls.Add(panel);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F1) OpenCustomerPanel(); if (e.KeyCode == Keys.F5) RefreshSnapshot(); e.Handled = true; };
        _notify.DoubleClick += (_, _) => Activate();
        _timer.Tick += (_, _) => RefreshSnapshot();
        _timer.Start();
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) return;
            var data = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
            if (data is null) return;
            _status.Text = $"状态：{data.Status}    模式：{data.DeviceMode}";
            _rental.Text = $"租期：{data.StartDate ?? "—"} 至 {data.EndDate ?? "暂无租期"}";
            _hardware.Text = $"内存：{data.MemoryGb:0.0} GB    剩余存储：{data.StorageGb:0.0} GB";
            _version.Text = $"版本：{data.Version}    最后同步：{data.UpdatedAt:yyyy-MM-dd HH:mm:ss}";
            _customerPanelUrl = data.ApiBaseUrl?.TrimEnd('/') + "/login";
            if (!_expiryNotified && DateTime.TryParse(data.EndDate, out var endDate) && (endDate.Date - DateTime.Now.Date).TotalDays <= 3 && endDate.Date >= DateTime.Now.Date)
            {
                _notify.ShowBalloonTip(8000, "租期即将到期", $"设备租期将在 {endDate:yyyy-MM-dd} 到期。", ToolTipIcon.Warning);
                _expiryNotified = true;
            }
        }
        catch { _status.Text = "状态：暂时无法读取"; }
    }

    private void OpenCustomerPanel()
    {
        if (string.IsNullOrWhiteSpace(_customerPanelUrl)) return;
        Process.Start(new ProcessStartInfo(_customerPanelUrl) { UseShellExecute = true });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _notify.Visible = false; _notify.Dispose(); }
        base.Dispose(disposing);
    }

    private static Label LabelFor(string text, float size = 15, FontStyle style = FontStyle.Regular) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 18), Font = new Font("Segoe UI", size, style), ForeColor = Color.White };
    private sealed record Snapshot(string Status, string DeviceMode, string? StartDate, string? EndDate, bool ProtocolRequired, double MemoryGb, double StorageGb, string Version, DateTime UpdatedAt, string? ApiBaseUrl);
}
