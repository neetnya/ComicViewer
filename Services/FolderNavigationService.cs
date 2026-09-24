using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ComicViewer.Models;

namespace ComicViewer.Services;

/// <summary>
/// 文件夹层级导航服务：负责列出同级/子文件夹、切换当前查看目录、
/// 在末页/末张点击左键时进入下一个同级文件夹（需求第 7 条）。
/// </summary>
public static class FolderNavigationService
{
    private static readonly HashSet<string> ImageExtSet =
        new(AppConfig.ImageExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>判断文件是否为支持的图片（按扩展名）。</summary>
    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtSet.Contains(ext);
    }

    /// <summary>
    /// 列出指定文件夹下所有图片（不递归子文件夹），按名称自然排序。
    /// </summary>
    public static List<string> ListImages(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => !IsHidden(f))
                .Where(IsImageFile)
                .OrderByNaturalPath()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>判断路径是否为隐藏（含系统）。</summary>
    public static bool IsHidden(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return (attr & FileAttributes.Hidden) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取所有可用磁盘根目录（如 C:\ D:\），用于未打开文件夹时显示计算机目录。
    /// </summary>
    public static List<string> ListLogicalDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 列出指定文件夹下的子目录（不递归，非隐藏），按名称自然排序。
    /// 用于侧栏树形展开。
    /// </summary>
    public static List<string> ListSubFolders(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        try
        {
            return Directory.EnumerateDirectories(folder)
                .Where(d => !IsHidden(d))
                .OrderByNaturalPath()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 获取同级文件夹列表（用于末页跳转），按名称自然排序。
    /// </summary>
    public static List<string> ListSiblingFolders(string currentFolder)
    {
        var parent = Directory.GetParent(currentFolder);
        if (parent == null || !parent.Exists)
            return [];

        try
        {
            return parent.EnumerateDirectories()
                .Select(d => d.FullName)
                .Where(d => !IsHidden(d))
                .OrderByNaturalPath()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 获取下一个同级文件夹（末页自动跳转用）。
    /// 返回 null 表示没有下一个文件夹。
    /// </summary>
    public static string? GetNextSiblingFolder(string currentFolder)
    {
        var siblings = ListSiblingFolders(currentFolder);
        var idx = siblings.FindIndex(s =>
            string.Equals(s, currentFolder, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < siblings.Count)
            return siblings[idx + 1];
        return null;
    }

    /// <summary>获取上一个同级文件夹。</summary>
    public static string? GetPrevSiblingFolder(string currentFolder)
    {
        var siblings = ListSiblingFolders(currentFolder);
        var idx = siblings.FindIndex(s =>
            string.Equals(s, currentFolder, StringComparison.OrdinalIgnoreCase));
        if (idx > 0)
            return siblings[idx - 1];
        return null;
    }

    /// <summary>自然排序（Windows 资源管理器风格），正确处理数字。</summary>
    private static IEnumerable<string> OrderByNaturalPath(this IEnumerable<string> paths)
    {
        return paths.OrderBy(p => p, new NaturalStringComparer());
    }

    /// <summary>自然字符串比较器。</summary>
    private sealed class NaturalStringComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var nx = Path.GetFileName(x);
            var ny = Path.GetFileName(y);
            return StrCmpLogicalW(nx, ny);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);
    }
}
