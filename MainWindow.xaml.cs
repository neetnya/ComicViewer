using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ComicViewer.Models;
using ComicViewer.Services;

namespace ComicViewer;

/// <summary>
/// 主窗口：实现漫画连续滚动与单张/双页图片查看模式、全屏与自适应布局、
/// 异步目录导航、自动滚动、快速跳转、旋转/适应模式及右键菜单。
/// </summary>
public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private string? _currentFolder;      // 当前查看图片的文件夹
    private string? _sidebarFolder;      // 侧栏当前显示的目录（null=磁盘根）
    private List<string> _imageFiles = [];
    private int _currentImageIndex;
    private int _currentImageCountShown = 1; // 图片模式当前同屏显示的张数（1 或 2）

    // 漫画模式下的图片源数据列表（绑到 ItemsControl）。
    private readonly ObservableCollection<ComicImageItem> _comicItems = [];
    private bool _heightsComputed;
    private int _lastComicDisplayedIndex = -1;
    private double _dpiScaleX = 1.0;

    // 图片模式：临时缩放、适应基数、旋转角度与拖动平移。
    private double _imageTempZoom = 1.0;
    private double _baseFitScale = 1.0;
    private double _imageRotation;
    private bool _imagePanning;
    private bool _imageDragMoved;
    private Point _panStart;
    private Point _panLast;
    private bool _isDeleting;
    private bool _suppressJumpSlider;

    // 漫画模式：惯性滚动与自动滚动（仅在运动时订阅 CompositionTarget.Rendering）。
    private double _scrollVelocity;
    private bool _scrollAnimating;
    private bool _autoScrolling;
    private bool _renderingHooked;
    private DateTime _lastScrollTick;

    private const double ScrollFriction = 0.90;
    private const double MaxScrollVelocity = 120000;
    private double _lastStableMax = -1;

    // 侧栏目录：异步加载防竞态。
    private string? _selectedPath;
    private List<FolderListEntry> _folderEntries = [];
    private CancellationTokenSource _sidebarCts = new();

    // 定时器与防抖。
    private DispatcherTimer? _sidebarHideTimer;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _resizeTimer;
    private DispatcherTimer? _widthDebounceTimer;
    private bool _sidebarHovering;
    private bool _topBarHovering;

    // 打开目录 / 切本时递增，使进行中的解码结果作废。
    private CancellationTokenSource _loadCts = new();
    private CancellationTokenSource _widthDecodeCts = new();
    private int _loadEpoch;

    public MainWindow()
    {
        InitializeComponent();
        ComicList.ItemsSource = _comicItems;
    }

    #region 启动与关闭

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (newDpi.DpiScaleX > 0)
            _dpiScaleX = newDpi.DpiScaleX;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _config = ConfigService.Load();
        var dpi = VisualTreeHelper.GetDpi(this);
        if (dpi.DpiScaleX > 0) _dpiScaleX = dpi.DpiScaleX;
        ApplyBackgroundBrush();

        // 启动侧栏/顶栏自动隐藏定时器（全屏时鼠标移开自动隐藏）
        _sidebarHideTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Background,
            SidebarHideTimer_Tick,
            Dispatcher);
        _sidebarHideTimer.Start();

        // 应用初始全屏状态与布局边距
        if (_config.StartFullscreen)
            EnterFullscreen();
        else
            ExitFullscreen();

        // 根据配置应用模式
        ApplyMode(_config.Mode, save: false);

        // 从命令行参数获取打开的文件或文件夹
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
        {
            var argPath = args[1];
            if (Directory.Exists(argPath))
            {
                await OpenFolderAsync(argPath, updateSidebar: true);
                return;
            }
            if (File.Exists(argPath))
            {
                if (!FolderNavigationService.IsImageFile(argPath))
                {
                    ShowStatus("不支持的文件格式");
                }
                else
                {
                    var folder = Path.GetDirectoryName(argPath);
                    if (folder != null)
                    {
                        await OpenFolderAsync(folder, updateSidebar: false, startFile: argPath);
                        _sidebarFolder = Directory.GetParent(folder)?.FullName;
                        _selectedPath = folder;
                        await BuildSidebarFolderListAsync();
                        SelectSidebarItem(folder);
                        return;
                    }
                }
            }
        }

        // 无文件夹：侧栏显示计算机磁盘根目录
        _sidebarFolder = null;
        await BuildSidebarFolderListAsync();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        _sidebarHideTimer?.Stop();
        _toastTimer?.Stop();
        _resizeTimer?.Stop();
        _widthDebounceTimer?.Stop();
        _scrollAnimating = false;
        _autoScrolling = false;
        UpdateRenderingSubscription();

        _loadCts.Cancel();
        _loadCts.Dispose();
        _widthDecodeCts.Cancel();
        _widthDecodeCts.Dispose();
        _sidebarCts.Cancel();
        _sidebarCts.Dispose();

        ConfigService.Save(_config);
    }

    private void ApplyBackgroundBrush()
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(_config.BackgroundColor);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Background = brush;
            RootGrid.Background = brush;
            ComicScroll.Background = brush;
            ImagePanel.Background = brush;
        }
        catch
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));
            brush.Freeze();
            Background = brush;
            RootGrid.Background = brush;
            ComicScroll.Background = brush;
            ImagePanel.Background = brush;
        }
    }

    #endregion

    #region 全屏切换与布局适配

    private bool _isFullscreen;

    private void EnterFullscreen()
    {
        _isFullscreen = true;
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;

        ContentHost.Margin = new Thickness(0);
        Sidebar.Margin = new Thickness(0);
        TopBar.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        Sidebar.Visibility = Visibility.Collapsed;
        _topBarHovering = false;
        _sidebarHovering = false;

        ScheduleLayoutRescale();
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = WindowState.Normal;

        ContentHost.Margin = new Thickness(240, 34, 0, 0);
        Sidebar.Margin = new Thickness(0, 34, 0, 0);
        TopBar.Visibility = Visibility.Visible;
        Sidebar.Visibility = Visibility.Visible;
        if (_imageFiles.Count > 0)
            StatusBar.Visibility = Visibility.Visible;

        ScheduleLayoutRescale();
    }

    private void ScheduleLayoutRescale()
    {
        if (_config.Mode == ViewMode.Comic && _comicItems.Count > 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)ResizeComicDisplay);
        }
        else if (_config.Mode == ViewMode.Image && _imageFiles.Count > 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)ApplyImageFitAndTransform);
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_config.Mode == ViewMode.Image)
        {
            ApplyImageFitAndTransform();
            return;
        }
        if (_config.Mode != ViewMode.Comic || _comicItems.Count == 0)
            return;

        if (_resizeTimer == null)
        {
            _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _resizeTimer.Tick += ResizeTimer_Tick;
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void ResizeTimer_Tick(object? sender, EventArgs e)
    {
        _resizeTimer?.Stop();
        if (_config.Mode == ViewMode.Comic && _comicItems.Count > 0)
            ResizeComicDisplay();
    }

    private void TopBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isFullscreen) return;
        if (e.OriginalSource is DependencyObject d &&
            (d.FindAncestorOrSelf<Button>() != null || ReferenceEquals(d, TopPageIndicator)))
            return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MainWindow_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (JumpPageBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Escape)
            {
                CloseJumpOverlay();
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.F1)
        {
            ToggleHelpOverlay();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (HelpOverlay.Visibility == Visibility.Visible)
            {
                HelpOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            if (JumpOverlay.Visibility == Visibility.Visible)
            {
                CloseJumpOverlay();
                e.Handled = true;
                return;
            }
            if (_autoScrolling)
            {
                SetAutoScroll(false);
                e.Handled = true;
                return;
            }
            if (_isFullscreen) ExitFullscreen();
            e.Handled = true;
            return;
        }

        if (HelpOverlay.Visibility == Visibility.Visible) return;

        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.PageUp:
            case Key.W:
                GoPrev();
                e.Handled = true;
                break;
            case Key.Right:
            case Key.PageDown:
            case Key.S:
                GoNext();
                e.Handled = true;
                break;
            case Key.Up:
                if (_config.Mode == ViewMode.Comic)
                    NudgeComicScroll(-_config.ScrollStepPixels);
                else
                    GoPrev();
                e.Handled = true;
                break;
            case Key.Down:
                if (_config.Mode == ViewMode.Comic)
                    NudgeComicScroll(_config.ScrollStepPixels);
                else
                    GoNext();
                e.Handled = true;
                break;
            case Key.Space:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    GoPrev();
                else
                    GoNext();
                e.Handled = true;
                break;
            case Key.Enter:
                HandleLeftClickAction();
                e.Handled = true;
                break;
            case Key.Home:
                JumpToPageIndex(0);
                e.Handled = true;
                break;
            case Key.End:
                if (_imageFiles.Count > 0)
                    JumpToPageIndex(_imageFiles.Count - 1);
                e.Handled = true;
                break;
            case Key.G:
                ToggleJumpOverlay();
                e.Handled = true;
                break;
            case Key.A:
                if (_config.Mode == ViewMode.Comic)
                {
                    SetAutoScroll(!_autoScrolling);
                    e.Handled = true;
                }
                break;
            case Key.OemPlus:
            case Key.Add:
                if (_config.Mode == ViewMode.Comic)
                {
                    AdjustAutoScrollSpeed(40);
                    e.Handled = true;
                }
                break;
            case Key.OemMinus:
            case Key.Subtract:
                if (_config.Mode == ViewMode.Comic)
                {
                    AdjustAutoScrollSpeed(-40);
                    e.Handled = true;
                }
                break;
            case Key.D1:
            case Key.NumPad1:
                if (_config.Mode == ViewMode.Image)
                {
                    SetImageFitMode(ImageFitMode.FitWindow);
                    e.Handled = true;
                }
                break;
            case Key.D2:
            case Key.NumPad2:
                if (_config.Mode == ViewMode.Image)
                {
                    SetImageFitMode(ImageFitMode.FitWidth);
                    e.Handled = true;
                }
                break;
            case Key.D3:
            case Key.NumPad3:
                if (_config.Mode == ViewMode.Image)
                {
                    SetImageFitMode(ImageFitMode.ActualSize);
                    e.Handled = true;
                }
                break;
            case Key.Q:
            case Key.OemOpenBrackets:
                if (_config.Mode == ViewMode.Image)
                {
                    RotateImage(-90);
                    e.Handled = true;
                }
                break;
            case Key.R:
            case Key.OemCloseBrackets:
                if (_config.Mode == ViewMode.Image)
                {
                    RotateImage(90);
                    e.Handled = true;
                }
                break;
            case Key.D0:
            case Key.NumPad0:
                if (_config.Mode == ViewMode.Image)
                {
                    _imageRotation = 0;
                    ResetImageZoomAndPan();
                    ShowStatus("已重置旋转与缩放");
                    e.Handled = true;
                }
                break;
            case Key.D:
                if (_config.Mode == ViewMode.Image)
                {
                    _ = ToggleDualPageModeAsync();
                    e.Handled = true;
                }
                break;
            case Key.M:
                if (_config.Mode == ViewMode.Image)
                {
                    _ = ToggleRightToLeftAsync();
                    e.Handled = true;
                }
                break;
            case Key.Delete:
                if (_config.Mode == ViewMode.Image)
                {
                    _ = DeleteCurrentImageAsync();
                    e.Handled = true;
                }
                break;
        }
    }

    #endregion

    #region 模式切换

    private void BtnToggleMode_OnClick(object sender, RoutedEventArgs e) =>
        _ = ToggleModeAsync(_config.Mode == ViewMode.Comic ? ViewMode.Image : ViewMode.Comic);

    private async Task ToggleModeAsync(ViewMode toMode)
    {
        SetAutoScroll(false, silent: true);

        int currentIndex = _config.Mode == ViewMode.Comic
            ? GetComicCurrentImageIndex()
            : _currentImageIndex;

        ApplyMode(toMode, save: true);

        if (_imageFiles.Count == 0) return;

        if (toMode == ViewMode.Comic)
        {
            if (_comicItems.Count != _imageFiles.Count)
                await LoadComicModeAsync(currentIndex, _loadCts.Token);
            else
                ScrollComicToIndex(currentIndex);
        }
        else
        {
            _currentImageIndex = Math.Clamp(currentIndex, 0, _imageFiles.Count - 1);
            await ShowImageAsync(_currentImageIndex, _loadCts.Token);
        }
    }

    private void ApplyMode(ViewMode mode, bool save)
    {
        _config.Mode = mode;
        BtnToggleMode.Content = mode == ViewMode.Comic ? "图片模式" : "漫画模式";

        bool showComic = mode == ViewMode.Comic;
        ComicScroll.Visibility = showComic ? Visibility.Visible : Visibility.Collapsed;
        ImagePanel.Visibility = showComic ? Visibility.Collapsed : Visibility.Visible;
        BtnDelete.Visibility = showComic ? Visibility.Collapsed : Visibility.Visible;

        if (save) ConfigService.Save(_config);

        _imageRotation = 0;
        ResetImageZoomAndPan();
    }

    #endregion

    #region 打开文件夹与漫画模式加载

    private CancellationToken BeginNewLoad()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();

        _widthDecodeCts.Cancel();
        _widthDecodeCts.Dispose();
        _widthDecodeCts = new CancellationTokenSource();

        unchecked { _loadEpoch++; }
        _scrollVelocity = 0;
        _scrollAnimating = false;
        _autoScrolling = false;
        UpdateRenderingSubscription();
        return _loadCts.Token;
    }

    private async Task OpenFolderAsync(string folder, bool updateSidebar = true,
        string? startFile = null, List<string>? prelistedImages = null)
    {
        var ct = BeginNewLoad();
        _currentFolder = folder;
        UpdateSidebarCurrentHighlight();

        _imageFiles = prelistedImages ?? await Task.Run(() => FolderNavigationService.ListImages(folder), ct);
        if (ct.IsCancellationRequested) return;

        int startIndex = 0;
        if (startFile != null)
        {
            var idx = _imageFiles.FindIndex(f =>
                string.Equals(f, startFile, StringComparison.OrdinalIgnoreCase));
            if (idx > 0)
            {
                if (_config.TruncateBeforeStartFile)
                {
                    _imageFiles = _imageFiles.GetRange(idx, _imageFiles.Count - idx);
                    startIndex = 0;
                }
                else
                {
                    startIndex = idx;
                }
            }
        }

        ComicScroll.ScrollChanged -= ComicScroll_OnScrollChanged;
        ComicScroll.ScrollToVerticalOffset(0);

        _comicItems.Clear();
        _heightsComputed = false;
        _lastComicDisplayedIndex = -1;
        _currentImageIndex = startIndex;
        _lastStableMax = -1;
        _lastReleaseOffset = double.NaN;
        _lastScrollCheckOffset = double.NaN;
        TitleFileName.Text = "";
        TopPageIndicator.Text = "";

        if (updateSidebar)
        {
            _sidebarFolder = folder;
            await BuildSidebarFolderListAsync();
        }

        if (ct.IsCancellationRequested) return;

        if (_imageFiles.Count == 0)
        {
            ClearSingleImages();
            ShowStatus("无图片");
            return;
        }

        if (_config.Mode == ViewMode.Comic)
        {
            await LoadComicModeAsync(startIndex, ct);
        }
        else
        {
            await ShowImageAsync(startIndex, ct);
        }
    }

    private async Task LoadComicModeAsync(int startIndex = 0, CancellationToken ct = default)
    {
        ComicScroll.ScrollChanged -= ComicScroll_OnScrollChanged;
        _comicItems.Clear();
        _heightsComputed = false;
        _lastComicDisplayedIndex = -1;

        var displayWidth = GetComicDisplayWidth();
        var decodeWidth = GetComicDecodeWidth(displayWidth);
        var itemMargin = GetComicItemMargin();

        startIndex = Math.Clamp(startIndex, 0, Math.Max(0, _imageFiles.Count - 1));
        var initFirst = Math.Max(0, startIndex - Math.Clamp(_config.PreloadUpCount, 0, 50));
        var initLast = Math.Min(_imageFiles.Count - 1, startIndex + Math.Clamp(_config.PreloadDownCount, 0, 50));

        for (var i = 0; i < _imageFiles.Count; i++)
        {
            var path = _imageFiles[i];
            _comicItems.Add(new ComicImageItem
            {
                Path = path,
                DisplayWidth = displayWidth,
                ItemMargin = itemMargin,
                IsActive = i >= initFirst && i <= initLast,
                State = IsGifPath(path) ? ComicLoadState.Gif : ComicLoadState.Idle,
            });
        }

        if (ct.IsCancellationRequested) return;

        if (startIndex <= 0)
        {
            if (_comicItems.Count > 0)
                await LoadComicItemAsync(0, decodeWidth, ct);

            if (ct.IsCancellationRequested) return;

            ComicScroll.ScrollToVerticalOffset(0);
            ComicScroll.ScrollChanged -= ComicScroll_OnScrollChanged;
            ComicScroll.ScrollChanged += ComicScroll_OnScrollChanged;
            _scrollVelocity = 0;

            _ = PrecomputeHeightsAsync(displayWidth, ct);
            _ = LoadNearbyPagesAsync(0, decodeWidth, ct);

            UpdateComicIndicators(0);
        }
        else
        {
            await PrecomputeHeightsAsync(displayWidth, ct);
            if (ct.IsCancellationRequested) return;

            var end = Math.Min(startIndex + 3, _comicItems.Count);
            for (var i = startIndex; i < end; i++)
            {
                await LoadComicItemAsync(i, decodeWidth, ct);
                if (ct.IsCancellationRequested) return;
            }

            ComicScroll.ScrollChanged -= ComicScroll_OnScrollChanged;
            ComicScroll.ScrollChanged += ComicScroll_OnScrollChanged;
            _scrollVelocity = 0;

            await Dispatcher.InvokeAsync(
                () => ScrollComicToIndex(startIndex), DispatcherPriority.Loaded);

            UpdateComicIndicators(startIndex);
        }
    }

    private async Task LoadNearbyPagesAsync(int center, int decodeWidth, CancellationToken ct = default)
    {
        var down = Math.Max(0, _config.PreloadDownCount);
        var end = Math.Min(_comicItems.Count - 1, center + down);
        for (var i = Math.Max(0, center + 1); i <= end; i++)
        {
            if (ct.IsCancellationRequested) return;
            await LoadComicItemAsync(i, decodeWidth, ct);
        }
    }

    private double _lastReleaseOffset = double.NaN;
    private double _lastScrollCheckOffset = double.NaN;
    private const double ReleaseThrottlePixels = 300.0;

    private Thickness GetComicItemMargin()
    {
        var half = Math.Clamp(_config.ComicPageGap, 0, 40) / 2.0;
        return new Thickness(0, half, 0, half);
    }

    private double GetItemVerticalGap() => Math.Clamp(_config.ComicPageGap, 0, 40);

    private int GetComicCurrentImageIndex()
    {
        if (_comicItems.Count == 0) return 0;
        var probeOffset = ComicScroll.VerticalOffset + Math.Min(ComicScroll.ViewportHeight * 0.35, 220);
        var gap = GetItemVerticalGap();
        double accum = 0;
        for (var i = 0; i < _comicItems.Count; i++)
        {
            accum += _comicItems[i].DisplayHeight + gap;
            if (probeOffset < accum) return i;
        }
        return _comicItems.Count - 1;
    }

    private void ScrollComicToIndex(int index)
    {
        if (_comicItems.Count == 0) return;
        index = Math.Clamp(index, 0, _comicItems.Count - 1);
        ComicScroll.UpdateLayout();
        var gap = GetItemVerticalGap();
        double offset = 0;
        for (var i = 0; i < index; i++)
            offset += _comicItems[i].DisplayHeight + gap;

        ComicScroll.ScrollToVerticalOffset(offset);
        UpdateComicIndicators(index);
    }

    private async Task PrecomputeHeightsAsync(double displayWidth, CancellationToken ct = default)
    {
        var items = _comicItems.ToList();
        var epoch = _loadEpoch;
        const int chunkSize = 16;

        for (var start = 0; start < items.Count; start += chunkSize)
        {
            if (ct.IsCancellationRequested || epoch != _loadEpoch) return;
            var chunk = items.Skip(start).Take(chunkSize).ToList();
            double[] heights;
            try
            {
                heights = await Task.Run(() =>
                {
                    var result = new double[chunk.Count];
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var size = ImageLoaderService.GetPixelSize(chunk[i].Path);
                        if (size.Width > 0 && size.Height > 0)
                            result[i] = displayWidth * size.Height / size.Width;
                    }
                    return result;
                }, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested || epoch != _loadEpoch) return;

            for (var i = 0; i < chunk.Count; i++)
            {
                if (heights[i] <= 0) continue;
                var idx = start + i;
                if (idx < _comicItems.Count && ReferenceEquals(_comicItems[idx], chunk[i]))
                    chunk[i].DisplayHeight = heights[i];
            }
        }

        if (!ct.IsCancellationRequested && epoch == _loadEpoch)
            _heightsComputed = true;
    }

    private async Task LoadComicItemAsync(int idx, int decodeWidth, CancellationToken ct = default)
    {
        if (idx < 0 || idx >= _comicItems.Count) return;
        var item = _comicItems[idx];

        if (item.State is ComicLoadState.Loading or ComicLoadState.Loaded or ComicLoadState.Failed)
            return;

        var path = item.Path;
        var epoch = _loadEpoch;

        if (item.State == ComicLoadState.Gif || IsGifPath(path))
        {
            item.State = ComicLoadState.Gif;
            if (item.DisplayHeight <= 0)
            {
                SizeInt size;
                try
                {
                    size = await Task.Run(() => ImageLoaderService.GetPixelSize(path), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (ct.IsCancellationRequested || epoch != _loadEpoch) return;
                if (idx >= _comicItems.Count || !ReferenceEquals(_comicItems[idx], item)) return;
                if (size.Width > 0 && size.Height > 0)
                    item.DisplayHeight = item.DisplayWidth * size.Height / size.Width;
            }
            return;
        }

        item.State = ComicLoadState.Loading;
        try
        {
            var bmp = await ImageLoaderService.LoadAsync(path, decodeWidth, ct);
            if (ct.IsCancellationRequested || epoch != _loadEpoch)
            {
                if (item.Source == null) item.State = ComicLoadState.Idle;
                return;
            }
            if (idx >= _comicItems.Count || !ReferenceEquals(_comicItems[idx], item)) return;

            if (bmp == null)
            {
                item.State = ComicLoadState.Failed;
                return;
            }

            if (item.DisplayHeight <= 0 && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                item.DisplayHeight = item.DisplayWidth * bmp.PixelHeight / bmp.PixelWidth;

            item.Source = bmp;
            item.State = ComicLoadState.Loaded;
        }
        catch
        {
            if (epoch == _loadEpoch && idx < _comicItems.Count && ReferenceEquals(_comicItems[idx], item))
                item.State = ComicLoadState.Failed;
        }
    }

    private void ComicScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_loadCts.IsCancellationRequested || _comicItems.Count == 0) return;
        if (ComicScroll.ViewportHeight <= 0) return;

        // 节流：惯性滑行时每移动 >= 48px 才扫描一次页码与预加载窗口，极大降低高刷屏每帧 UI 线程开销。
        var offset = ComicScroll.VerticalOffset;
        if (!double.IsNaN(_lastScrollCheckOffset) &&
            Math.Abs(offset - _lastScrollCheckOffset) < 48.0)
            return;
        _lastScrollCheckOffset = offset;

        var center = GetComicCurrentImageIndex();
        if (center != _lastComicDisplayedIndex)
            UpdateComicIndicators(center);

        var up = Math.Clamp(_config.PreloadUpCount, 0, 50);
        var down = Math.Clamp(_config.PreloadDownCount, 0, 50);
        var first = Math.Max(0, center - up);
        var last = Math.Min(_comicItems.Count - 1, center + down);

        if (double.IsNaN(_lastReleaseOffset) ||
            Math.Abs(offset - _lastReleaseOffset) >= ReleaseThrottlePixels)
        {
            UpdateComicActiveWindow(first, last);
            _lastReleaseOffset = offset;
        }

        var decodeWidth = GetComicDecodeWidth(GetComicDisplayWidth());
        var ct = _loadCts.Token;
        for (var i = first; i <= last; i++)
        {
            var item = _comicItems[i];
            if (item.State == ComicLoadState.Gif)
            {
                if (!item.IsActive) item.IsActive = true;
                if (item.DisplayHeight <= 0)
                    _ = LoadComicItemAsync(i, decodeWidth, ct);
            }
            else if (item.State == ComicLoadState.Idle)
            {
                _ = LoadComicItemAsync(i, decodeWidth, ct);
            }
        }
    }

    /// <summary>同步滑动窗口：释放窗口外静态图的 Source 与 GIF 的帧缓存，静态图不触发 IsActive 通知。</summary>
    private void UpdateComicActiveWindow(int first, int last)
    {
        for (var i = 0; i < _comicItems.Count; i++)
        {
            var item = _comicItems[i];
            if (i < first || i > last)
            {
                if (item.State == ComicLoadState.Gif && item.IsActive)
                    item.IsActive = false;
                if (item.Source != null)
                {
                    item.Source = null;
                    item.State = ComicLoadState.Idle;
                }
            }
            else if (item.State == ComicLoadState.Gif && !item.IsActive)
            {
                item.IsActive = true;
            }
        }
    }

    private void UpdateComicIndicators(int idx)
    {
        if (_comicItems.Count == 0)
        {
            _lastComicDisplayedIndex = -1;
            TitleFileName.Text = "";
            TopPageIndicator.Text = "";
            return;
        }
        idx = Math.Clamp(idx, 0, _comicItems.Count - 1);
        _lastComicDisplayedIndex = idx;
        var name = Path.GetFileName(_comicItems[idx].Path);
        var pageText = $"{idx + 1}/{_comicItems.Count}";
        TitleFileName.Text = name;
        TopPageIndicator.Text = pageText;
        if (!_isFullscreen)
        {
            StatusText.Text = $"{pageText}  {name}";
            StatusBar.Visibility = Visibility.Visible;
        }
    }

    /// <summary>计算漫画模式图片显示宽度（像素）= 阅读区可用宽度 × 比例。</summary>
    private double GetComicDisplayWidth()
    {
        var w = ContentHost.ActualWidth;
        if (w <= 0)
            w = ActualWidth > 0 ? ActualWidth - (_isFullscreen ? 0 : 240) : SystemParameters.PrimaryScreenWidth;
        if (w < 100) w = 100;
        var ratio = Math.Clamp(_config.ComicWidthRatio, 0.1, 1.0);
        return w * ratio;
    }

    /// <summary>结合缓存的 DPI 计算解码像素宽度，确保高分屏清晰且滚动帧零 GetDpi 开销。</summary>
    private int GetComicDecodeWidth(double displayWidth)
    {
        var decodeWidth = (int)Math.Round(displayWidth * Math.Max(1.0, _dpiScaleX));
        return Math.Clamp(decodeWidth, 64, 2880);
    }

    /// <summary>Ctrl+滚轮防抖缩放：立即等比缩放视图，停止滚动 180ms 后统一存盘并重解码。</summary>
    private void ScheduleComicWidthUpdate()
    {
        if (_comicItems.Count > 0)
            ResizeComicDisplay();

        if (_widthDebounceTimer == null)
        {
            _widthDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _widthDebounceTimer.Tick += async (_, _) =>
            {
                _widthDebounceTimer.Stop();
                ConfigService.Save(_config);
                if (_config.Mode == ViewMode.Comic && _comicItems.Count > 0)
                    await RedecodeNearbyComicPagesAsync();
            };
        }
        _widthDebounceTimer.Stop();
        _widthDebounceTimer.Start();
    }

    private async Task RedecodeNearbyComicPagesAsync()
    {
        _widthDecodeCts.Cancel();
        _widthDecodeCts.Dispose();
        _widthDecodeCts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token);
        var ct = _widthDecodeCts.Token;
        var epoch = _loadEpoch;

        var displayWidth = GetComicDisplayWidth();
        var decodeWidth = GetComicDecodeWidth(displayWidth);
        var currentIndex = GetComicCurrentImageIndex();

        var first = Math.Max(0, currentIndex - 2);
        var last = Math.Min(_comicItems.Count - 1, currentIndex + 3);
        for (var i = first; i <= last; i++)
        {
            if (ct.IsCancellationRequested || epoch != _loadEpoch) return;
            var item = _comicItems[i];
            if (item.State == ComicLoadState.Gif || IsGifPath(item.Path)) continue;

            var bmp = await ImageLoaderService.LoadAsync(item.Path, decodeWidth, ct);
            if (bmp == null || ct.IsCancellationRequested || epoch != _loadEpoch) continue;
            if (i >= _comicItems.Count || !ReferenceEquals(_comicItems[i], item)) return;
            item.Source = bmp;
            item.State = ComicLoadState.Loaded;
        }
    }

    private async Task ApplyComicSettingsAsync()
    {
        var gapMargin = GetComicItemMargin();
        foreach (var item in _comicItems)
            item.ItemMargin = gapMargin;

        ResizeComicDisplay();
        await RedecodeNearbyComicPagesAsync();
    }

    private void ResizeComicDisplay()
    {
        if (_comicItems.Count == 0) return;

        var currentIndex = GetComicCurrentImageIndex();
        var newWidth = GetComicDisplayWidth();
        _lastStableMax = -1;

        foreach (var item in _comicItems)
        {
            var oldWidth = item.DisplayWidth;
            if (oldWidth > 0 && item.DisplayHeight > 0)
                item.DisplayHeight *= newWidth / oldWidth;
            item.DisplayWidth = newWidth;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            (Action)(() => ScrollComicToIndex(currentIndex)));
    }

    #endregion

    #region 图片模式（单张 / 双页 / 适应方式 / 旋转 / 删除）

    private void ClearSingleImages()
    {
        PrimaryImage.GifPath = null;
        PrimaryImage.StaticSource = null;
        SecondaryImage.GifPath = null;
        SecondaryImage.StaticSource = null;
        SecondaryImage.Visibility = Visibility.Collapsed;
        _currentImageCountShown = 1;
    }

    private async Task ShowImageAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= _imageFiles.Count) return;
        _currentImageIndex = index;
        var epoch = _loadEpoch;

        bool useDual = _config.DualPageMode && index + 1 < _imageFiles.Count;
        _currentImageCountShown = useDual ? 2 : 1;

        string primaryPath;
        string? secondaryPath;

        if (useDual)
        {
            if (_config.RightToLeft)
            {
                primaryPath = _imageFiles[index + 1];
                secondaryPath = _imageFiles[index];
            }
            else
            {
                primaryPath = _imageFiles[index];
                secondaryPath = _imageFiles[index + 1];
            }
            PrimaryImage.HorizontalAlignment = HorizontalAlignment.Right;
            SecondaryImage.HorizontalAlignment = HorizontalAlignment.Left;
            SecondaryImage.Visibility = Visibility.Visible;
            RightImageCol.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            primaryPath = _imageFiles[index];
            secondaryPath = null;
            PrimaryImage.HorizontalAlignment = HorizontalAlignment.Center;
            SecondaryImage.Visibility = Visibility.Collapsed;
            SecondaryImage.GifPath = null;
            SecondaryImage.StaticSource = null;
            RightImageCol.Width = GridLength.Auto;
        }

        var loadPrimary = LoadIntoSingleControlAsync(PrimaryImage, primaryPath, epoch, index, ct);
        var loadSecondary = secondaryPath != null
            ? LoadIntoSingleControlAsync(SecondaryImage, secondaryPath, epoch, index, ct)
            : Task.CompletedTask;

        await Task.WhenAll(loadPrimary, loadSecondary);
        if (ct.IsCancellationRequested || epoch != _loadEpoch || _currentImageIndex != index)
            return;

        _imageRotation = 0;
        ResetImageZoomAndPan();
        UpdateStatus();
        UpdateCurrentFileName();
    }

    private async Task LoadIntoSingleControlAsync(AutoGifImage target, string path,
        int epoch, int expectedIndex, CancellationToken ct)
    {
        target.GifPath = path;
        if (IsGifPath(path))
        {
            target.StaticSource = null;
            return;
        }

        var bmp = await ImageLoaderService.LoadAsync(path, 0, ct);
        if (ct.IsCancellationRequested || epoch != _loadEpoch || _currentImageIndex != expectedIndex)
            return;
        target.StaticSource = bmp;
    }

    private void ApplyImageFitAndTransform()
    {
        if (_config.Mode != ViewMode.Image) return;

        var viewW = ContentHost.ActualWidth;
        var viewH = ContentHost.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;

        _baseFitScale = 1.0;

        if (_config.FitMode != ImageFitMode.FitWindow && PrimaryImage.Source is BitmapSource bmp)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double natW = bmp.PixelWidth / Math.Max(0.5, dpi.DpiScaleX);
            double natH = bmp.PixelHeight / Math.Max(0.5, dpi.DpiScaleY);
            if (_currentImageCountShown == 2 && SecondaryImage.Source is BitmapSource bmp2)
            {
                natW += bmp2.PixelWidth / Math.Max(0.5, dpi.DpiScaleX) + 4;
                natH = Math.Max(natH, bmp2.PixelHeight / Math.Max(0.5, dpi.DpiScaleY));
            }

            if (natW > 0 && natH > 0)
            {
                var fitWindowScale = Math.Min(viewW / natW, viewH / natH);
                if (fitWindowScale > 0)
                {
                    if (_config.FitMode == ImageFitMode.FitWidth)
                    {
                        var targetScale = viewW / natW;
                        _baseFitScale = Math.Max(1.0, targetScale / fitWindowScale);
                    }
                    else if (_config.FitMode == ImageFitMode.ActualSize)
                    {
                        _baseFitScale = 1.0 / fitWindowScale;
                    }
                }
            }
        }

        var effectiveScale = _baseFitScale * _imageTempZoom;
        ImageRotate.Angle = _imageRotation;
        ImageScale.ScaleX = effectiveScale;
        ImageScale.ScaleY = effectiveScale;
        ClampImagePan();
    }

    private void SetImageFitMode(ImageFitMode mode)
    {
        _config.FitMode = mode;
        ConfigService.Save(_config);
        _imageTempZoom = 1.0;
        ImagePan.X = 0;
        ImagePan.Y = 0;
        ApplyImageFitAndTransform();

        var label = mode switch
        {
            ImageFitMode.FitWidth => "适应宽度",
            ImageFitMode.ActualSize => "1:1 原始大小",
            _ => "适应窗口",
        };
        ShowStatus($"视图：{label}");
    }

    private void RotateImage(double deltaDegrees)
    {
        _imageRotation = (_imageRotation + deltaDegrees) % 360;
        if (_imageRotation < 0) _imageRotation += 360;
        ImagePan.X = 0;
        ImagePan.Y = 0;
        ApplyImageFitAndTransform();
        ShowStatus($"旋转：{_imageRotation:0}°");
    }

    private async Task ToggleDualPageModeAsync()
    {
        _config.DualPageMode = !_config.DualPageMode;
        ConfigService.Save(_config);
        ShowStatus(_config.DualPageMode ? "已开启双页并排" : "已切换为单页模式");
        if (_config.Mode == ViewMode.Image && _imageFiles.Count > 0)
            await ShowImageAsync(_currentImageIndex, _loadCts.Token);
    }

    private async Task ToggleRightToLeftAsync()
    {
        _config.RightToLeft = !_config.RightToLeft;
        ConfigService.Save(_config);
        ShowStatus(_config.RightToLeft ? "双页顺序：日漫从右到左" : "双页顺序：从左到右");
        if (_config.Mode == ViewMode.Image && _config.DualPageMode && _imageFiles.Count > 0)
            await ShowImageAsync(_currentImageIndex, _loadCts.Token);
    }

    private void UpdateStatus()
    {
        if (_imageFiles.Count > 0 && _currentImageIndex < _imageFiles.Count)
        {
            var pageText = _currentImageCountShown == 2 && _currentImageIndex + 1 < _imageFiles.Count
                ? $"{_currentImageIndex + 1}-{_currentImageIndex + 2}/{_imageFiles.Count}"
                : $"{_currentImageIndex + 1}/{_imageFiles.Count}";
            var name = Path.GetFileName(_imageFiles[_currentImageIndex]);
            TopPageIndicator.Text = pageText;
            StatusText.Text = $"{pageText}  {name}";
            if (!_isFullscreen)
                StatusBar.Visibility = Visibility.Visible;
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        if (_isFullscreen)
        {
            ToastText.Text = message;
            ToastBar.Visibility = Visibility.Visible;
            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                _toastTimer.Tick += ToastTimer_Tick;
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        }
        else
        {
            StatusBar.Visibility = Visibility.Visible;
        }
    }

    private void ToastTimer_Tick(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        ToastBar.Visibility = Visibility.Collapsed;
    }

    private void BtnDelete_OnClick(object sender, RoutedEventArgs e) =>
        _ = DeleteCurrentImageAsync();

    private async Task DeleteCurrentImageAsync()
    {
        if (_isDeleting || _config.Mode != ViewMode.Image) return;
        if (_imageFiles.Count == 0 || _currentImageIndex < 0 ||
            _currentImageIndex >= _imageFiles.Count) return;

        var path = _imageFiles[_currentImageIndex];
        if (!File.Exists(path)) return;

        if (_config.ConfirmBeforeDelete)
        {
            var confirm = MessageBox.Show(this,
                $"确定将「{Path.GetFileName(path)}」移入回收站吗？",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        _isDeleting = true;
        try
        {
            if (!RecycleFile(path))
            {
                var perm = MessageBox.Show(this,
                    $"无法将「{Path.GetFileName(path)}」移入回收站（可能位于无回收站的磁盘或网络路径）。\n是否永久删除该文件？（此操作不可撤销）",
                    "无法移入回收站", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (perm != MessageBoxResult.Yes)
                {
                    ShowStatus("已取消删除");
                    return;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    ShowStatus($"删除失败：{ex.Message}");
                    return;
                }
            }

            var removedIndex = _currentImageIndex;
            _imageFiles.RemoveAt(removedIndex);

            if (removedIndex < _comicItems.Count &&
                string.Equals(_comicItems[removedIndex].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _comicItems.RemoveAt(removedIndex);
            }
            else if (_comicItems.Count > 0)
            {
                var found = _comicItems.FirstOrDefault(x =>
                    string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                if (found != null) _comicItems.Remove(found);
            }

            if (_currentImageIndex >= _imageFiles.Count)
                _currentImageIndex = _imageFiles.Count - 1;

            if (_imageFiles.Count == 0)
            {
                ClearSingleImages();
                TitleFileName.Text = "";
                TopPageIndicator.Text = "";
                ShowStatus("无图片");
                return;
            }

            await ShowImageAsync(_currentImageIndex, _loadCts.Token);
        }
        finally
        {
            _isDeleting = false;
        }
    }

    private static bool RecycleFile(string path)
    {
        var ops = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperationW(ref ops) == 0 && !ops.fAnyOperationsAborted;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    private void UpdateCurrentFileName()
    {
        if (_config.Mode == ViewMode.Image)
        {
            if (_imageFiles.Count > 0 && _currentImageIndex < _imageFiles.Count)
            {
                if (_currentImageCountShown == 2 && _currentImageIndex + 1 < _imageFiles.Count)
                {
                    var n1 = Path.GetFileName(_imageFiles[_currentImageIndex]);
                    var n2 = Path.GetFileName(_imageFiles[_currentImageIndex + 1]);
                    TitleFileName.Text = _config.RightToLeft ? $"{n2}  |  {n1}" : $"{n1}  |  {n2}";
                }
                else
                {
                    TitleFileName.Text = Path.GetFileName(_imageFiles[_currentImageIndex]);
                }
            }
            else
            {
                TitleFileName.Text = "";
            }
            return;
        }

        if (_comicItems.Count == 0) { TitleFileName.Text = ""; return; }
        var idx = GetComicCurrentImageIndex();
        TitleFileName.Text = Path.GetFileName(_comicItems[idx].Path);
    }

    #endregion

    #region 鼠标交互（点击翻页、光标中心缩放、平移与右键菜单）

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsOverInteractiveControl(e)) return;

        var effectiveScale = _baseFitScale * _imageTempZoom;
        if (_config.Mode == ViewMode.Image && effectiveScale > 1.02)
        {
            _imagePanning = true;
            _imageDragMoved = false;
            _panStart = e.GetPosition(this);
            _panLast = _panStart;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        HandleLeftClickAction();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_imagePanning)
        {
            var clicked = !_imageDragMoved;
            _imagePanning = false;
            _imageDragMoved = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            if (clicked)
                HandleLeftClickAction();
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    private void ResetImageZoomAndPan()
    {
        _imageTempZoom = 1.0;
        _imagePanning = false;
        _imageDragMoved = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        ImagePan.X = 0;
        ImagePan.Y = 0;
        ApplyImageFitAndTransform();
    }

    private void ClampImagePan()
    {
        var effectiveScale = _baseFitScale * _imageTempZoom;
        if (effectiveScale <= 1.01)
        {
            ImagePan.X = 0;
            ImagePan.Y = 0;
            return;
        }

        var maxX = Math.Max(0, ContentHost.ActualWidth * (effectiveScale - 1.0) / 2.0 + 40);
        var maxY = Math.Max(0, ContentHost.ActualHeight * (effectiveScale - 1.0) / 2.0 + 40);
        ImagePan.X = Math.Clamp(ImagePan.X, -maxX, maxX);
        ImagePan.Y = Math.Clamp(ImagePan.Y, -maxY, maxY);
    }

    private void HandleLeftClickAction()
    {
        if (_config.Mode == ViewMode.Comic)
        {
            if (IsAtComicLastPage())
                _ = GoToNextSiblingFolderAsync();
            else
                ScrollComicByOnePage(1);
        }
        else
        {
            GoNext();
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (IsOverInteractiveControl(e)) return;

        if (_config.Mode == ViewMode.Comic ||
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            OpenReaderContextMenu();
            e.Handled = true;
            return;
        }

        if (_config.Mode == ViewMode.Image)
        {
            GoPrev();
            e.Handled = true;
        }
    }

    private bool IsOverInteractiveControl(MouseEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            if (d.IsDescendantOf(Sidebar)) return true;
            if (d.IsDescendantOf(TopBar)) return true;
            if (d.IsDescendantOf(JumpOverlay)) return true;
            if (d.IsDescendantOf(HelpOverlay)) return true;
            if (d.IsDescendantOf(StatusBar)) return true;
            if (d.FindAncestorOrSelf<Button>() != null) return true;
            if (d.FindAncestorOrSelf<ScrollBar>() != null) return true;
        }
        return false;
    }

    private void MainWindow_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            (src.IsDescendantOf(Sidebar) || src.IsDescendantOf(TopBar) ||
             src.IsDescendantOf(JumpOverlay) || src.IsDescendantOf(HelpOverlay)))
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (_config.Mode == ViewMode.Comic)
            {
                var delta = e.Delta > 0 ? 0.05 : -0.05;
                _config.ComicWidthRatio = Math.Clamp(_config.ComicWidthRatio + delta, 0.1, 1.0);
                ShowStatus($"宽度比例：{_config.ComicWidthRatio * 100:0}%");
                ScheduleComicWidthUpdate();
            }
            else
            {
                ZoomImageAtCursor(e.Delta > 0 ? 1.15 : 1.0 / 1.15, e.GetPosition(ImagePanel));
            }
            e.Handled = true;
            return;
        }

        if (_config.Mode == ViewMode.Comic && _comicItems.Count > 0)
        {
            var step = _config.ScrollStepPixels > 0 ? _config.ScrollStepPixels : 300;
            var lines = e.Delta / 120.0;
            _scrollVelocity -= step * lines * (1 - ScrollFriction) * 60.0;
            _scrollVelocity = Math.Clamp(_scrollVelocity, -MaxScrollVelocity, MaxScrollVelocity);
            StartScrollAnimation();
            e.Handled = true;
        }
    }

    private void ZoomImageAtCursor(double factor, Point cursorInPanel)
    {
        var oldEffective = _baseFitScale * _imageTempZoom;
        var newTemp = Math.Clamp(_imageTempZoom * factor, 0.2, 8.0);
        if (Math.Abs(newTemp - 1.0) < 0.03) newTemp = 1.0;

        _imageTempZoom = newTemp;
        var newEffective = _baseFitScale * _imageTempZoom;

        if (oldEffective > 0 && newEffective > 1.01)
        {
            var centerX = ImagePanel.ActualWidth / 2.0;
            var centerY = ImagePanel.ActualHeight / 2.0;
            var ox = cursorInPanel.X - centerX;
            var oy = cursorInPanel.Y - centerY;
            var ratio = newEffective / oldEffective;
            ImagePan.X = ox - (ox - ImagePan.X) * ratio;
            ImagePan.Y = oy - (oy - ImagePan.Y) * ratio;
        }
        else
        {
            ImagePan.X = 0;
            ImagePan.Y = 0;
        }

        ImageScale.ScaleX = newEffective;
        ImageScale.ScaleY = newEffective;
        ClampImagePan();
        ShowStatus($"缩放：{newEffective * 100:0}%");
    }

    private void UpdateRenderingSubscription()
    {
        var needHook = _scrollAnimating || _autoScrolling;
        if (needHook && !_renderingHooked)
        {
            _lastScrollTick = DateTime.UtcNow;
            CompositionTarget.Rendering += ScrollAnim_Rendering;
            _renderingHooked = true;
        }
        else if (!needHook && _renderingHooked)
        {
            CompositionTarget.Rendering -= ScrollAnim_Rendering;
            _renderingHooked = false;
        }
    }

    private void StartScrollAnimation()
    {
        if (!_scrollAnimating)
        {
            _scrollAnimating = true;
            _lastScrollTick = DateTime.UtcNow;
            UpdateRenderingSubscription();
        }
    }

    private void SetAutoScroll(bool enabled, bool silent = false)
    {
        if (_autoScrolling == enabled) return;
        _autoScrolling = enabled;
        if (enabled)
            _lastScrollTick = DateTime.UtcNow;
        UpdateRenderingSubscription();

        if (!silent)
            ShowStatus(enabled ? $"自动滚动：开启（{_config.AutoScrollSpeed:0} px/s）" : "自动滚动：已停止");
    }

    private void AdjustAutoScrollSpeed(double delta)
    {
        _config.AutoScrollSpeed = Math.Clamp(_config.AutoScrollSpeed + delta, 20, 3000);
        ConfigService.Save(_config);
        ShowStatus($"自动滚动速度：{_config.AutoScrollSpeed:0} px/s");
    }

    private void NudgeComicScroll(double deltaPixels)
    {
        if (_comicItems.Count == 0) return;
        _scrollVelocity += deltaPixels * (1 - ScrollFriction) * 60.0;
        _scrollVelocity = Math.Clamp(_scrollVelocity, -MaxScrollVelocity, MaxScrollVelocity);
        StartScrollAnimation();
    }

    private void ScrollAnim_Rendering(object? sender, EventArgs e)
    {
        if (!_scrollAnimating && !_autoScrolling)
        {
            UpdateRenderingSubscription();
            return;
        }

        var now = DateTime.UtcNow;
        var dt = (now - _lastScrollTick).TotalSeconds;
        _lastScrollTick = now;
        if (dt <= 0) dt = 1.0 / 60.0;
        if (dt > 0.05) dt = 0.05;

        var current = ComicScroll.VerticalOffset;
        var max = ComicScroll.ExtentHeight - ComicScroll.ViewportHeight;
        if (max < 0) max = 0;

        if (current > max) max = current;
        if (_lastStableMax < 0 || max >= _lastStableMax)
            _lastStableMax = max;
        else
            max = _lastStableMax;

        double delta = 0;
        if (_scrollAnimating)
        {
            delta += _scrollVelocity * dt;
            var friction = Math.Pow(ScrollFriction, dt * 60.0);
            _scrollVelocity *= friction;
            if (Math.Abs(_scrollVelocity) < 1.0)
            {
                _scrollVelocity = 0;
                _scrollAnimating = false;
            }
        }

        if (_autoScrolling)
        {
            delta += _config.AutoScrollSpeed * dt;
        }

        var next = current + delta;
        if (next <= 0)
        {
            next = 0;
            if (_scrollVelocity < 0) _scrollVelocity = 0;
        }
        else if (next >= max)
        {
            next = max;
            if (_scrollVelocity > 0) _scrollVelocity = 0;
            if (_autoScrolling && _heightsComputed)
                SetAutoScroll(false);
        }

        ComicScroll.ScrollToVerticalOffset(next);
        UpdateRenderingSubscription();
    }

    private void ScrollComicByOnePage(int direction = 1)
    {
        var step = ComicScroll.ViewportHeight * 0.95;
        if (step <= 0) step = 300;
        _scrollVelocity += direction * step * (1 - ScrollFriction) * 60.0;
        _scrollVelocity = Math.Clamp(_scrollVelocity, -MaxScrollVelocity, MaxScrollVelocity);
        StartScrollAnimation();
    }

    private bool IsAtComicLastPage()
    {
        if (_comicItems.Count == 0) return true;
        if (!_heightsComputed && _comicItems.Count > 1) return false;
        return ComicScroll.VerticalOffset + ComicScroll.ViewportHeight >= ComicScroll.ExtentHeight - 5;
    }

    private bool IsAtComicFirstPage()
    {
        if (_comicItems.Count == 0) return true;
        return ComicScroll.VerticalOffset <= 5;
    }

    #endregion

    #region 翻页、跳转与文件夹导航

    private void GoNext()
    {
        if (_imageFiles.Count == 0) return;
        if (_config.Mode == ViewMode.Comic)
        {
            if (IsAtComicLastPage())
                _ = GoToNextSiblingFolderAsync();
            else
                ScrollComicByOnePage(1);
            return;
        }

        var step = _config.DualPageMode ? _currentImageCountShown : 1;
        if (_currentImageIndex + step < _imageFiles.Count)
            _ = ShowImageAsync(_currentImageIndex + step, _loadCts.Token);
        else
            _ = GoToNextSiblingFolderAsync();
    }

    private void GoPrev()
    {
        if (_imageFiles.Count == 0) return;
        if (_config.Mode == ViewMode.Comic)
        {
            if (IsAtComicFirstPage())
                _ = GoToPrevSiblingFolderAsync();
            else
                ScrollComicByOnePage(-1);
            return;
        }

        var step = _config.DualPageMode ? 2 : 1;
        if (_currentImageIndex > 0)
            _ = ShowImageAsync(Math.Max(0, _currentImageIndex - step), _loadCts.Token);
        else
            _ = GoToPrevSiblingFolderAsync();
    }

    private void JumpToPageIndex(int index)
    {
        if (_imageFiles.Count == 0) return;
        index = Math.Clamp(index, 0, _imageFiles.Count - 1);

        if (_config.Mode == ViewMode.Comic)
        {
            _scrollVelocity = 0;
            _scrollAnimating = false;
            UpdateRenderingSubscription();
            ScrollComicToIndex(index);
        }
        else
        {
            _ = ShowImageAsync(index, _loadCts.Token);
        }
    }

    private async Task GoToNextSiblingFolderAsync()
    {
        if (_currentFolder == null) return;
        var current = _currentFolder;
        var next = await Task.Run(() => FolderNavigationService.GetNextSiblingFolder(current));
        if (next != null)
        {
            await OpenFolderAsync(next, updateSidebar: false);
            SelectSidebarItem(next);
        }
        else
        {
            ShowStatus("已是最后一个文件夹");
        }
    }

    private async Task GoToPrevSiblingFolderAsync()
    {
        if (_currentFolder == null) return;
        var current = _currentFolder;
        var prev = await Task.Run(() => FolderNavigationService.GetPrevSiblingFolder(current));
        if (prev != null)
        {
            await OpenFolderAsync(prev, updateSidebar: false);
            SelectSidebarItem(prev);
        }
        else
        {
            ShowStatus("已是第一个文件夹");
        }
    }

    #endregion

    #region 快速跳转与快捷键帮助浮层

    private void StatusBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ToggleJumpOverlay();
        e.Handled = true;
    }

    private void ToggleJumpOverlay()
    {
        if (_imageFiles.Count == 0) return;
        if (JumpOverlay.Visibility == Visibility.Visible)
        {
            CloseJumpOverlay();
            return;
        }

        var cur = _config.Mode == ViewMode.Comic ? GetComicCurrentImageIndex() : _currentImageIndex;
        _suppressJumpSlider = true;
        JumpSlider.Minimum = 1;
        JumpSlider.Maximum = Math.Max(1, _imageFiles.Count);
        JumpSlider.Value = cur + 1;
        JumpPageBox.Text = (cur + 1).ToString(CultureInfo.InvariantCulture);
        JumpTotalText.Text = $"/ {_imageFiles.Count} 页";
        _suppressJumpSlider = false;

        JumpOverlay.Visibility = Visibility.Visible;
        JumpPageBox.Focus();
        JumpPageBox.SelectAll();
    }

    private void CloseJumpOverlay() => JumpOverlay.Visibility = Visibility.Collapsed;

    private void JumpSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressJumpSlider || JumpOverlay.Visibility != Visibility.Visible) return;
        var page = (int)Math.Round(e.NewValue);
        JumpPageBox.Text = page.ToString(CultureInfo.InvariantCulture);
        JumpToPageIndex(page - 1);
    }

    private void JumpPageBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitJumpPageBox();
            e.Handled = true;
        }
    }

    private void BtnJumpGo_OnClick(object sender, RoutedEventArgs e) => CommitJumpPageBox();

    private void CommitJumpPageBox()
    {
        if (int.TryParse(JumpPageBox.Text.Trim(), out var page) &&
            page >= 1 && page <= _imageFiles.Count)
        {
            JumpToPageIndex(page - 1);
            CloseJumpOverlay();
        }
        else
        {
            ShowStatus($"请输入 1 ~ {_imageFiles.Count} 之间的页码");
        }
    }

    private void BtnJumpClose_OnClick(object sender, RoutedEventArgs e) => CloseJumpOverlay();

    private void BtnHelp_OnClick(object sender, RoutedEventArgs e) => ToggleHelpOverlay();

    private void ToggleHelpOverlay()
    {
        HelpOverlay.Visibility = HelpOverlay.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void HelpOverlay_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, HelpOverlay))
        {
            HelpOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void BtnHelpClose_OnClick(object sender, RoutedEventArgs e) =>
        HelpOverlay.Visibility = Visibility.Collapsed;

    #endregion

    #region 右键菜单动作

    private void OpenReaderContextMenu()
    {
        if (FindResource("ReaderContextMenu") is ContextMenu menu)
        {
            menu.PlacementTarget = ContentHost;
            menu.IsOpen = true;
        }
    }

    private void ReaderContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        SyncMenuCheckStates(menu.Items);
    }

    private void SyncMenuCheckStates(ItemCollection items)
    {
        foreach (var obj in items)
        {
            if (obj is not MenuItem item) continue;
            switch (item.Tag as string)
            {
                case "FitWindow":
                    item.IsChecked = _config.FitMode == ImageFitMode.FitWindow;
                    break;
                case "FitWidth":
                    item.IsChecked = _config.FitMode == ImageFitMode.FitWidth;
                    break;
                case "ActualSize":
                    item.IsChecked = _config.FitMode == ImageFitMode.ActualSize;
                    break;
                case "DualPage":
                    item.IsChecked = _config.DualPageMode;
                    break;
                case "Rtl":
                    item.IsChecked = _config.RightToLeft;
                    break;
                case "AutoScroll":
                    item.IsChecked = _autoScrolling;
                    break;
            }
            if (item.Items.Count > 0)
                SyncMenuCheckStates(item.Items);
        }
    }

    private string? GetActiveImagePath()
    {
        if (_imageFiles.Count == 0) return null;
        var idx = _config.Mode == ViewMode.Comic ? GetComicCurrentImageIndex() : _currentImageIndex;
        return idx >= 0 && idx < _imageFiles.Count ? _imageFiles[idx] : null;
    }

    private void MenuRevealInExplorer_OnClick(object sender, RoutedEventArgs e)
    {
        var path = GetActiveImagePath() ?? _currentFolder;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch
        {
            ShowStatus("无法打开资源管理器");
        }
    }

    private async void MenuCopyImage_OnClick(object sender, RoutedEventArgs e)
    {
        var path = GetActiveImagePath();
        if (string.IsNullOrEmpty(path)) return;
        var bmp = await ImageLoaderService.LoadAsync(path);
        if (bmp != null)
        {
            try
            {
                Clipboard.SetImage(bmp);
                ShowStatus("已复制图片到剪贴板");
            }
            catch
            {
                ShowStatus("复制图片失败");
            }
        }
    }

    private void MenuCopyPath_OnClick(object sender, RoutedEventArgs e)
    {
        var path = GetActiveImagePath() ?? _currentFolder;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Clipboard.SetText(path);
            ShowStatus("已复制文件路径");
        }
        catch
        {
            ShowStatus("复制路径失败");
        }
    }

    private void MenuFitWindow_OnClick(object sender, RoutedEventArgs e) => SetImageFitMode(ImageFitMode.FitWindow);
    private void MenuFitWidth_OnClick(object sender, RoutedEventArgs e) => SetImageFitMode(ImageFitMode.FitWidth);
    private void MenuActualSize_OnClick(object sender, RoutedEventArgs e) => SetImageFitMode(ImageFitMode.ActualSize);
    private void MenuRotateLeft_OnClick(object sender, RoutedEventArgs e) => RotateImage(-90);
    private void MenuRotateRight_OnClick(object sender, RoutedEventArgs e) => RotateImage(90);
    private void MenuRotateReset_OnClick(object sender, RoutedEventArgs e)
    {
        _imageRotation = 0;
        ResetImageZoomAndPan();
    }
    private void MenuDualPage_OnClick(object sender, RoutedEventArgs e) => _ = ToggleDualPageModeAsync();
    private void MenuRtl_OnClick(object sender, RoutedEventArgs e) => _ = ToggleRightToLeftAsync();
    private void MenuAutoScroll_OnClick(object sender, RoutedEventArgs e)
    {
        if (_config.Mode == ViewMode.Comic)
            SetAutoScroll(!_autoScrolling);
        else
            ShowStatus("自动滚动仅在漫画模式下可用");
    }
    private void MenuJumpPage_OnClick(object sender, RoutedEventArgs e) => ToggleJumpOverlay();
    private void MenuShowHelp_OnClick(object sender, RoutedEventArgs e) => ToggleHelpOverlay();

    #endregion

    #region 侧栏文件目录（异步枚举与计数）

    private async Task BuildSidebarFolderListAsync()
    {
        _sidebarCts.Cancel();
        _sidebarCts.Dispose();
        _sidebarCts = new CancellationTokenSource();
        var ct = _sidebarCts.Token;

        var targetFolder = _sidebarFolder;
        SidebarTitle.Text = targetFolder == null ? "计算机" : GetDisplayName(targetFolder);
        BtnUp.IsEnabled = targetFolder != null;

        List<string> dirs;
        try
        {
            dirs = await Task.Run(() => targetFolder == null
                ? FolderNavigationService.ListLogicalDrives()
                : FolderNavigationService.ListSubFolders(targetFolder), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested) return;

        var entries = dirs.Select(dir => new FolderListEntry
        {
            Path = dir,
            Name = GetDisplayName(dir),
            CountText = "",
            IsCurrent = _currentFolder != null &&
                string.Equals(dir, _currentFolder, StringComparison.OrdinalIgnoreCase),
        }).ToList();

        _folderEntries = entries;
        FolderListBox.ItemsSource = _folderEntries;
        SelectSidebarItem(_selectedPath);

        if (targetFolder != null && entries.Count > 0)
            _ = PopulateSidebarCountsAsync(entries, ct);
    }

    private static async Task PopulateSidebarCountsAsync(List<FolderListEntry> entries, CancellationToken ct)
    {
        const int batchSize = 24;
        for (var start = 0; start < entries.Count; start += batchSize)
        {
            if (ct.IsCancellationRequested) return;
            var batch = entries.Skip(start).Take(batchSize).ToList();
            int[] counts;
            try
            {
                counts = await Task.Run(() =>
                {
                    var res = new int[batch.Count];
                    for (var i = 0; i < batch.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        res[i] = FolderNavigationService.CountImages(batch[i].Path);
                    }
                    return res;
                }, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested) return;
            for (var i = 0; i < batch.Count; i++)
                batch[i].CountText = counts[i] > 0 ? counts[i].ToString(CultureInfo.InvariantCulture) : "";
        }
    }

    private void UpdateSidebarCurrentHighlight()
    {
        foreach (var entry in _folderEntries)
        {
            entry.IsCurrent = _currentFolder != null &&
                string.Equals(entry.Path, _currentFolder, StringComparison.OrdinalIgnoreCase);
        }
    }

    private int _sidebarClickEpoch;

    private void FolderListBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        // 若点在滚动条上，交给滚动条自身处理。
        if (d.FindAncestorOrSelf<ScrollBar>() != null) return;

        var container = d.FindAncestorOrSelf<ListBoxItem>()
            ?? ItemsControl.ContainerFromElement(FolderListBox, d) as ListBoxItem;
        if (container?.DataContext is not FolderListEntry entry) return;

        e.Handled = true;

        if (e.ClickCount >= 2)
        {
            unchecked { _sidebarClickEpoch++; }
            _selectedPath = null;
            FolderListBox.SelectedItem = null;
            _sidebarFolder = entry.Path;
            _ = BuildSidebarFolderListAsync();
            return;
        }

        FolderListBox.SelectedItem = entry;
        _selectedPath = entry.Path;
        _ = OnSidebarFolderClickAsync(entry.Path);
    }

    private void SelectSidebarItem(string? path)
    {
        _selectedPath = path;
        FolderListEntry? match = null;
        if (path != null)
        {
            match = _folderEntries.Find(x =>
                string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        }
        FolderListBox.SelectedItem = match;
        if (match != null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(FolderListBox.SelectedItem, match))
                    FolderListBox.ScrollIntoView(match);
            }, DispatcherPriority.Loaded);
        }
    }

    private async Task OnSidebarFolderClickAsync(string path)
    {
        var clickEpoch = unchecked(++_sidebarClickEpoch);
        try
        {
            if (!Directory.Exists(path)) return;
            var images = await Task.Run(() => FolderNavigationService.ListImages(path));
            if (clickEpoch != _sidebarClickEpoch) return;
            if (images.Count > 0)
                await OpenFolderAsync(path, updateSidebar: false, prelistedImages: images);
        }
        catch (Exception ex)
        {
            ShowStatus($"打开目录失败：{ex.Message}");
        }
    }

    private static string GetDisplayName(string path)
    {
        try
        {
            if (path.Length == 3 && path.EndsWith(":\\"))
                return path;
            return Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }

    private void BtnUp_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sidebarFolder == null) return;
        var child = _sidebarFolder;
        var parent = Directory.GetParent(_sidebarFolder);
        _sidebarFolder = parent?.FullName;
        _selectedPath = child;
        _ = BuildSidebarFolderListAsync();
    }

    private void MainWindow_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_imagePanning)
        {
            var now = e.GetPosition(this);
            var dx = now.X - _panLast.X;
            var dy = now.Y - _panLast.Y;
            if (!_imageDragMoved &&
                (Math.Abs(now.X - _panStart.X) > 4 || Math.Abs(now.Y - _panStart.Y) > 4))
            {
                _imageDragMoved = true;
            }
            ImagePan.X += dx;
            ImagePan.Y += dy;
            ClampImagePan();
            _panLast = now;
        }

        if (!_isFullscreen) return;

        var pos = e.GetPosition(this);
        if (pos.X <= 40)
        {
            Sidebar.Visibility = Visibility.Visible;
            _sidebarHovering = true;
        }
        else if (pos.X > 280)
        {
            _sidebarHovering = false;
        }

        if (pos.Y <= 40)
        {
            TopBar.Visibility = Visibility.Visible;
            _topBarHovering = true;
        }
        else if (pos.Y > 80)
        {
            _topBarHovering = false;
        }
    }

    private void SidebarHideTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isFullscreen) return;

        if (!_sidebarHovering && Sidebar.Visibility == Visibility.Visible)
            Sidebar.Visibility = Visibility.Collapsed;

        if (!_topBarHovering && TopBar.Visibility == Visibility.Visible)
            TopBar.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region 拖放打开与设置窗口

    private async void MainWindow_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;

        var path = files[0];
        if (Directory.Exists(path))
        {
            await OpenFolderAsync(path, updateSidebar: true);
        }
        else if (File.Exists(path))
        {
            if (!FolderNavigationService.IsImageFile(path))
            {
                ShowStatus("不支持的文件格式");
                return;
            }
            var dir = Path.GetDirectoryName(path);
            if (dir != null)
                await OpenFolderAsync(dir, updateSidebar: true, startFile: path);
        }
    }

    private static bool IsGifPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    private async void BtnSettings_OnClick(object sender, RoutedEventArgs e)
    {
        var oldBg = _config.BackgroundColor;
        var oldDual = _config.DualPageMode;
        var oldRtl = _config.RightToLeft;
        var oldFit = _config.FitMode;

        var win = new SettingsWindow(_config) { Owner = this };
        if (win.ShowDialog() != true) return;

        ConfigService.Save(_config);
        if (!string.Equals(oldBg, _config.BackgroundColor, StringComparison.OrdinalIgnoreCase))
            ApplyBackgroundBrush();

        if (_config.Mode == ViewMode.Comic)
        {
            await ApplyComicSettingsAsync();
        }
        else if (_imageFiles.Count > 0)
        {
            if (oldDual != _config.DualPageMode || oldRtl != _config.RightToLeft)
                await ShowImageAsync(_currentImageIndex, _loadCts.Token);
            else if (oldFit != _config.FitMode)
                ApplyImageFitAndTransform();
        }
    }

    #endregion
}

/// <summary>侧栏本层目录项（支持异步计数更新与当前目录高亮同步）。</summary>
internal sealed class FolderListEntry : INotifyPropertyChanged
{
    private string _countText = "";
    private bool _isCurrent;

    public string Path { get; init; } = "";
    public string Name { get; init; } = "";

    public string CountText
    {
        get => _countText;
        set
        {
            if (_countText == value) return;
            _countText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>漫画页加载状态，防止竞态重复解码与坏图死循环重试。</summary>
public enum ComicLoadState
{
    Idle = 0,
    Loading = 1,
    Loaded = 2,
    Failed = 3,
    Gif = 4,
}

/// <summary>漫画模式绑定的图片项。</summary>
public sealed class ComicImageItem : INotifyPropertyChanged
{
    private ImageSource? _source;
    private double _displayWidth;
    private double _displayHeight;
    private Thickness _itemMargin = new(0, 2, 0, 2);
    private bool _isActive;

    public string Path { get; set; } = "";
    public ComicLoadState State { get; set; } = ComicLoadState.Idle;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public double DisplayWidth
    {
        get => _displayWidth;
        set
        {
            if (Math.Abs(_displayWidth - value) < 0.01) return;
            _displayWidth = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayWidth)));
        }
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        set
        {
            if (Math.Abs(_displayHeight - value) < 0.01) return;
            _displayHeight = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayHeight)));
        }
    }

    public Thickness ItemMargin
    {
        get => _itemMargin;
        set
        {
            if (_itemMargin == value) return;
            _itemMargin = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemMargin)));
        }
    }

    public ImageSource? Source
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value)) return;
            _source = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
