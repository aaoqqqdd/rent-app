using System.Text.Json;
using System.Diagnostics;

public sealed class AgentDashboardForm : Form
{
    private static readonly Color Navy = Color.FromArgb(13, 27, 42);
    private static readonly Color Panel = Color.FromArgb(20, 39, 56);
    private static readonly Color Blue = Color.FromArgb(59, 130, 246);
    private static readonly Color Mint = Color.FromArgb(91, 214, 173);
    private static readonly Color Muted = Color.FromArgb(166, 184, 198);
    private readonly Label _status = ValueLabel("正在读取客户端状态");
    private readonly Label _mode = ValueLabel("—");
    private readonly Label _rental = ValueLabel("—");
    private readonly Label _hardware = ValueLabel("—");
    private readonly Label _version = ValueLabel("—");
    private readonly Label _identity = ValueLabel("—");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly NotifyIcon _notify = new() { Icon = SystemIcons.Application, Visible = false, Text = "PC Rental 设备管理" };
    private string _customerPanelUrl = "";
    private bool _expiryNotified;
    private static readonly string SnapshotPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "dashboard.json");

    public AgentDashboardForm()
    {
        Text = "PC Rental 设备管理"; Width = 900; Height = 620; MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Navy; ForeColor = Color.White;
        Font = new Font("Segoe UI", 10); KeyPreview = true;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(32, 26, 32, 28), BackColor = Navy };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(Header(), 0, 0); root.Controls.Add(Content(), 0, 1); root.Controls.Add(Footer(), 0, 2); Controls.Add(root);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F1) OpenCustomerPanel(); if (e.KeyCode == Keys.F5) RefreshSnapshot(); e.Handled = true; };
        _notify.DoubleClick += (_, _) => { Show(); Activate(); }; _timer.Tick += (_, _) => RefreshSnapshot(); _timer.Start(); RefreshSnapshot();
    }

    private Control Header()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        var eyebrow = LabelFor("PC RENTAL / DEVICE AGENT", 9, FontStyle.Bold); eyebrow.ForeColor = Mint; eyebrow.Margin = new Padding(0, 0, 0, 3);
        var title = LabelFor("设备控制台", 25, FontStyle.Bold); title.Margin = new Padding(0); layout.Controls.Add(eyebrow, 0, 0); layout.Controls.Add(title, 0, 1);
        var button = ActionButton("打开客户面板  [F1]", Blue); button.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; button.Margin = new Padding(0, 0, 0, 8); button.Click += (_, _) => OpenCustomerPanel(); layout.Controls.Add(button, 1, 1); return layout;
    }

    private Control Content()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 16, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.Controls.Add(Card("连接状态", _status, "客户端正在与租赁平台同步"), 0, 0); layout.Controls.Add(Card("设备模式", _mode, "由租期和平台策略决定"), 1, 0); layout.Controls.Add(Card("当前租期", _rental, "租期结束后设备将按平台规则处理"), 0, 1); layout.Controls.Add(Card("设备资源", _hardware, "最近一次心跳上报的数据"), 1, 1); return layout;
    }

    private Control Card(string title, Label value, string hint)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 12, 12) };
        var eyebrow = LabelFor(title.ToUpperInvariant(), 9, FontStyle.Bold); eyebrow.ForeColor = Muted; eyebrow.Location = new Point(20, 18);
        value.Location = new Point(20, 43); value.MaximumSize = new Size(360, 48); value.ForeColor = Color.White;
        var note = LabelFor(hint, 9); note.ForeColor = Muted; note.Location = new Point(20, 84); note.MaximumSize = new Size(360, 32); panel.Controls.AddRange([eyebrow, value, note]); return panel;
    }

    private Control Footer()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        _version.ForeColor = Muted; _version.Font = new Font("Segoe UI", 9); _version.Margin = new Padding(0, 2, 0, 0); _identity.ForeColor = Muted; _identity.Font = new Font("Segoe UI", 9); _identity.Margin = new Padding(0, 20, 0, 0); layout.Controls.Add(_version, 0, 0); layout.Controls.Add(_identity, 0, 1);
        var refresh = ActionButton("刷新  [F5]", Color.FromArgb(35, 58, 78)); refresh.Anchor = AnchorStyles.Right; refresh.Click += (_, _) => RefreshSnapshot(); layout.Controls.Add(refresh, 1, 0); return layout;
    }

    private void RefreshSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) return; var data = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath)); if (data is null) return;
            _status.Text = data.Status; _mode.Text = data.DeviceMode; _rental.Text = data.EndDate is null ? "暂无租期" : $"{data.StartDate ?? "—"}  —  {data.EndDate}"; _hardware.Text = $"内存 {data.MemoryGb:0.0} GB  ·  剩余存储 {data.StorageGb:0.0} GB"; _version.Text = $"版本 {data.Version}  ·  最后同步 {data.UpdatedAt:yyyy-MM-dd HH:mm:ss}"; _identity.Text = $"识别码 {data.DeviceId ?? "未绑定"}  ·  序列号 {data.SerialNumber ?? "—"}"; _customerPanelUrl = data.ApiBaseUrl?.TrimEnd('/') + "/login";
            if (!_expiryNotified && DateTime.TryParse(data.EndDate, out var endDate) && (endDate.Date - DateTime.Now.Date).TotalDays <= 3 && endDate.Date >= DateTime.Now.Date) { _notify.ShowBalloonTip(8000, "租期即将到期", $"设备租期将在 {endDate:yyyy-MM-dd} 到期。", ToolTipIcon.Warning); _expiryNotified = true; }
        }
        catch { _status.Text = "暂时无法读取状态"; }
    }

    private void OpenCustomerPanel() { if (!string.IsNullOrWhiteSpace(_customerPanelUrl)) Process.Start(new ProcessStartInfo(_customerPanelUrl) { UseShellExecute = true }); }
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _notify.Visible = false; _notify.Dispose(); } base.Dispose(disposing); }
    private static Label LabelFor(string text, float size = 15, FontStyle style = FontStyle.Regular) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", size, style), ForeColor = Color.White };
    private static Label ValueLabel(string text) => LabelFor(text, 14, FontStyle.Bold);
    private static Button ActionButton(string text, Color background) => new() { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, BackColor = background, ForeColor = Color.White, Padding = new Padding(14, 0, 14, 0), Cursor = Cursors.Hand };
    private sealed record Snapshot(string Status, string DeviceMode, string? StartDate, string? EndDate, bool ProtocolRequired, double MemoryGb, double StorageGb, string Version, DateTime UpdatedAt, string? ApiBaseUrl, string? DeviceId = null, string? SerialNumber = null);
}
