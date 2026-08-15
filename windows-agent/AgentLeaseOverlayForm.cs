using System.Diagnostics;
using System.Text.Json;

public sealed class AgentLeaseOverlayForm : Form
{
    private readonly Label _lease = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    private readonly NotifyIcon _tray = new() { Icon = SystemIcons.Information, Visible = true, Text = "Rent Device Agent" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private static readonly string SnapshotPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "dashboard.json");

    public AgentLeaseOverlayForm()
    {
        Text = "Rent Device Agent";
        Width = 330;
        Height = 145;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 18, area.Bottom - Height - 18);
        BackColor = Color.FromArgb(24, 40, 50);
        Padding = new Padding(18);

        var title = new Label { Text = "Rent Device Agent", AutoSize = true, ForeColor = Color.FromArgb(113, 224, 181), Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(18, 12) };
        _lease.Location = new Point(18, 36);
        var open = new Button { Text = "打开完整界面", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(37, 99, 235), Location = new Point(18, 68) };
        open.Click += (_, _) => { using var form = new AgentDashboardForm(); form.ShowDialog(this); };
        Controls.AddRange([title, _lease, open]);
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Items.Add("打开租期窗口", null, (_, _) => { Show(); Activate(); });
        _tray.ContextMenuStrip.Items.Add("打开完整界面", null, (_, _) => open.PerformClick());
        _tray.DoubleClick += (_, _) => { Show(); Activate(); };
        FormClosing += (_, e) => e.Cancel = true;
        _timer.Tick += (_, _) => RefreshSnapshot();
        _timer.Start();
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) { _lease.Text = "租期：等待设备连接"; _tray.Text = "Rent Device Agent · 等待租期"; return; }
            var data = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
            if (data is null) return;
            if (data.ProtocolRequired && !AgentSoftwareAgreementForm.IsAccepted(data.StartDate ?? "current"))
            {
                Hide();
                AgentSoftwareAgreementForm.ShowIfRequired(data.StartDate ?? "current", this);
                return;
            }
            _lease.Text = $"到期：{data.EndDate ?? "暂无租期"}";
            _tray.Text = $"Rent Device Agent · 到期：{data.EndDate ?? "暂无租期"}";
        }
        catch { _lease.Text = "租期：暂时无法读取"; }
    }

    private sealed record Snapshot(string Status, string DeviceMode, string? StartDate, string? EndDate, bool ProtocolRequired, double MemoryGb, double StorageGb, string Version, DateTime UpdatedAt, string? ApiBaseUrl);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tray.Visible = false; _tray.Dispose(); }
        base.Dispose(disposing);
    }
}
