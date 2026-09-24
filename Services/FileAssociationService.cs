using System.Diagnostics;

namespace ComicViewer.Services;

/// <summary>
/// 文件关联服务：引导用户将本软件设为系统默认图片查看器。
///
/// 说明：Windows 8+ 上"默认应用"受 UserChoice 保护，程序无法直接改注册表生效，
/// 因此这里只负责打开系统"默认应用"设置页，由用户手动确认。
/// </summary>
public static class FileAssociationService
{
    /// <summary>
    /// 打开系统「默认应用」设置页，引导用户选择 ComicViewer 作为默认图片查看器。
    /// 返回是否成功打开设置页。
    /// </summary>
    public static bool TrySetAsDefault()
    {
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
