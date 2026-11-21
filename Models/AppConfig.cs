using System.Text.Json.Serialization;
using System.Windows.Media;

namespace ComicViewer.Models;

/// <summary>
/// 查看模式枚举。
/// </summary>
public enum ViewMode
{
    /// <summary>漫画模式：下拉式连续查看。</summary>
    Comic = 0,

    /// <summary>图片模式：单张/双页查看。</summary>
    Image = 1,
}

/// <summary>
/// 图片模式自适应方式。
/// </summary>
public enum ImageFitMode
{
    /// <summary>完整适应窗口。</summary>
    FitWindow = 0,

    /// <summary>适应窗口宽度。</summary>
    FitWidth = 1,

    /// <summary>1:1 原始像素大小。</summary>
    ActualSize = 2,
}

/// <summary>
/// 应用配置（序列化为 JSON 存于 exe 同目录；只读目录时自动回退 LocalAppData）。
/// </summary>
public class AppConfig
{
    /// <summary>当前查看模式，持久化记忆。</summary>
    public ViewMode Mode { get; set; } = ViewMode.Comic;

    /// <summary>
    /// 漫画模式缩放百分比：图片显示宽度 = 阅读区宽度 × 该值。
    /// 例如 0.5（50%）即图片宽度为阅读区宽度的一半，1.0（100%）铺满阅读区宽度。
    /// </summary>
    public double ComicWidthRatio { get; set; } = 0.75;

    /// <summary>是否全屏启动。</summary>
    public bool StartFullscreen { get; set; } = true;

    /// <summary>漫画模式滚轮滚动步长（像素）。</summary>
    public double ScrollStepPixels { get; set; } = 300;

    /// <summary>漫画模式向上预加载页数（当前页之前保留/预解码的页数）。</summary>
    public int PreloadUpCount { get; set; } = 2;

    /// <summary>漫画模式向下预加载页数（当前页之后预解码的页数）。</summary>
    public int PreloadDownCount { get; set; } = 5;

    /// <summary>漫画模式相邻页面垂直间距（像素，0~40）。</summary>
    public double ComicPageGap { get; set; } = 4;

    /// <summary>漫画模式自动滚动速度（像素/秒，20~3000）。</summary>
    public double AutoScrollSpeed { get; set; } = 180;

    /// <summary>阅读区背景色（十六进制，如 #1a1a1a）。</summary>
    public string BackgroundColor { get; set; } = "#1a1a1a";

    /// <summary>
    /// 通过文件关联或拖放打开单张图片时，是否丢弃该图之前的文件。
    /// 默认 false：保留完整列表并直接定位到目标图，支持往回翻页。
    /// </summary>
    public bool TruncateBeforeStartFile { get; set; } = false;

    /// <summary>图片模式删除图片前是否弹出确认框（回收站失败降级永久删除时始终会询问）。</summary>
    public bool ConfirmBeforeDelete { get; set; } = false;

    /// <summary>图片模式：是否启用双页并排查看。</summary>
    public bool DualPageMode { get; set; } = false;

    /// <summary>图片模式：双页模式下是否按日漫习惯从右到左排列。</summary>
    public bool RightToLeft { get; set; } = false;

    /// <summary>图片模式：默认适应方式。</summary>
    public ImageFitMode FitMode { get; set; } = ImageFitMode.FitWindow;

    /// <summary>校验并修正越界或非法配置值，防止损坏的 JSON 导致异常或内存溢出。</summary>
    public void Sanitize()
    {
        if (!Enum.IsDefined(Mode)) Mode = ViewMode.Comic;
        if (!Enum.IsDefined(FitMode)) FitMode = ImageFitMode.FitWindow;

        if (double.IsNaN(ComicWidthRatio) || double.IsInfinity(ComicWidthRatio))
            ComicWidthRatio = 0.75;
        ComicWidthRatio = Math.Clamp(ComicWidthRatio, 0.1, 1.0);

        if (double.IsNaN(ScrollStepPixels) || double.IsInfinity(ScrollStepPixels))
            ScrollStepPixels = 300;
        ScrollStepPixels = Math.Clamp(ScrollStepPixels, 50, 5000);

        PreloadUpCount = Math.Clamp(PreloadUpCount, 0, 50);
        PreloadDownCount = Math.Clamp(PreloadDownCount, 0, 50);

        if (double.IsNaN(ComicPageGap) || double.IsInfinity(ComicPageGap))
            ComicPageGap = 4;
        ComicPageGap = Math.Clamp(ComicPageGap, 0, 40);

        if (double.IsNaN(AutoScrollSpeed) || double.IsInfinity(AutoScrollSpeed))
            AutoScrollSpeed = 180;
        AutoScrollSpeed = Math.Clamp(AutoScrollSpeed, 20, 3000);

        if (!IsValidColorString(BackgroundColor))
            BackgroundColor = "#1a1a1a";
    }

    private static bool IsValidColorString(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return false;
        try
        {
            _ = ColorConverter.ConvertFromString(color);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>复制一份，供设置窗口在保存前独立编辑。</summary>
    public AppConfig Clone() => new()
    {
        Mode = Mode,
        ComicWidthRatio = ComicWidthRatio,
        StartFullscreen = StartFullscreen,
        ScrollStepPixels = ScrollStepPixels,
        PreloadUpCount = PreloadUpCount,
        PreloadDownCount = PreloadDownCount,
        ComicPageGap = ComicPageGap,
        AutoScrollSpeed = AutoScrollSpeed,
        BackgroundColor = BackgroundColor,
        TruncateBeforeStartFile = TruncateBeforeStartFile,
        ConfirmBeforeDelete = ConfirmBeforeDelete,
        DualPageMode = DualPageMode,
        RightToLeft = RightToLeft,
        FitMode = FitMode,
    };

    /// <summary>把另一份配置的可编辑字段写回自身。</summary>
    public void CopyFrom(AppConfig other)
    {
        Mode = other.Mode;
        ComicWidthRatio = other.ComicWidthRatio;
        StartFullscreen = other.StartFullscreen;
        ScrollStepPixels = other.ScrollStepPixels;
        PreloadUpCount = other.PreloadUpCount;
        PreloadDownCount = other.PreloadDownCount;
        ComicPageGap = other.ComicPageGap;
        AutoScrollSpeed = other.AutoScrollSpeed;
        BackgroundColor = other.BackgroundColor;
        TruncateBeforeStartFile = other.TruncateBeforeStartFile;
        ConfirmBeforeDelete = other.ConfirmBeforeDelete;
        DualPageMode = other.DualPageMode;
        RightToLeft = other.RightToLeft;
        FitMode = other.FitMode;
        Sanitize();
    }

    /// <summary>支持的图片扩展名（小写，含点）。</summary>
    [JsonIgnore]
    public static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".wmf", ".emf"];
}
