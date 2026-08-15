using System.Text.Json;
using System.Diagnostics;

public sealed class AgentDashboardForm : Form
{
    private const string DefaultApiBaseUrl = "https://rent.ydnw6zt6vj.workers.dev";
    private static readonly Color Navy = Color.FromArgb(13, 27, 42);
    private static readonly Color Panel = Color.FromArgb(20, 39, 56);
    private static readonly Color Blue = Color.FromArgb(59, 130, 246);
    private static readonly Color Mint = Color.FromArgb(91, 214, 173);
    private static readonly Color Muted = Color.FromArgb(166, 184, 198);
    private readonly Label _status = ValueLabel("正在读取客户端状态");
    private readonly Label _deviceId = ValueLabel("未绑定");
    private readonly Label _rental = ValueLabel("—");
    private readonly Label _hardware = ValueLabel("—");
    private readonly Label _version = ValueLabel("—");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly NotifyIcon _notify = new() { Icon = SystemIcons.Application, Visible = false, Text = "PC Rental 设备管理" };
    private string _customerPanelUrl = $"{DefaultApiBaseUrl}/login";
    private bool _expiryNotified;
    private static readonly string SnapshotPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "dashboard.json");

    public AgentDashboardForm()
    {
        Text = "PC Rental 设备管理"; Width = 900; Height = 620; MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Navy; ForeColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Microsoft YaHei UI", 10); KeyPreview = true;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(32, 26, 32, 28), BackColor = Navy };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(Header(), 0, 0); root.Controls.Add(Content(), 0, 1); root.Controls.Add(Footer(), 0, 2); Controls.Add(root);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F1) OpenCustomerPanel(); if (e.KeyCode == Keys.F5) RequestRefresh(); e.Handled = true; };
        _notify.DoubleClick += (_, _) => { Show(); Activate(); }; _timer.Tick += (_, _) => RefreshSnapshot(); _timer.Start(); RefreshSnapshot();
    }

    private Control Header()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var eyebrow = LabelFor("PC RENTAL / DEVICE AGENT", 9, FontStyle.Bold); eyebrow.ForeColor = Mint; eyebrow.Margin = new Padding(0, 0, 0, 3);
        var title = LabelFor("设备控制台", 25, FontStyle.Bold); title.Margin = new Padding(0); layout.Controls.Add(eyebrow, 0, 0); layout.Controls.Add(title, 0, 1);
        var button = ActionButton("打开客户面板  [F1]", Blue); button.AutoSize = false; button.Dock = DockStyle.Fill; button.Margin = new Padding(12, 0, 0, 8); button.Click += (_, _) => OpenCustomerPanel(); layout.Controls.Add(button, 1, 1); return layout;
    }

    private Control Content()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 16, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.Controls.Add(Card("连接状态", _status, "显示客户端与平台的当前连接结果"), 0, 0); layout.Controls.Add(Card("设备 ID", _deviceId, "网站用于识别这台出租设备"), 1, 0); layout.Controls.Add(Card("当前租期", _rental, "显示租期开始和到期时间"), 0, 1); layout.Controls.Add(Card("设备资源", _hardware, "最近一次心跳上报的数据"), 1, 1); return layout;
    }

    private Control Card(string title, Label value, string hint)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 12, 12), Padding = new Padding(20) };
        var eyebrow = LabelFor(title.ToUpperInvariant(), 9, FontStyle.Bold); eyebrow.ForeColor = Muted; eyebrow.Dock = DockStyle.Top; eyebrow.Height = 22;
        value.AutoSize = false; value.Dock = DockStyle.Top; value.Height = 48; value.ForeColor = Color.White; value.Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold); value.AutoEllipsis = true; value.Padding = new Padding(0, 2, 0, 0);
        var note = LabelFor(hint, 9); note.AutoSize = false; note.Dock = DockStyle.Fill; note.ForeColor = Muted; note.Padding = new Padding(0, 5, 0, 0); panel.Controls.Add(note); panel.Controls.Add(value); panel.Controls.Add(eyebrow); return panel;
    }

    private Control Footer()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 4, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _version.AutoSize = false; _version.Dock = DockStyle.Fill; _version.ForeColor = Muted; _version.Font = new Font("Microsoft YaHei UI", 9); layout.Controls.Add(_version, 0, 0);
        var refresh = ActionButton("同步一次  [F5]", Color.FromArgb(35, 58, 78)); refresh.AutoSize = false; refresh.Dock = DockStyle.Fill; refresh.Margin = new Padding(8, 0, 0, 0); refresh.Click += (_, _) => RequestRefresh(); layout.Controls.Add(refresh, 1, 0); return layout;
    }

    private void RequestRefresh()
    {
        try
        {
            var directory = Path.GetDirectoryName(SnapshotPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "refresh-request"), DateTime.UtcNow.ToString("O"));
            _version.Text = "已请求立即同步，正在读取最新租期…";
        }
        catch (Exception ex) { _status.Text = $"无法请求同步：{ex.Message}"; }
    }

    private void RefreshSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath))
            {
                _status.Text = "等待连接网站…";
                _deviceId.Text = "等待绑定";
                _rental.Text = "等待同步租期";
                _hardware.Text = "等待设备信息";
                _version.Text = "客户端已启动 · 尚未收到网站数据";
                return;
            }

            var data = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
            if (data is null)
            {
                _status.Text = "网站数据为空";
                return;
            }

            _status.Text = string.IsNullOrWhiteSpace(data.Status) ? "已连接，等待状态" : data.Status;
            _deviceId.Text = data.DeviceId ?? "未绑定";
            _rental.Text = data.EndDate is null ? "暂无租期" : $"开始：{data.StartDate ?? "—"}  ·  到期：{data.EndDate}";
            var memoryGb = data.MemoryGb > 0 ? data.MemoryGb : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824d;
            var storageGb = data.StorageGb > 0 ? data.StorageGb : ReadStorageGb();
            _hardware.Text = $"内存 {memoryGb:0.0} GB  ·  剩余存储 {storageGb:0.0} GB";
            _version.Text = $"版本 {data.Version}  ·  最后同步 {data.UpdatedAt:yyyy-MM-dd HH:mm:ss}";
            if (!string.IsNullOrWhiteSpace(data.ApiBaseUrl)) _customerPanelUrl = $"{data.ApiBaseUrl.TrimEnd('/')}/login";
            if (!_expiryNotified && DateTime.TryParse(data.EndDate, out var endDate) && (endDate.Date - DateTime.Now.Date).TotalDays <= 3 && endDate.Date >= DateTime.Now.Date) { _notify.ShowBalloonTip(8000, "租期即将到期", $"设备租期将在 {endDate:yyyy-MM-dd} 到期。", ToolTipIcon.Warning); _expiryNotified = true; }
        }
        catch (Exception ex)
        {
            _status.Text = $"读取网站状态失败：{ex.Message}";
            _deviceId.Text = "无法读取设备 ID";
            _rental.Text = "无法读取租期";
            _hardware.Text = "无法读取设备资源";
        }
    }

    private void OpenCustomerPanel()
    {
        try { Process.Start(new ProcessStartInfo(_customerPanelUrl) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"无法打开客户面板：{ex.Message}", "PC Rental", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private static double ReadStorageGb()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace / 1073741824d; }
        catch { return 0; }
    }
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _notify.Visible = false; _notify.Dispose(); } base.Dispose(disposing); }
    private static Label LabelFor(string text, float size = 15, FontStyle style = FontStyle.Regular) => new() { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = Color.White };
    private static Label ValueLabel(string text) => LabelFor(text, 14, FontStyle.Bold);
    private static Button ActionButton(string text, Color background) => new() { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, BackColor = background, ForeColor = Color.White, Padding = new Padding(14, 0, 14, 0), Cursor = Cursors.Hand };
    private sealed record Snapshot(string Status, string DeviceMode, string? StartDate, string? EndDate, bool ProtocolRequired, double MemoryGb, double StorageGb, string Version, DateTime UpdatedAt, string? ApiBaseUrl, string? DeviceId = null, string? RegisteredSerialNumber = null, string? DetectedSerialNumber = null);
}
