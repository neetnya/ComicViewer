using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ComicViewer.Services;

/// <summary>
/// 文件关联服务：将本软件注册到系统，使其出现在「设置 → 默认应用」列表中，
/// 并关联常见图片扩展名。
///
/// 说明：Windows 8+ 上真正"设为默认"受 UserChoice 保护，程序无法直接改写，
/// 但正确注册 ProgId / Application / Capabilities 后，本软件会出现在系统
/// 默认应用候选列表里，用户即可手动选择。
/// </summary>
public static class FileAssociationService
{
    private const string ProgId = "ComicViewer.Image";
    private const string AppName = "ComicViewer";
    private const string ExeName = "ComicViewer.exe";

    /// <summary>支持的图片扩展名（小写，含点）。</summary>
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico"];

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
    /// 并关联支持的图片扩展名（按当前用户写入 HKCU，无需管理员权限）。
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
        using (var capsKey = Registry.CurrentUser.CreateSubKey(
            $@"Software\ComicViewer\Capabilities"))
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
    }

    /// <summary>
    /// 注册关联并打开系统「默认应用」设置页，引导用户手动确认。
    /// </summary>
    public static bool RegisterAndOpenSettings()
    {
        try
        {
            Register();
        }
        catch
        {
            // 注册失败不阻断打开设置页
        }

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
