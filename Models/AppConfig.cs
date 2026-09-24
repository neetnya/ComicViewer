using System.Text.Json.Serialization;

namespace ComicViewer.Models;

/// <summary>
/// 查看模式枚举。
/// </summary>
public enum ViewMode
{
    /// <summary>漫画模式：下拉式连续查看。</summary>
    Comic = 0,

    /// <summary>图片模式：单张查看。</summary>
    Image = 1,
}

/// <summary>
/// 应用配置（序列化为 JSON 存于 exe 同目录）。
/// </summary>
public class AppConfig
{
    /// <summary>当前查看模式，持久化记忆。</summary>
    public ViewMode Mode { get; set; } = ViewMode.Comic;

    /// <summary>
    /// 漫画模式缩放百分比：图片显示宽度 = 窗口宽度 × 该值。
    /// 例如 0.5（50%）即图片宽度为窗口宽度的一半，1.0（100%）铺满窗口宽度。
    /// </summary>
    public double ComicWidthRatio { get; set; } = 0.75;

    /// <summary>是否全屏启动。</summary>
    public bool StartFullscreen { get; set; } = true;

    /// <summary>漫画模式滚轮滚动步长（像素）。</summary>
    public double ScrollStepPixels { get; set; } = 300;

    /// <summary>支持的图片扩展名（小写，含点）。</summary>
    [JsonIgnore]
    public static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".wmf", ".emf"];
}
