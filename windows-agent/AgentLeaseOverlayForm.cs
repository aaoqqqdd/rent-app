using System.Diagnostics;
using System.Text.Json;

public sealed class AgentLeaseOverlayForm : Form
{
    private readonly Label _lease = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold) };
    private readonly NotifyIcon _tray = new() { Icon = SystemIcons.Application, Visible = true, Text = "PC Rental 设备管理" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private ToolStripMenuItem? _toggleOverlay;
    private static readonly string SnapshotPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "dashboard.json");

    public AgentLeaseOverlayForm()
    {
        Text = "PC Rental 设备管理";
        Width = 330;
        Height = 145;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 18, area.Bottom - Height - 18);
        BackColor = Color.FromArgb(24, 40, 50);
        Padding = new Padding(18);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10);

        var title = new Label { Text = "PC Rental Device Agent", AutoSize = true, ForeColor = Color.FromArgb(113, 224, 181), Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), Location = new Point(18, 12) };
        _lease.Location = new Point(18, 36);
        var open = new Button { Text = "打开完整界面", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(37, 99, 235), Location = new Point(18, 68) };
        open.Click += (_, _) => { using var form = new AgentDashboardForm(); form.ShowDialog(this); };
        var bind = new Button { Text = "手动绑定", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(35, 58, 78), Location = new Point(145, 68) };
        bind.Click += (_, _) => { using var form = new AgentBindingForm(); form.ShowDialog(this); };
        Controls.AddRange([title, _lease, open, bind]);
        var windowMenu = new ContextMenuStrip();
        windowMenu.Items.Add("隐藏租期窗口", null, (_, _) => Hide());
        windowMenu.Items.Add("打开完整界面", null, (_, _) => open.PerformClick());
        ContextMenuStrip = windowMenu;
        foreach (Control control in Controls) control.ContextMenuStrip = windowMenu;
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _toggleOverlay = new ToolStripMenuItem("隐藏租期窗口");
        _toggleOverlay.Click += (_, _) => ToggleOverlay();
        _tray.ContextMenuStrip.Items.Add(_toggleOverlay);
        _tray.ContextMenuStrip.Items.Add("打开完整界面", null, (_, _) => open.PerformClick());
        _tray.ContextMenuStrip.Items.Add("手动绑定设备", null, (_, _) => bind.PerformClick());
        _tray.DoubleClick += (_, _) => { Show(); Activate(); };
        FormClosing += (_, e) => e.Cancel = IsBound();
        _timer.Tick += (_, _) => RefreshSnapshot();
        _timer.Start();
        RefreshSnapshot();
    }

    private void ToggleOverlay()
    {
        if (Visible) Hide(); else { Show(); Activate(); }
        if (_toggleOverlay is not null) _toggleOverlay.Text = Visible ? "隐藏租期窗口" : "显示租期窗口";
    }

    private void RefreshSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) { _lease.Text = "租期：等待设备连接"; _tray.Text = "PC Rental 设备管理 · 等待设备连接"; return; }
            var data = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
            if (data is null) return;
            if (string.Equals(data.BindingStatus, "unbound", StringComparison.OrdinalIgnoreCase))
            {
                _lease.Text = "设备未绑定";
                _tray.Text = "PC Rental 设备管理 · 设备未绑定";
                return;
            }
            if (data.ProtocolRequired && !AgentSoftwareAgreementForm.IsAccepted(data.StartDate ?? "current"))
            {
                Hide();
                AgentSoftwareAgreementForm.ShowIfRequired(data.StartDate ?? "current", this);
                return;
            }
            _lease.Text = $"到期：{data.EndDate ?? "暂无租期"}";
            _tray.Text = $"PC Rental 设备管理 · 到期：{data.EndDate ?? "暂无租期"}";
        }
        catch { _lease.Text = "租期：暂时无法读取"; }
    }

    private static bool IsBound()
    {
        var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "state.json");
        var unboundPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "unbound.flag");
        return File.Exists(statePath) && !File.Exists(unboundPath);
    }

    private sealed record Snapshot(string Status, string DeviceMode, string? StartDate, string? EndDate, bool ProtocolRequired, double MemoryGb, double StorageGb, string Version, DateTime UpdatedAt, string? ApiBaseUrl, string? BindingStatus);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tray.Visible = false; _tray.Dispose(); }
        base.Dispose(disposing);
    }
}
