using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ComicViewer.Services;

namespace ComicViewer;

/// <summary>
/// 静态图走 <see cref="StaticSource"/>；GIF 自行按帧播放。
/// 不直接绑定 Image.Source，避免空 Source 把正在播的 GIF 冲掉。
/// </summary>
public sealed class AutoGifImage : Image
{
    public static readonly DependencyProperty GifPathProperty =
        DependencyProperty.Register(
            nameof(GifPath), typeof(string), typeof(AutoGifImage),
            new PropertyMetadata(null, OnGifPathChanged));

    public static readonly DependencyProperty StaticSourceProperty =
        DependencyProperty.Register(
            nameof(StaticSource), typeof(ImageSource), typeof(AutoGifImage),
            new PropertyMetadata(null, OnStaticSourceChanged));

    private List<(BitmapSource Frame, int DelayMs)>? _frames;
    private int _index;
    private DispatcherTimer? _timer;
    private int _playEpoch;

    public string? GifPath
    {
        get => (string?)GetValue(GifPathProperty);
        set => SetValue(GifPathProperty, value);
    }

    public ImageSource? StaticSource
    {
        get => (ImageSource?)GetValue(StaticSourceProperty);
        set => SetValue(StaticSourceProperty, value);
    }

    public AutoGifImage()
    {
        Loaded += (_, _) => TryStart();
        Unloaded += (_, _) => StopPlayback();
    }

    private static void OnGifPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var img = (AutoGifImage)d;
        img.StopPlayback();
        img.TryStart();
    }

    private static void OnStaticSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var img = (AutoGifImage)d;
        if (!IsGif(img.GifPath))
            img.Source = img.StaticSource;
    }

    private void TryStart()
    {
        if (!IsLoaded) return;
        var path = GifPath;
        if (!IsGif(path))
        {
            Source = StaticSource;
            return;
        }

        _ = PlayGifAsync(path!);
    }

    private async Task PlayGifAsync(string path)
    {
        var epoch = ++_playEpoch;
        List<(BitmapSource Frame, int DelayMs)> frames;
        try
        {
            frames = await Task.Run(() => ImageLoaderService.DecodeGifFrames(path));
        }
        catch
        {
            return;
        }

        if (epoch != _playEpoch) return;
        if (!string.Equals(GifPath, path, StringComparison.OrdinalIgnoreCase)) return;
        if (frames.Count == 0) return;

        if (frames.Count == 1)
        {
            Source = frames[0].Frame;
            return;
        }

        _frames = frames;
        _index = 0;
        Source = frames[0].Frame;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(frames[0].DelayMs) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_frames == null || _frames.Count == 0) return;
        _index = (_index + 1) % _frames.Count;
        var (frame, delay) = _frames[_index];
        Source = frame;
        if (_timer != null)
            _timer.Interval = TimeSpan.FromMilliseconds(delay);
    }

    private void StopPlayback()
    {
        _playEpoch++;
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        _frames = null;
        _index = 0;
    }

    private static bool IsGif(string? path) =>
        !string.IsNullOrEmpty(path) &&
        Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
}
