using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicViewer.Services;

/// <summary>
/// 图片加载服务：异步加载并解码图片，支持 EXIF 方向自动校正、GIF 完整帧合成与并发解码限流。
/// 关键点：
/// - 统一通过 FileStream + StreamSource 解码，彻底避免文件名含 '#' / '%' 时 UriSource 解析失败。
/// - 使用 BitmapCacheOption.OnLoad 立即释放文件句柄，避免占用图片文件。
/// - 使用 SemaphoreSlim 限制并发解码数，防止快速滚动打满线程池。
/// </summary>
public static class ImageLoaderService
{
    private static readonly SemaphoreSlim DecodeGate = new(4, 4);

    /// <summary>
    /// 异步加载静态图片。若指定 <paramref name="decodePixelWidth"/>，则按该宽度（像素）解码。
    /// 自动处理 EXIF Orientation 旋转与镜像。
    /// </summary>
    public static async Task<BitmapSource?> LoadAsync(string path, int decodePixelWidth = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            return await Task.Run<BitmapSource?>(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(path))
                        return null;

                    int orientation;
                    using (var metaStream = new FileStream(path, FileMode.Open,
                        FileAccess.Read, FileShare.Read))
                    {
                        orientation = ReadExifOrientation(metaStream);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    using var stream = new FileStream(path, FileMode.Open,
                        FileAccess.Read, FileShare.Read);

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = stream;
                    if (decodePixelWidth > 0)
                    {
                        // 旋转 90°/270° 时，原图高度才是旋转后的显示宽度。
                        if (orientation is 5 or 6 or 7 or 8)
                            bmp.DecodePixelHeight = decodePixelWidth;
                        else
                            bmp.DecodePixelWidth = decodePixelWidth;
                    }
                    bmp.EndInit();
                    bmp.Freeze();

                    var result = ApplyExifOrientation(bmp, orientation);
                    if (!result.IsFrozen)
                        result.Freeze();
                    return result;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch
                {
                    return null;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    /// <summary>
    /// 获取图片原始尺寸（宽高，像素），只解析文件头、不把像素数据解码进内存。
    /// 若图片带有 90°/270° EXIF 方向标记，自动对调宽高以匹配最终显示比例。
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
            var w = frame.PixelWidth;
            var h = frame.PixelHeight;
            var orientation = ReadExifOrientation(frame);
            return orientation is 5 or 6 or 7 or 8
                ? new SizeInt(h, w)
                : new SizeInt(w, h);
        }
        catch
        {
            return new SizeInt(0, 0);
        }
    }

    /// <summary>
    /// 解码并合成 GIF 全部完整帧及每帧延迟（毫秒）。
    /// 正确处理局部帧偏移（Left/Top）、透明合成与 Disposal Method（保留/清背景/恢复上一帧），
    /// 解决优化 GIF 播放花屏、残影与跳动问题。
    /// </summary>
    public static List<(BitmapSource Frame, int DelayMs)> DecodeGifFrames(
        string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var frameCount = decoder.Frames.Count;
        var result = new List<(BitmapSource, int)>(frameCount);
        if (frameCount == 0) return result;

        // 逻辑画布尺寸：优先读全局逻辑屏幕尺寸，缺失则回退第 0 帧尺寸。
        var canvasWidth = ReadGifMetaInt(decoder.Metadata, "/logscrdesc/Width");
        var canvasHeight = ReadGifMetaInt(decoder.Metadata, "/logscrdesc/Height");
        if (canvasWidth <= 0) canvasWidth = decoder.Frames[0].PixelWidth;
        if (canvasHeight <= 0) canvasHeight = decoder.Frames[0].PixelHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0) return result;

        // 单帧 GIF 无需画布合成。
        if (frameCount == 1)
        {
            var single = decoder.Frames[0];
            if (!single.IsFrozen) single.Freeze();
            result.Add((single, ReadGifDelayMs(single)));
            return result;
        }

        var canvasStride = canvasWidth * 4;
        var canvas = new byte[canvasHeight * canvasStride];
        byte[]? previousSnapshot = null;

        for (var i = 0; i < frameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rawFrame = decoder.Frames[i];
            var left = Math.Max(0, ReadGifMetaInt(rawFrame.Metadata, "/imgdesc/Left"));
            var top = Math.Max(0, ReadGifMetaInt(rawFrame.Metadata, "/imgdesc/Top"));
            var disposal = ReadGifMetaInt(rawFrame.Metadata, "/grctlext/Disposal");

            // Disposal == 3（Restore to previous）需要在绘制本帧前保存当前画布快照。
            if (disposal == 3)
            {
                previousSnapshot ??= new byte[canvas.Length];
                Buffer.BlockCopy(canvas, 0, previousSnapshot, 0, canvas.Length);
            }

            // 将局部帧转为 BGRA32 并混合到完整画布。
            var bgra = rawFrame.Format == PixelFormats.Bgra32
                ? (BitmapSource)rawFrame
                : new FormatConvertedBitmap(rawFrame, PixelFormats.Bgra32, null, 0);

            var fw = bgra.PixelWidth;
            var fh = bgra.PixelHeight;
            var frameStride = fw * 4;
            var framePixels = new byte[fh * frameStride];
            bgra.CopyPixels(framePixels, frameStride, 0);

            var drawW = Math.Min(fw, canvasWidth - left);
            var drawH = Math.Min(fh, canvasHeight - top);
            for (var y = 0; y < drawH; y++)
            {
                var srcRow = y * frameStride;
                var dstRow = (top + y) * canvasStride + left * 4;
                for (var x = 0; x < drawW; x++)
                {
                    var si = srcRow + x * 4;
                    var a = framePixels[si + 3];
                    if (a == 0) continue; // GIF 透明像素保持底层画布内容

                    var di = dstRow + x * 4;
                    if (a == 255)
                    {
                        canvas[di] = framePixels[si];
                        canvas[di + 1] = framePixels[si + 1];
                        canvas[di + 2] = framePixels[si + 2];
                        canvas[di + 3] = 255;
                    }
                    else
                    {
                        var inv = 255 - a;
                        canvas[di] = (byte)((framePixels[si] * a + canvas[di] * inv) / 255);
                        canvas[di + 1] = (byte)((framePixels[si + 1] * a + canvas[di + 1] * inv) / 255);
                        canvas[di + 2] = (byte)((framePixels[si + 2] * a + canvas[di + 2] * inv) / 255);
                        canvas[di + 3] = (byte)Math.Min(255, a + (canvas[di + 3] * inv) / 255);
                    }
                }
            }

            // 从当前画布生成冻结后的完整帧。
            var composed = BitmapSource.Create(
                canvasWidth, canvasHeight, 96, 96,
                PixelFormats.Bgra32, null, canvas, canvasStride);
            composed.Freeze();
            result.Add((composed, ReadGifDelayMs(rawFrame)));

            // 应用 Disposal Method，为下一帧准备画布。
            if (disposal == 2)
            {
                // Restore to background（透明清除本帧覆盖区域）
                for (var y = 0; y < drawH; y++)
                {
                    var dstRow = (top + y) * canvasStride + left * 4;
                    Array.Clear(canvas, dstRow, drawW * 4);
                }
            }
            else if (disposal == 3 && previousSnapshot != null)
            {
                Buffer.BlockCopy(previousSnapshot, 0, canvas, 0, canvas.Length);
            }
        }

        return result;
    }

    private static int ReadExifOrientation(Stream stream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return 1;
            return ReadExifOrientation(decoder.Frames[0]);
        }
        catch
        {
            return 1;
        }
    }

    private static int ReadExifOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta)
            {
                object? val = null;
                if (meta.ContainsQuery("System.Photo.Orientation"))
                    val = meta.GetQuery("System.Photo.Orientation");
                else if (meta.ContainsQuery("/app1/ifd/{ushort=274}"))
                    val = meta.GetQuery("/app1/ifd/{ushort=274}");

                if (val != null)
                    return Convert.ToInt32(val);
            }
        }
        catch
        {
            // 非 JPEG/TIFF 或无 EXIF 时忽略
        }
        return 1;
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = [new RotateTransform(90), new ScaleTransform(-1, 1)] },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = [new RotateTransform(270), new ScaleTransform(-1, 1)] },
            8 => new RotateTransform(270),
            _ => null,
        };

        if (transform == null) return source;
        if (!transform.IsFrozen) transform.Freeze();
        var tb = new TransformedBitmap(source, transform);
        tb.Freeze();
        return tb;
    }

    private static int ReadGifMetaInt(ImageMetadata? metadata, string query)
    {
        try
        {
            if (metadata is BitmapMetadata meta && meta.ContainsQuery(query))
            {
                var val = meta.GetQuery(query);
                if (val != null)
                    return Convert.ToInt32(val);
            }
        }
        catch
        {
            // 忽略缺失字段
        }
        return 0;
    }

    private static int ReadGifDelayMs(BitmapFrame frame)
    {
        const int fallbackMs = 100;
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.ContainsQuery("/grctlext/Delay"))
            {
                var delay = meta.GetQuery("/grctlext/Delay");
                if (delay != null)
                {
                    var hundredths = Convert.ToInt32(delay);
                    // GIF 延迟 <= 1 (<=10ms) 在主流浏览器中通常按 100ms 处理，避免极端高刷。
                    if (hundredths <= 1) return fallbackMs;
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
