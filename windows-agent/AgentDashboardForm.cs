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
    private readonly Label _version = ValueLabel("—");
    private readonly LinkLabel _updateLink = new() { Text = "", AutoSize = true, LinkColor = Color.FromArgb(113, 224, 181), ActiveLinkColor = Color.White, VisitedLinkColor = Color.FromArgb(113, 224, 181), Cursor = Cursors.Hand };
    private readonly Label _sync = ValueLabel("—");
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
        var title = LabelFor("我的租赁设备", 25, FontStyle.Bold); title.Margin = new Padding(0); layout.Controls.Add(eyebrow, 0, 0); layout.Controls.Add(title, 0, 1);
        var button = ActionButton("打开客户面板  [F1]", Blue); button.AutoSize = false; button.Dock = DockStyle.Fill; button.Margin = new Padding(12, 0, 0, 8); button.Click += (_, _) => OpenCustomerPanel(); layout.Controls.Add(button, 1, 1); return layout;
    }

    private Control Content()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 16, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 2; row++) layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.Controls.Add(Card("服务状态", _status), 0, 0); layout.Controls.Add(Card("租赁设备", _deviceId), 1, 0); layout.Controls.Add(Card("我的租期", _rental), 0, 1); layout.Controls.Add(Card("客户端信息", _sync), 1, 1); return layout;
    }

    private Control Card(string title, Label value)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Panel, Margin = new Padding(0, 0, 12, 12), Padding = new Padding(20) };
        var eyebrow = LabelFor(title.ToUpperInvariant(), 9, FontStyle.Bold); eyebrow.ForeColor = Muted; eyebrow.Dock = DockStyle.Top; eyebrow.Height = 22;
        value.AutoSize = false; value.Dock = DockStyle.Fill; value.ForeColor = Color.White; value.Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold); value.AutoEllipsis = true; value.Padding = new Padding(0, 2, 0, 0);
        panel.Controls.Add(value); panel.Controls.Add(eyebrow); return panel;
    }

    private Control Footer()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 4, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnCount = 3;
        _version.AutoSize = false; _version.Dock = DockStyle.Fill; _version.ForeColor = Muted; _version.Font = new Font("Microsoft YaHei UI", 9); layout.Controls.Add(_version, 0, 0);
        _updateLink.Anchor = AnchorStyles.Left; _updateLink.Visible = false; _updateLink.LinkClicked += (_, _) => OpenLatestVersion(); layout.Controls.Add(_updateLink, 1, 0);
        var refresh = ActionButton("同步一次  [F5]", Color.FromArgb(35, 58, 78)); refresh.AutoSize = false; refresh.Dock = DockStyle.Fill; refresh.Margin = new Padding(8, 0, 0, 0); refresh.Click += (_, _) => RequestRefresh(); layout.Controls.Add(refresh, 2, 0); return layout;
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
                _version.Text = "客户端已启动 · 尚未收到网站数据";
                _sync.Text = _version.Text;
                return;
            }

            var data = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
            if (data is null)
            {
                _status.Text = "网站数据为空";
                return;
            }

            _status.Text = string.IsNullOrWhiteSpace(data.Status) ? "已连接，等待状态" : data.Status;
            _status.ForeColor = data.Status?.Contains("失败", StringComparison.OrdinalIgnoreCase) == true || data.Status?.Contains("无法", StringComparison.OrdinalIgnoreCase) == true ? Color.FromArgb(248, 113, 113) : Mint;
            _deviceId.Text = string.IsNullOrWhiteSpace(data.DeviceId) ? "未绑定" : "已绑定";
            _rental.Text = data.EndDate is null ? "暂无租期" : $"开始：{data.StartDate ?? "—"}  ·  到期：{data.EndDate}";
            _version.Text = $"版本 {data.Version}  ·  最后同步 {data.UpdatedAt:yyyy-MM-dd HH:mm:ss}";
            _sync.Text = _version.Text;
            _updateLink.Visible = !string.IsNullOrWhiteSpace(data.LatestVersion) && !string.IsNullOrWhiteSpace(data.UpdateDownloadUrl);
            _updateLink.Text = _updateLink.Visible ? $"下载最新版 {data.LatestVersion}" : "";
            _updateLink.Tag = data.UpdateDownloadUrl;
            if (!string.IsNullOrWhiteSpace(data.ApiBaseUrl)) _customerPanelUrl = $"{data.ApiBaseUrl.TrimEnd('/')}/login";
            if (!_expiryNotified && DateTime.TryParse(data.EndDate, out var endDate) && (endDate.Date - DateTime.Now.Date).TotalDays <= 3 && endDate.Date >= DateTime.Now.Date) { _notify.ShowBalloonTip(8000, "租期即将到期", $"设备租期将在 {endDate:yyyy-MM-dd} 到期。", ToolTipIcon.Warning); _expiryNotified = true; }
        }
        catch (Exception ex)
        {
            _status.Text = $"读取网站状态失败：{ex.Message}";
            _deviceId.Text = "无法读取设备 ID";
            _rental.Text = "无法读取租期";
            _status.ForeColor = Color.FromArgb(248, 113, 113);
        }
    }

    private void OpenCustomerPanel()
    {
        try { Process.Start(new ProcessStartInfo(_customerPanelUrl) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"无法打开客户面板：{ex.Message}", "PC Rental", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void OpenLatestVersion()
    {
        if (_updateLink.Tag is not string url || string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"无法打开下载页面：{ex.Message}", "PC Rental", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _notify.Visible = false; _notify.Dispose(); } base.Dispose(disposing); }
    private static Label LabelFor(string text, float size = 15, FontStyle style = FontStyle.Regular) => new() { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = Color.White };
    private static Label ValueLabel(string text) => LabelFor(text, 14, FontStyle.Bold);
    private static Button ActionButton(string text, Color background) => new() { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, BackColor = background, ForeColor = Color.White, Padding = new Padding(14, 0, 14, 0), Cursor = Cursors.Hand };
    private sealed record Snapshot(string Status, string DeviceMode, string? StartDate, string? EndDate, bool ProtocolRequired, double MemoryGb, double StorageGb, string Version, DateTime UpdatedAt, string? ApiBaseUrl, string? DeviceId = null, string? RegisteredSerialNumber = null, string? DetectedSerialNumber = null, string? LatestVersion = null, string? UpdateDownloadUrl = null);
}
