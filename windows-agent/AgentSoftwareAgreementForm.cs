using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public sealed class AgentSoftwareAgreementForm : Form
{
    private const string AgreementText = "软件使用协议\r\n\r\n本软件用于连接出租设备与租赁管理平台。软件会按平台要求发送设备状态、基础硬件信息和租期状态，用于设备管理、技术支持和履行租赁服务。\r\n\r\n您不得绕过设备授权、破坏客户端、反向工程或将设备用于违法用途。租赁开始后，必须同意本协议才能继续使用设备。\r\n\r\n协议更新后，客户端可能要求重新确认。";
    private static readonly string AcceptancePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RentDeviceAgent", "software-agreement.accepted");
    private readonly CheckBox _agree = new() { Text = "我已阅读并同意软件使用协议", AutoSize = true, ForeColor = Color.White, Location = new Point(28, 430) };

    public static bool IsAccepted(string rentalKey = "current")
    {
        try
        {
            if (!File.Exists(AcceptancePath)) return false;
            var protectedBytes = Convert.FromBase64String(File.ReadAllText(AcceptancePath));
            var value = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(value) == $"1.0:{rentalKey}";
        }
        catch { return false; }
    }

    public AgentSoftwareAgreementForm()
    {
        Text = "PC Rental Device Agent · 软件使用协议";
        Width = 720; Height = 560; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        TopMost = true; BackColor = Color.FromArgb(16, 24, 32); ForeColor = Color.White; AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Microsoft YaHei UI", 10);
        var title = new Label { Text = "软件使用协议", Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold), AutoSize = true, Location = new Point(28, 24), ForeColor = Color.FromArgb(113, 224, 181) };
        var notice = new Label { Text = "租赁已开始。请阅读并确认后继续使用此设备。", AutoSize = true, Location = new Point(30, 70), ForeColor = Color.LightGray };
        var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = AgreementText, Location = new Point(28, 105), Size = new Size(645, 300), BackColor = Color.FromArgb(32, 45, 56), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        var accept = new Button { Text = "同意并继续", Enabled = false, DialogResult = DialogResult.OK, Location = new Point(470, 470), Size = new Size(120, 34), BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var reject = new Button { Text = "拒绝并注销 Windows", DialogResult = DialogResult.Cancel, Location = new Point(290, 470), Size = new Size(165, 34), BackColor = Color.FromArgb(127, 29, 29), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _agree.CheckedChanged += (_, _) => accept.Enabled = _agree.Checked;
        Controls.AddRange([title, notice, text, _agree, reject, accept]); AcceptButton = accept; CancelButton = reject;
        FormClosing += (_, e) => { if (DialogResult != DialogResult.OK) { e.Cancel = false; } };
    }

    public static void ShowIfRequired(string rentalKey = "current", IWin32Window? owner = null)
    {
        if (IsAccepted(rentalKey)) return;
        using var form = new AgentSoftwareAgreementForm();
        if (form.ShowDialog(owner) == DialogResult.OK)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AcceptancePath)!);
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes($"1.0:{rentalKey}"), null, DataProtectionScope.CurrentUser);
            File.WriteAllText(AcceptancePath, Convert.ToBase64String(encrypted));
            return;
        }
        ExitWindowsEx(0, 0);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool ExitWindowsEx(uint flags, uint reason);
}
