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
    /// <returns>BitmapSource，已冻结（可跨线程使用）。</returns>
    public static Task<BitmapSource> LoadAsync(string path, int decodePixelWidth = 0)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path))
                    return null!;

                var ext = Path.GetExtension(path).ToLowerInvariant();
                var isGif = ext == ".gif";

                // GIF 单独处理以保证动画播放。
                if (isGif)
                {
                    // GifBitmapDecoder 支持多帧动画；WPF Image 控件可自动播放。
                    using var stream = new FileStream(path, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                    var decoder = new GifBitmapDecoder(
                        stream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0];
                    frame.Freeze();
                    return (BitmapSource)frame;
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
            catch
            {
                return null!;
            }
        });
    }

    /// <summary>
    /// 获取图片原始尺寸（宽高，像素），不加载整张到内存。
    /// </summary>
    public static SizeInt GetPixelSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            return new SizeInt(frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return new SizeInt(0, 0);
        }
    }
}

/// <summary>整型尺寸。</summary>
public readonly record struct SizeInt(int Width, int Height);
