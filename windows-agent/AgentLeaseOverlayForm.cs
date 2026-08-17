using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

public sealed class AgentLeaseOverlayForm : Form
{
    private readonly Label _lease = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold) };
    private readonly NotifyIcon _tray = new() { Icon = SystemIcons.Application, Visible = true, Text = "PC Rental 设备管理" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private ToolStripMenuItem? _toggleOverlay;
    private ToolStripMenuItem? _bindMenuItem;
    private Button? _bindButton;
    private AgentDashboardForm? _dashboard;
    private DateTime? _endDate;
    private DateTime? _serverDate;
    private string _lastReminder = "";
    private string _lastMessage = "";
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
        var bind = new Button { Text = "手动绑定", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(35, 58, 78), Location = new Point(18, 68) };
        _bindButton = bind;
        bind.Click += (_, _) =>
        {
            using var form = new AgentBindingForm();
            form.ShowDialog(this);
            // Binding restarts the service. Read the new dashboard immediately instead of
            // waiting for the 15-second overlay timer.
            RefreshSnapshot();
            if (_bindButton is not null) _bindButton.Visible = !IsBound();
        };
        Controls.AddRange([title, _lease, bind]);
        Click += (_, _) => ShowMainPanel();
        foreach (Control control in Controls)
        {
            if (control != bind) control.Click += (_, _) => ShowMainPanel();
        }
        var windowMenu = new ContextMenuStrip();
        windowMenu.Items.Add("隐藏租期窗口", null, (_, _) => Hide());
        windowMenu.Items.Add("打开完整界面", null, (_, _) => ShowMainPanel());
        ContextMenuStrip = windowMenu;
        foreach (Control control in Controls) control.ContextMenuStrip = windowMenu;
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _toggleOverlay = new ToolStripMenuItem("隐藏租期窗口");
        _toggleOverlay.Click += (_, _) => ToggleOverlay();
        _tray.ContextMenuStrip.Items.Add(_toggleOverlay);
        _tray.ContextMenuStrip.Items.Add("打开完整界面", null, (_, _) => ShowMainPanel());
        _bindMenuItem = new ToolStripMenuItem("手动绑定设备");
        _bindMenuItem.Click += (_, _) => bind.PerformClick();
        _tray.ContextMenuStrip.Items.Add(_bindMenuItem);
        _tray.DoubleClick += (_, _) => { Show(); Activate(); };
        FormClosing += (_, e) => e.Cancel = IsBound();
        _timer.Tick += (_, _) => RefreshSnapshot();
        EnsureUserStartupEntry();
        _timer.Start();
        RefreshSnapshot();
    }

    private void ToggleOverlay()
    {
        if (Visible) Hide(); else { Show(); Activate(); }
        if (_toggleOverlay is not null) _toggleOverlay.Text = Visible ? "隐藏租期窗口" : "显示租期窗口";
    }

    private void ShowMainPanel()
    {
        if (_dashboard is not null && !_dashboard.IsDisposed)
        {
            _dashboard.Show();
            _dashboard.WindowState = FormWindowState.Normal;
            _dashboard.Activate();
            return;
        }
        _dashboard = new AgentDashboardForm();
        _dashboard.FormClosed += (_, _) => _dashboard = null;
        _dashboard.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                // Closing the main window also hides the floating lease window;
                // the tray icon and background service remain available.
                Hide();
            }
        };
        _dashboard.Show(this);
    }

    private void RefreshSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) { _endDate = null; _lease.Text = "租期：等待设备连接"; _tray.Text = "PC Rental 设备管理 · 等待设备连接"; return; }
            var data = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
            if (data is null) return;
            if (string.Equals(data.BindingStatus, "unbound", StringComparison.OrdinalIgnoreCase))
            {
                _endDate = null;
                _lease.Text = "设备未绑定";
                _tray.Text = "PC Rental 设备管理 · 设备未绑定";
                if (_bindButton is not null) _bindButton.Visible = true;
                if (_bindMenuItem is not null) _bindMenuItem.Visible = true;
                return;
            }
            if (_bindButton is not null) _bindButton.Visible = false;
            if (_bindMenuItem is not null) _bindMenuItem.Visible = false;
            var rentalKey = data.RentalId ?? data.StartDate ?? "current";
            if (data.ProtocolRequired && !AgentSoftwareAgreementForm.IsAccepted(rentalKey))
            {
                Hide();
                AgentSoftwareAgreementForm.ShowIfRequired(data.ApiBaseUrl, rentalKey, this);
                return;
            }
            _endDate = DateTime.TryParse(data.EndDate, out var endDate) ? endDate.Date : null;
            _serverDate = DateTimeOffset.TryParse(data.ServerTime, out var serverTime) ? serverTime.UtcDateTime.Date : null;
            if (!string.IsNullOrWhiteSpace(data.MessageBody))
            {
                var messageKey = $"{data.MessageTitle}|{data.MessageBody}";
                if (_lastMessage != messageKey)
                {
                    _lastMessage = messageKey;
                    _tray.ShowBalloonTip(8000, data.MessageTitle ?? "租赁通知", data.MessageBody, ToolTipIcon.Info);
                }
            }
            ShowExpiry();
            if (string.Equals(data.DeviceMode, "maintenance", StringComparison.OrdinalIgnoreCase))
                _lease.Text = "设备暂时暂停使用\r\n请联系管理员或客服";
        }
        catch { _lease.Text = "租期：暂时无法读取"; }
    }

    private static void EnsureUserStartupEntry()
    {
        try
        {
            var startupShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "PC Rental 设备管理.lnk");
            File.Delete(startupShortcut);
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith("RentDeviceAgent.exe", StringComparison.OrdinalIgnoreCase)) return;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
                ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            key?.DeleteValue("PC Rental Device Agent UI", false);
            key?.SetValue("PC Rental Device Agent UI", $"\"{executable}\" --ui");
        }
        catch { }
    }

    private static bool IsBound()
    {
        var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "state.json");
        var unboundPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RentDeviceAgent", "unbound.flag");
        return File.Exists(statePath) && !File.Exists(unboundPath);
    }

    private void ShowExpiry()
    {
        if (_endDate is null)
        {
            _lease.Text = "剩余时间：暂无租期\r\n到期时间：暂无租期";
            _tray.Text = "PC Rental 设备管理 · 到期：暂无租期";
            return;
        }
        var today = _serverDate ?? DateTime.Now.Date;
        var days = (_endDate.Value - today).Days;
        var remaining = _endDate.Value < today ? "已到期" : $"{days} 天";
        var reminder = days <= 0 ? "expired" : days <= 1 ? "24h" : days <= 3 ? "72h" : "";
        if (!string.IsNullOrEmpty(reminder) && _lastReminder != reminder)
        {
            _lastReminder = reminder;
            var text = reminder == "expired" ? "租赁期限已结束，请联系管理员安排归还。" : $"租赁将在 {(_endDate.Value - today).Days} 天内到期，请提前准备归还。";
            _tray.ShowBalloonTip(8000, "租期提醒", text, reminder == "expired" ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        _lease.Text = $"剩余时间：{remaining}\r\n到期时间：{_endDate:yyyy-MM-dd}";
        _tray.Text = $"PC Rental 设备管理 · 剩余 {remaining} · 到期 {_endDate:yyyy-MM-dd}";
    }

    private sealed record Snapshot(string Status, string DeviceMode, string? StartDate, string? EndDate, string? RentalId, string? ServerTime, bool ProtocolRequired, double MemoryGb, double StorageGb, string Version, DateTime UpdatedAt, string? ApiBaseUrl, string? BindingStatus, string? MessageTitle = null, string? MessageBody = null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_dashboard is not null && !_dashboard.IsDisposed) _dashboard.Close();
            _timer.Dispose(); _tray.Visible = false; _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
