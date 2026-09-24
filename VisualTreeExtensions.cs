using System.Windows;
using System.Windows.Media;

namespace ComicViewer;

/// <summary>
/// WPF 可视化树辅助扩展方法。
/// </summary>
public static class VisualTreeExtensions
{
    /// <summary>
    /// 向上查找指定类型的祖先（包括自身）。
    /// </summary>
    public static T? FindAncestorOrSelf<T>(this DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t)
                return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d)
                ?? (d as System.Windows.FrameworkElement)?.Parent;
        }
        return null;
    }

    /// <summary>
    /// 判断 d 是否为 ancestor 的可视化/逻辑树后代（含自身）。
    /// 用于精确判断点击源是否位于侧栏、顶栏等覆盖层内部，
    /// 避免「最近 Border 祖先」误判（列表项本身也是 Border）。
    /// </summary>
    public static bool IsDescendantOf(this DependencyObject? d, DependencyObject ancestor)
    {
        while (d != null)
        {
            if (ReferenceEquals(d, ancestor))
                return true;

            if (d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            else if (d is System.Windows.FrameworkContentElement fce)
                d = fce.Parent;
            else
                d = System.Windows.LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
