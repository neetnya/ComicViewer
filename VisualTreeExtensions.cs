using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ComicViewer;

/// <summary>
/// WPF 可视化树辅助扩展方法。
/// </summary>
public static class VisualTreeExtensions
{
    /// <summary>
    /// 向上查找指定类型的祖先（包括自身）。
    /// 兼容 Visual、Visual3D 与 FrameworkContentElement（如 Run / Hyperlink），避免 GetParent 抛异常。
    /// </summary>
    public static T? FindAncestorOrSelf<T>(this DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t)
                return t;

            if (d is Visual or Visual3D)
                d = VisualTreeHelper.GetParent(d) ?? (d as FrameworkElement)?.Parent;
            else if (d is FrameworkContentElement fce)
                d = fce.Parent;
            else
                d = LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>
    /// 向下查找第一个指定类型的可视化后代（广度优先）。
    /// </summary>
    public static T? FindDescendant<T>(this DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is T match && !ReferenceEquals(current, root))
                return match;

            if (current is Visual or Visual3D)
            {
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++)
                    queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
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

            if (d is Visual or Visual3D)
                d = VisualTreeHelper.GetParent(d);
            else if (d is FrameworkContentElement fce)
                d = fce.Parent;
            else
                d = LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
