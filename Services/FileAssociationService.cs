using System.Diagnostics;
using System.Runtime.InteropServices;
using ComicViewer.Models;
using Microsoft.Win32;

namespace ComicViewer.Services;

/// <summary>
/// 文件关联服务：将本软件注册到系统，使其出现在「设置 → 默认应用」列表中，
/// 并关联常见图片扩展名（按当前用户写入 HKCU，无需管理员权限）。
/// </summary>
public static class FileAssociationService
{
    private const string ProgId = "ComicViewer.Image";
    private const string AppName = "ComicViewer";
    private const string ExeName = "ComicViewer.exe";

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>复用 AppConfig 中的统一扩展名列表，避免两端不一致。</summary>
    private static string[] ImageExtensions => AppConfig.ImageExtensions;

    /// <summary>获取当前 exe 完整路径。</summary>
    private static string GetExePath()
    {
        try
        {
            var module = Process.GetCurrentProcess().MainModule;
            if (!string.IsNullOrEmpty(module?.FileName))
                return module.FileName;
        }
        catch
        {
            // 忽略，回退到进程路径
        }
        return Environment.ProcessPath ?? ExeName;
    }

    /// <summary>
    /// 注册 ProgId、应用程序条目与 Capabilities，使本软件出现在系统默认应用列表，
    /// 并通知 Shell 立即刷新文件关联缓存。
    /// </summary>
    public static void Register()
    {
        var exePath = GetExePath();

        // 1. 注册 ProgId（命令行为 "%1" 传入打开的文件）
        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progKey.SetValue(null, AppName);
            using (var iconKey = progKey.CreateSubKey("DefaultIcon"))
                iconKey.SetValue(null, $"{exePath},0");
            using (var cmdKey = progKey.CreateSubKey(@"shell\open\command"))
                cmdKey.SetValue(null, $"\"{exePath}\" \"%1\"");
        }

        // 2. 扩展名 → ProgId
        foreach (var ext in ImageExtensions)
        {
            using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}");
            extKey.SetValue(null, ProgId);
        }

        // 3. 注册「应用程序」条目（决定其是否出现在默认应用列表）
        using (var appKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ExeName}"))
        {
            appKey.SetValue("FriendlyAppName", AppName);
            appKey.SetValue("ApplicationIcon", $"{exePath},0");
            appKey.SetValue("NoStartPage", "");
            using var cmdKey = appKey.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue(null, $"\"{exePath}\" \"%1\"");
            using var typesKey = appKey.CreateSubKey("SupportedTypes");
            foreach (var ext in ImageExtensions)
                typesKey.SetValue(ext, "");
        }

        // 4. 注册 Capabilities（出现在「默认应用 → 按应用设置默认值」）
        using (var capsKey = Registry.CurrentUser.CreateSubKey(@"Software\ComicViewer\Capabilities"))
        {
            capsKey.SetValue("ApplicationName", AppName);
            capsKey.SetValue("ApplicationDescription", "本地漫画 / 图片查看器");
            using var assocKey = capsKey.CreateSubKey("FileAssociations");
            foreach (var ext in ImageExtensions)
                assocKey.SetValue(ext, ProgId);
        }
        using (var regAppsKey = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            regAppsKey.SetValue(AppName, @"Software\ComicViewer\Capabilities");
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 检查当前用户下是否已写入本软件的 ProgId 打开命令，
    /// 并区分「已注册到当前路径」与「便携程序移动后路径过期」。
    /// </summary>
    public static (bool IsRegistered, bool PathMatches) GetRegistrationState()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{ProgId}\shell\open\command");
            var val = cmd?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(val))
                return (false, false);

            var currentExe = GetExePath();
            var matches = val.Contains(currentExe, StringComparison.OrdinalIgnoreCase);
            return (true, matches);
        }
        catch
        {
            return (false, false);
        }
    }

    /// <summary>当前用户下是否已写入本软件的 ProgId 打开命令且路径匹配。</summary>
    public static bool IsRegistered()
    {
        var (registered, pathMatches) = GetRegistrationState();
        return registered && pathMatches;
    }

    /// <summary>
    /// 移除本软件写入的 ProgId / 应用程序条目 / Capabilities，并通知 Shell 刷新。
    /// </summary>
    public static void Unregister()
    {
        foreach (var ext in ImageExtensions)
        {
            try
            {
                using var extKey = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\{ext}", writable: true);
                if (extKey?.GetValue(null) as string == ProgId)
                    extKey.DeleteValue("", throwOnMissingValue: false);
            }
            catch
            {
                // 单个扩展名失败不阻断其余清理
            }
        }

        TryDeleteSubKeyTree($@"Software\Classes\{ProgId}");
        TryDeleteSubKeyTree($@"Software\Classes\Applications\{ExeName}");
        TryDeleteSubKeyTree(@"Software\ComicViewer");
        try
        {
            using var regApps = Registry.CurrentUser.OpenSubKey(
                @"Software\RegisteredApplications", writable: true);
            regApps?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch
        {
            // 静默：取消关联不应打断设置窗口
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private static void TryDeleteSubKeyTree(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch
        {
            // 键不存在或无权限时忽略
        }
    }

    /// <summary>
    /// 注册关联并打开系统「默认应用」设置页。若注册本身失败则抛出异常，不再静默吞掉。
    /// </summary>
    public static bool RegisterAndOpenSettings()
    {
        Register();

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
