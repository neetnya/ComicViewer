using System.IO;
using System.Windows.Media.Imaging;

namespace ComicViewer.Services;

/// <summary>
/// 图片加载服务：异步加载 <see cref="BitmapImage"/>，支持 GIF 动画。
/// 关键点：
/// - 使用 <see cref="BitmapCacheOption.OnLoad"/> 立即释放文件句柄，避免占用图片文件。
/// - 对 GIF 使用 <see cref="GifBitmapDecoder"/> 以支持动画播放。
/// - 设置 <see cref="BitmapImage.DecodePixelWidth"/> 进行解码缩放，降低内存占用，
///   在漫画模式下按统一宽度解码可显著提升流畅度。
/// </summary>
public static class ImageLoaderService
{
    /// <summary>
    /// 异步加载图片。若指定 <paramref name="decodePixelWidth"/>，则按该宽度（像素）解码，
    /// 适合漫画模式的统一宽度显示，既省内存又流畅。
    /// </summary>
    /// <param name="path">图片完整路径。</param>
    /// <param name="decodePixelWidth">解码宽度（0 表示不缩放，按原图）。</param>
    /// <param name="cancellationToken">目录切换时取消，避免旧图写回新列表。</param>
    /// <returns>BitmapSource，已冻结（可跨线程使用）；失败或取消时返回 null。</returns>
    public static Task<BitmapSource?> LoadAsync(string path, int decodePixelWidth = 0,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<BitmapSource?>(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path))
                    return null;

                var ext = Path.GetExtension(path).ToLowerInvariant();
                var isGif = ext == ".gif";

                // GIF 单帧解码仍可用于占位；动画由 AutoGifImage 按帧播放。
                if (isGif)
                {
                    using var stream = new FileStream(path, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                    var decoder = new GifBitmapDecoder(
                        stream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0];
                    frame.Freeze();
                    return frame;
                }

                // 普通图片：用 BitmapImage + DecodePixelWidth 优化。
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                if (decodePixelWidth > 0)
                    bmp.DecodePixelWidth = decodePixelWidth;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>
    /// 获取图片原始尺寸（宽高，像素），只解析文件头、不把像素数据解码进内存。
    /// 使用 <see cref="BitmapCreateOptions.DelayCreation"/> 推迟像素解码，仅读取
    /// <see cref="BitmapFrame.PixelWidth"/>/<see cref="BitmapFrame.PixelHeight"/> 元数据，
    /// 避免为计算占位高度而把整张图完整解码（大文件夹下这是 CPU 占用飙升的主因）。
    /// </summary>
    public static SizeInt GetPixelSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return new SizeInt(frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return new SizeInt(0, 0);
        }
    }

    /// <summary>
    /// 解码 GIF 全部帧及每帧延迟（毫秒），帧已冻结，可在 UI 线程轮播。
    /// </summary>
    public static List<(BitmapSource Frame, int DelayMs)> DecodeGifFrames(string path)
    {
        using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frames = new List<(BitmapSource, int)>(decoder.Frames.Count);
        foreach (var frame in decoder.Frames)
        {
            if (!frame.IsFrozen)
                frame.Freeze();
            frames.Add((frame, ReadGifDelayMs(frame)));
        }
        return frames;
    }

    private static int ReadGifDelayMs(BitmapFrame frame)
    {
        const int fallbackMs = 100;
        try
        {
            if (frame.Metadata is BitmapMetadata meta)
            {
                var delay = meta.GetQuery("/grctlext/Delay");
                if (delay != null)
                {
                    var hundredths = Convert.ToInt32(delay);
                    // GIF 延迟 0 在浏览器里通常按 100ms 处理。
                    if (hundredths <= 0) return fallbackMs;
                    return hundredths * 10;
                }
            }
        }
        catch
        {
            // 无图形控制扩展时用默认间隔
        }
        return fallbackMs;
    }
}

/// <summary>整型尺寸。</summary>
public readonly record struct SizeInt(int Width, int Height);
