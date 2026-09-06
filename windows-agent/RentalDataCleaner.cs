using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

/// <summary>
/// 租约结束 / 设备归还时，清除承租人留在设备上的个人痕迹：
/// 浏览器记录（Chrome / Edge / Firefox / 360 / QQ 等）、微信聊天记录、回收站、最近访问等。
/// 只处理承租人本地账户的用户目录，永不触碰 <c>Admin</c> / <c>Administrator</c> / 系统账户，
/// 与 <see cref="AgentWorker"/> 里 net.exe 账户指令保持同样的安全边界。
/// 采集能力上属于“数据最小化 / 隐私清理”，不是监控——不违反需求文档的“非目标”。
/// </summary>
public sealed record CleanupSpec(bool Browsers, bool WeChat, bool RecycleBin, bool RemoveProfile, IReadOnlyList<string> ExtraPaths)
{
    public static CleanupSpec Everything { get; } = new(true, true, true, false, Array.Empty<string>());
}

public sealed class CleanupReport
{
    public int Removed;
    public int Skipped;
    public int Failed;
    public List<string> TargetUsers { get; } = new();

    public string Summary => TargetUsers.Count == 0
        ? "未找到需要清理的承租人账户"
        : $"已清理账户 {string.Join("、", TargetUsers)}：删除 {Removed} 项、跳过 {Skipped} 项、失败 {Failed} 项";
}

public static class RentalDataCleaner
{
    private static readonly string[] ProtectedProfiles =
    {
        "public", "default", "default user", "defaultaccount", "all users",
        "admin", "administrator", "wdagutilityaccount", "systemprofile",
        "localservice", "networkservice", "contactadmin"
    };

    // 关闭这些进程后再删除，避免文件句柄占用导致删除失败。
    private static readonly string[] KillProcesses =
    {
        "chrome", "msedge", "firefox", "brave", "iexplore", "opera", "vivaldi", "chromium",
        "360se", "360chrome", "qqbrowser", "sogouexplorer", "liebao", "maxthon", "twinkstar",
        "WeChat", "Weixin", "WeChatApp", "WeChatAppEx", "WeChatOCR", "WeChatPlayer", "WechatBrowser"
    };

    // 相对承租人用户目录（%USERPROFILE%）的浏览器数据路径。整目录删除即可清掉历史 / Cookie / 密码 / 缓存 / 自动填充。
    private static readonly string[] BrowserPaths =
    {
        @"AppData\Local\Google\Chrome\User Data",
        @"AppData\Local\Google\Chrome Beta\User Data",
        @"AppData\Local\Google\Chrome SxS\User Data",
        @"AppData\Local\Microsoft\Edge\User Data",
        @"AppData\Local\Microsoft\Edge Beta\User Data",
        @"AppData\Local\Chromium\User Data",
        @"AppData\Local\BraveSoftware\Brave-Browser\User Data",
        @"AppData\Local\Vivaldi\User Data",
        @"AppData\Local\Opera Software",
        @"AppData\Roaming\Opera Software",
        @"AppData\Local\360Chrome\Chrome\User Data",
        @"AppData\Local\360 se6\User Data",
        @"AppData\Roaming\360se6\User Data",
        @"AppData\Local\Tencent\QQBrowser",
        @"AppData\Local\Sogou\SogouExplorer",
        @"AppData\Roaming\Mozilla\Firefox\Profiles",
        @"AppData\Local\Mozilla\Firefox\Profiles",
        @"AppData\Roaming\Microsoft\Windows\Recent",
        @"AppData\Local\Microsoft\Windows\WebCache",
        @"AppData\Local\Microsoft\Windows\INetCache",
        @"AppData\Local\Microsoft\Windows\History",
    };

    // 相对承租人用户目录的微信数据路径，覆盖经典版与 4.x（xwechat）。
    private static readonly string[] WeChatPaths =
    {
        @"AppData\Roaming\Tencent\WeChat",
        @"AppData\Roaming\Tencent\xwechat",
        @"AppData\Roaming\Tencent\WeChatData",
        @"AppData\Local\Tencent\WeChat",
        @"AppData\Local\Tencent\xwechat",
        @"Documents\WeChat Files",
        @"Documents\xwechat_files",
    };

    /// <summary>
    /// 清理承租人数据。<paramref name="username"/> 为空时清理所有非管理员的本地用户目录（整机归还场景）。
    /// </summary>
    public static async Task<CleanupReport> RunAsync(string? username, CleanupSpec spec, Action<string> log)
    {
        var report = new CleanupReport();
        if (!OperatingSystem.IsWindows()) { log("数据清理仅支持 Windows"); return report; }

        var usersRoot = UsersRoot();
        var targets = ResolveProfiles(usersRoot, username, log);
        if (targets.Count == 0) { log("数据清理：未找到匹配的承租人用户目录"); return report; }

        KillNoisyProcesses(log);
        await Task.Delay(1500); // 给句柄一点释放时间

        foreach (var profile in targets)
        {
            report.TargetUsers.Add(Path.GetFileName(profile));
            var paths = new List<string>();
            if (spec.Browsers) paths.AddRange(BrowserPaths.Select(p => Path.Combine(profile, p)));
            if (spec.WeChat) paths.AddRange(WeChatPaths.Select(p => Path.Combine(profile, p)));
            foreach (var extra in spec.ExtraPaths ?? Array.Empty<string>())
                paths.Add(Path.IsPathRooted(extra) ? extra : Path.Combine(profile, extra));

            foreach (var path in paths) Wipe(path, report, log);

            if (spec.RemoveProfile) RemoveUserProfile(Path.GetFileName(profile), log);
        }

        if (spec.RecycleBin) ClearRecycleBin(report, log);
        log($"数据清理结束：{report.Summary}");
        return report;
    }

    /// <summary>删除承租人用户目录并清理对应的 ProfileList 注册表项（账户已被 net user /delete 移除后调用）。</summary>
    public static void RemoveUserProfile(string username, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) return;
        var clean = SafeName(username);
        if (clean.Length == 0 || clean.StartsWith("admin", StringComparison.OrdinalIgnoreCase)) return;

        foreach (var dir in Directory.GetDirectories(UsersRoot())
            .Where(d => Path.GetFileName(d).Equals(clean, StringComparison.OrdinalIgnoreCase)
                     || Path.GetFileName(d).StartsWith(clean + ".", StringComparison.OrdinalIgnoreCase)))
        {
            try { DeleteDirectory(dir); log($"已删除用户目录 {dir}"); }
            catch (Exception ex) { log($"删除用户目录失败 {dir}：{ex.Message}"); TryCmdDelete(dir); }
        }

        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", writable: true);
            foreach (var sid in list?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var sub = list!.OpenSubKey(sid);
                if (sub?.GetValue("ProfileImagePath") is string img
                    && Path.GetFileName(img).Equals(clean, StringComparison.OrdinalIgnoreCase))
                {
                    list.DeleteSubKeyTree(sid, throwOnMissingSubKey: false);
                    log($"已清理用户配置注册表项 {sid}");
                }
            }
        }
        catch (Exception ex) { log($"清理用户配置注册表失败：{ex.Message}"); }
    }

    private static List<string> ResolveProfiles(string usersRoot, string? username, Action<string> log)
    {
        var result = new List<string>();
        if (!Directory.Exists(usersRoot)) return result;

        string? self = null;
        try { self = WindowsIdentity.GetCurrent().Name.Split('\\').Last().ToLowerInvariant(); } catch { }

        var wanted = string.IsNullOrWhiteSpace(username) ? null : SafeName(username!);
        foreach (var dir in Directory.GetDirectories(usersRoot))
        {
            var name = Path.GetFileName(dir);
            var lower = name.ToLowerInvariant();
            if (wanted is not null &&
                !(lower == wanted || lower.StartsWith(wanted + ".", StringComparison.Ordinal))) continue;
            if (ProtectedProfiles.Contains(lower)) continue;
            if (lower.StartsWith("admin")) { log($"数据清理：跳过受保护账户 {name}"); continue; }
            if (self is not null && lower == self) continue;
            result.Add(dir);
        }
        return result;
    }

    private static void Wipe(string path, CleanupReport report, Action<string> log)
    {
        try
        {
            if (Directory.Exists(path)) { DeleteDirectory(path); report.Removed++; log($"已删除目录 {path}"); }
            else if (File.Exists(path)) { ClearAttributes(path); File.Delete(path); report.Removed++; log($"已删除文件 {path}"); }
            else report.Skipped++;
        }
        catch (Exception ex)
        {
            report.Failed++;
            log($"删除失败 {path}：{ex.Message}");
            TryCmdDelete(path);
        }
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) ClearAttributes(file);
        Directory.Delete(path, recursive: true);
    }

    private static void ClearAttributes(string file)
    {
        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
    }

    private static void KillNoisyProcesses(Action<string> log)
    {
        foreach (var name in KillProcesses)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("/F"); psi.ArgumentList.Add("/T");
                psi.ArgumentList.Add("/IM"); psi.ArgumentList.Add(name + ".exe");
                Process.Start(psi)?.WaitForExit(5000);
            }
            catch { }
        }
        log("数据清理：已尝试关闭浏览器与微信进程");
    }

    private static void ClearRecycleBin(CleanupReport report, Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("Clear-RecycleBin -Force -ErrorAction SilentlyContinue");
            Process.Start(psi)?.WaitForExit(20000);
            report.Removed++;
            log("已清空回收站");
        }
        catch (Exception ex) { report.Failed++; log($"清空回收站失败：{ex.Message}"); }
    }

    private static void TryCmdDelete(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(Directory.Exists(path) ? "rd" : "del");
            if (Directory.Exists(path)) { psi.ArgumentList.Add("/s"); psi.ArgumentList.Add("/q"); }
            else { psi.ArgumentList.Add("/f"); psi.ArgumentList.Add("/q"); }
            psi.ArgumentList.Add(path);
            Process.Start(psi)?.WaitForExit(15000);
        }
        catch { }
    }

    private static string UsersRoot() =>
        Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))!, "Users");

    private static string SafeName(string value) =>
        new(value.Trim().Where(char.IsLetterOrDigit).ToArray());
}
