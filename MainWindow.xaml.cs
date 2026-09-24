using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ComicViewer.Models;
using ComicViewer.Services;

namespace ComicViewer;

/// <summary>
/// 主窗口：实现两种查看模式（漫画/图片）、全屏、左侧文件目录树、
/// 末页自动跳转下一文件夹等核心需求。
/// </summary>
public partial class MainWindow : Window
{
    private AppConfig _config = null!;
    private string? _currentFolder;      // 当前查看图片的文件夹
    private string? _sidebarFolder;     // 侧栏当前显示的目录（null=磁盘根）
    private List<string> _imageFiles = [];
    private int _currentImageIndex;

    // 漫画模式下的图片源数据列表（绑到 ItemsControl）。
    private readonly ObservableCollection<ComicImageItem> _comicItems = [];

    // 图片模式：临时缩放（按住右键滚轮），不记忆。
    private double _imageTempZoom = 1.0;

    // 漫画模式：平滑滚动（惯性滚动速度，像素/帧）
    private double _scrollVelocity;
    private bool _scrollAnimating;
    private DateTime _lastScrollTick;

    // 惯性滚动参数：摩擦系数（按 60fps 基准每帧速度保留比例）、速度上限（像素/秒）。
    private const double ScrollFriction = 0.90;
    private const double MaxScrollVelocity = 120000;

    // 侧栏单击选中高亮：记录当前选中项，用于单击视觉反馈。
    private string? _selectedPath;
    private Border? _selectedBorder;

    // 路径 → 目录项 Border 映射，便于按路径定位并高亮（返回上一级、末页跳转同步选中）。
    private readonly Dictionary<string, Border> _sidebarItems = new(StringComparer.OrdinalIgnoreCase);

    // 侧栏/顶栏自动隐藏：鼠标位置定时器。
    private DispatcherTimer? _sidebarHideTimer;
    private bool _sidebarHovering;
    private bool _topBarHovering;

    public MainWindow()
    {
        InitializeComponent();
        ComicList.ItemsSource = _comicItems;
    }

    #region 启动

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _config = ConfigService.Load();

        // 启动侧栏隐藏定时器（全屏时鼠标移开自动隐藏）
        _sidebarHideTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Background,
            SidebarHideTimer_Tick,
            Dispatcher);
        _sidebarHideTimer.Start();

        // 平滑滚动动画：与屏幕渲染帧同步（CompositionTarget.Rendering）
        _scrollVelocity = 0;
        _scrollAnimating = false;
        CompositionTarget.Rendering += ScrollAnim_Rendering;

        // 应用初始全屏状态
        if (_config.StartFullscreen)
            EnterFullscreen();
        else
            ExitFullscreen();

        // 根据配置应用模式
        ApplyMode(_config.Mode, false);

        // 从命令行参数获取打开的文件（文件关联调用）
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            var folder = Path.GetDirectoryName(args[1]);
            if (folder != null)
            {
                // 阅读图片所在目录，但左侧目录显示其「上一级」（父目录），
                // 并高亮当前目录，而非直接钻入。
                await OpenFolderAsync(folder, updateSidebar: false);
                _sidebarFolder = Directory.GetParent(folder)?.FullName;
                _selectedPath = folder;
                BuildSidebarFolderList();
                SelectSidebarItem(folder);

                var file = args[1];
                var idx = _imageFiles.FindIndex(f =>
                    string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    _currentImageIndex = idx;
                    if (_config.Mode == ViewMode.Image)
                        await ShowImageAsync(idx);
                }
                return;
            }
        }

        // 无文件夹：侧栏显示计算机磁盘根目录
        _sidebarFolder = null;
        BuildSidebarFolderList();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        _sidebarHideTimer?.Stop();
        CompositionTarget.Rendering -= ScrollAnim_Rendering;
        ConfigService.Save(_config);
    }

    #endregion

    #region 全屏切换

    private bool _isFullscreen;

    private void EnterFullscreen()
    {
        _isFullscreen = true;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        // 全屏时顶栏/侧栏默认隐藏（鼠标移至顶部/左侧出现）
        TopBar.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        Sidebar.Visibility = Visibility.Collapsed;
        _topBarHovering = false;
        _sidebarHovering = false;

        // 窗口宽度变化，布局完成后仅等比调整显示尺寸（不重新解码）。
        if (_config.Mode == ViewMode.Comic && _comicItems.Count > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                (Action)ResizeComicDisplay);
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = WindowState.Normal;
        // 非全屏时顶栏/侧栏默认展示；状态栏按内容显示
        TopBar.Visibility = Visibility.Visible;
        Sidebar.Visibility = Visibility.Visible;
        if (_imageFiles.Count > 0)
            StatusBar.Visibility = Visibility.Visible;

        // 窗口宽度变化，布局完成后仅等比调整显示尺寸（不重新解码）。
        if (_config.Mode == ViewMode.Comic && _comicItems.Count > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                (Action)ResizeComicDisplay);
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void TopBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 非全屏时允许拖动窗口
        if (!_isFullscreen)
            DragMove();
    }

    private void MainWindow_OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isFullscreen) ExitFullscreen();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.PageUp:
                GoPrev();
                e.Handled = true;
                break;
            case Key.Right:
            case Key.PageDown:
            case Key.Space:
                GoNext();
                e.Handled = true;
                break;
            case Key.Enter:
                // 回车键等同于鼠标左键：漫画翻一页 / 图片下一张（末页进入下一本）。
                HandleLeftClickAction();
                e.Handled = true;
                break;
        }
    }

    #endregion

    #region 模式切换

    private void BtnToggleMode_OnClick(object sender, RoutedEventArgs e) =>
        _ = ToggleModeAsync(_config.Mode == ViewMode.Comic ? ViewMode.Image : ViewMode.Comic);

    /// <summary>
    /// 切换漫画/图片模式，并保持当前查看的同一页（同一张图片）。
    /// 例：漫画模式看到第 8 张，切到图片模式仍显示第 8 张，反之亦然。
    /// </summary>
    private async Task ToggleModeAsync(ViewMode toMode)
    {
        // 记录切换前当前看到的图片索引（漫画模式由滚动偏移换算，图片模式直接用索引）
        int currentIndex = _config.Mode == ViewMode.Comic
            ? GetComicCurrentImageIndex()
            : _currentImageIndex;

        ApplyMode(toMode, true);

        if (_imageFiles.Count == 0) return;

        if (toMode == ViewMode.Comic)
        {
            // 漫画项列表缺失（此前一直在图片模式）时先重建，再定位到当前页
            if (_comicItems.Count != _imageFiles.Count)
                await LoadComicModeAsync(currentIndex);
            else
                ScrollComicToIndex(currentIndex);
        }
        else
        {
            _currentImageIndex = currentIndex;
            await ShowImageAsync(currentIndex);
        }
    }

    private void ApplyMode(ViewMode mode, bool save)
    {
        _config.Mode = mode;
        // 按钮显示"点击后切换到的目标模式"：漫画模式时显示"图片模式"，反之亦然。
        BtnToggleMode.Content = mode == ViewMode.Comic ? "图片模式" : "漫画模式";

        bool showComic = mode == ViewMode.Comic;
        ComicScroll.Visibility = showComic ? Visibility.Visible : Visibility.Collapsed;
        ImagePanel.Visibility = showComic ? Visibility.Collapsed : Visibility.Visible;

        if (save) ConfigService.Save(_config);

        // 重置临时缩放
        _imageTempZoom = 1.0;
        ImageScale.ScaleX = 1.0;
        ImageScale.ScaleY = 1.0;
    }

    #endregion

    #region 打开文件夹

    /// <summary>
    /// 打开文件夹用于查看图片（右侧显示）。<paramref name="updateSidebar"/> 控制是否
    /// 同步更新左侧侧栏位置（单击查看时传 false，避免改变左侧列表）。
    /// </summary>
    private async Task OpenFolderAsync(string folder, bool updateSidebar = true)
    {
        _currentFolder = folder;
        _imageFiles = FolderNavigationService.ListImages(folder);

        // 更换内容前先断开滚动监听并归零滚动位置，避免旧滚动偏移（常在末页）
        // 在清空/重建期间触发 ScrollChanged，把新漫画误加载到末尾再跳回开头。
        ComicScroll.ScrollChanged -= ComicScroll_OnScrollChanged;
        ComicScroll.ScrollToVerticalOffset(0);

        _comicItems.Clear();
        _currentImageIndex = 0;
        _comicCurrentPage = 0;

        // 侧栏是否跟随到当前文件夹
        if (updateSidebar)
        {
            _sidebarFolder = folder;
            BuildSidebarFolderList();
        }

        if (_imageFiles.Count == 0)
        {
            StatusText.Text = "无图片";
            if (!_isFullscreen)
                StatusBar.Visibility = Visibility.Visible;
            return;
        }

        if (_config.Mode == ViewMode.Comic)
        {
            await LoadComicModeAsync();
        }
        else
        {
            await ShowImageAsync(0);
        }
    }

    /// <summary>
    /// 漫画模式：异步加载图片项。正常打开时立即显示首页，其余尺寸/图片在后台
    /// 渐进补齐（边看边加载），避免黑屏等待。
    /// </summary>
    private async Task LoadComicModeAsync(int startIndex = 0)
    {
        // 加载/重建期间断开滚动监听，防止旧的滚动偏移触发乱序加载。
        ComicScroll.ScrollChanged -= ComicScroll_OnScrollChanged;
        _comicItems.Clear();
        var displayWidth = GetComicDisplayWidth();
        // 解码宽度取显示宽度的 1.5 倍，保证缩放后仍清晰
        var decodeWidth = (int)(displayWidth * 1.5);
        if (decodeWidth < 50) decodeWidth = 50;

        // 创建占位项，并统一设置显示宽度
        foreach (var path in _imageFiles)
        {
            _comicItems.Add(new ComicImageItem
            {
                Path = path,
                DisplayWidth = displayWidth,
            });
        }

        if (startIndex <= 0)
        {
            // 正常打开：只同步解码第一张立即显示，其余图片后台异步补齐，最大程度降低首屏卡顿。
            if (_comicItems.Count > 0)
                await LoadComicItemAsync(0, decodeWidth);

            // 归零滚动位置（清空后默认即顶部），挂上滚动监听按需懒加载。
            ComicScroll.ScrollToVerticalOffset(0);
            ComicScroll.ScrollChanged -= ComicScroll_OnScrollChanged;
            ComicScroll.ScrollChanged += ComicScroll_OnScrollChanged;
            _scrollVelocity = 0;
            _comicCurrentPage = 0;

            // 后台渐进：预读其余图片尺寸 + 加载首屏附近前几张（不阻塞首屏）。
            _ = PrecomputeHeightsAsync(displayWidth);
            _ = LoadNearbyPagesAsync(decodeWidth);
        }
        else
        {
            // 模式切换：需先补齐高度，才能按累计高度准确定位到指定页。
            await PrecomputeHeightsAsync(displayWidth);
            for (var i = startIndex; i < Math.Min(startIndex + 3, _comicItems.Count); i++)
            {
                await LoadComicItemAsync(i, decodeWidth);
            }

            ComicScroll.ScrollChanged -= ComicScroll_OnScrollChanged;
            ComicScroll.ScrollChanged += ComicScroll_OnScrollChanged;
            _scrollVelocity = 0;
            _comicCurrentPage = 0;
            await Dispatcher.InvokeAsync(
                () => ScrollComicToIndex(startIndex), DispatcherPriority.Loaded);
        }
    }

    /// <summary>后台预加载首屏附近的前几张图片（从第 1 张起，不阻塞首屏）。</summary>
    private async Task LoadNearbyPagesAsync(int decodeWidth)
    {
        var count = Math.Max(0, _config.PreloadDownCount) + 1;
        for (var i = 1; i < Math.Min(count, _comicItems.Count); i++)
        {
            await LoadComicItemAsync(i, decodeWidth);
        }
    }

    private int _comicCurrentPage;

    /// <summary>漫画模式相邻图片项的垂直间距（Image 的 Margin="0,2" 上下各 2px）。</summary>
    private const double ItemVerticalGap = 4.0;

    /// <summary>
    /// 根据漫画滚动偏移计算当前看到的图片索引（用于模式切换时保持同一页）。
    /// </summary>
    private int GetComicCurrentImageIndex()
    {
        if (_comicItems.Count == 0) return 0;
        var offset = ComicScroll.VerticalOffset;
        double accum = 0;
        for (var i = 0; i < _comicItems.Count; i++)
        {
            accum += _comicItems[i].DisplayHeight + ItemVerticalGap;
            if (offset < accum) return i;
        }
        return _comicItems.Count - 1;
    }

    /// <summary>滚动漫画列表到指定图片索引处（按累计高度计算像素偏移）。</summary>
    private void ScrollComicToIndex(int index)
    {
        if (_comicItems.Count == 0) return;
        index = Math.Clamp(index, 0, _comicItems.Count - 1);

        ComicScroll.UpdateLayout();
        double offset = 0;
        for (var i = 0; i < index; i++)
            offset += _comicItems[i].DisplayHeight + ItemVerticalGap;

        ComicScroll.ScrollToVerticalOffset(offset);
    }

    /// <summary>
    /// 后台按顺序读取图片尺寸并计算占位高度。逐块（每块 16 张）在后台读取、
    /// 回到 UI 线程应用，使占位高度渐进补齐，避免一次性打开所有文件造成长时间黑屏。
    /// </summary>
    private async Task PrecomputeHeightsAsync(double displayWidth)
    {
        var items = _comicItems.ToList();
        const int chunkSize = 16;
        for (var start = 0; start < items.Count; start += chunkSize)
        {
            var chunk = items.Skip(start).Take(chunkSize).ToList();
            var heights = await Task.Run(() =>
            {
                var result = new double[chunk.Count];
                for (var i = 0; i < chunk.Count; i++)
                {
                    var size = ImageLoaderService.GetPixelSize(chunk[i].Path);
                    if (size.Width > 0 && size.Height > 0)
                        result[i] = displayWidth * size.Height / size.Width;
                }
                return result;
            });

            // 回到 UI 线程（await 后）再应用，保证 PropertyChanged 在主线程触发。
            for (var i = 0; i < chunk.Count; i++)
            {
                if (heights[i] > 0)
                    chunk[i].DisplayHeight = heights[i];
            }
        }
    }

    /// <summary>解码第 idx 张图片并设置 Source 与显示高度（按原始宽高比）。</summary>
    private async Task LoadComicItemAsync(int idx, int decodeWidth)
    {
        if (idx < 0 || idx >= _comicItems.Count) return;
        if (_comicItems[idx].Source != null) return;

        var item = _comicItems[idx];
        var bmp = await ImageLoaderService.LoadAsync(item.Path, decodeWidth);
        if (bmp == null) return;

        // 若占位高度尚未算好，按解码结果补上
        if (item.DisplayHeight <= 0 && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            item.DisplayHeight = item.DisplayWidth * bmp.PixelHeight / bmp.PixelWidth;
        }
        item.Source = bmp;
    }

    private async void ComicScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scroll = ComicScroll;
        if (scroll.ViewportHeight <= 0) return;
        _comicCurrentPage = (int)(scroll.VerticalOffset / scroll.ViewportHeight);

        // 异步加载附近的图片（解码宽度为显示宽度 1.5 倍）
        var decodeWidth = (int)(GetComicDisplayWidth() * 1.5);
        if (decodeWidth < 50) decodeWidth = 50;

        // 窗口中心用「当前可见图片索引」（按图片高度累计），而非「屏页索引」，
        // 因为单张图片高度常大于一屏，用屏页会错位，导致当前可见图被误判为离屏。
        var center = GetComicCurrentImageIndex();
        var up = Math.Max(0, _config.PreloadUpCount);
        var down = Math.Max(0, _config.PreloadDownCount);
        var first = Math.Max(0, center - up);
        var last = Math.Min(_comicItems.Count - 1, center + down);

        // 先回收离屏图片，再并发加载窗口内缺失的图片（不逐张 await 排队）。
        ReleaseDistantComicSources(first, last);

        var tasks = new List<Task>();
        for (var i = first; i <= last; i++)
        {
            var item = _comicItems[i];
            if (item.Source == null && item.Loading == false)
            {
                item.Loading = true;
                tasks.Add(LoadComicItemAsync(i, decodeWidth));
            }
        }

        // 并发加载，全部完成后统一复位 Loading 标志。
        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
            for (var i = first; i <= last; i++)
                _comicItems[i].Loading = false;
        }
    }

    /// <summary>
    /// 释放预加载窗口之外的已解码图片源（Source 置 null），仅保留当前可见图片附近，
    /// 使 WPF 回收非托管解码内存，降低内存占用。
    /// </summary>
    private void ReleaseDistantComicSources(int first, int last)
    {
        for (var i = 0; i < _comicItems.Count; i++)
        {
            if (i < first || i > last)
                _comicItems[i].Source = null;
        }
    }

    /// <summary>
    /// 计算漫画模式图片显示宽度（像素）= 窗口宽度 × 比例。
    /// 例：窗口 1920px、比例 50% → 图片以 960px 宽度显示。
    /// </summary>
    private double GetComicDisplayWidth()
    {
        // ActualWidth 在 Loaded 后有效；初值用主屏宽度兜底
        var w = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        var ratio = Math.Clamp(_config.ComicWidthRatio, 0.1, 1.0);
        return w * ratio;
    }

    /// <summary>
    /// 重新应用漫画显示宽度（宽度比例变化，如设置保存、Ctrl+滚轮调宽度）。
    /// 保留已有图片先等比缩放（避免黑屏），再后台按新分辨率重新解码当前页附近
    /// 以保持清晰度。
    /// </summary>
    private async Task ApplyComicWidthAsync()
    {
        var displayWidth = GetComicDisplayWidth();
        var decodeWidth = (int)(displayWidth * 1.5);
        if (decodeWidth < 50) decodeWidth = 50;

        // 记录当前页，缩放后重新定位回同一页。
        var currentIndex = GetComicCurrentImageIndex();

        // 更新显示宽度（保留 Source，不丢图片，避免黑屏；Image 会等比拉伸）。
        foreach (var item in _comicItems)
        {
            item.DisplayWidth = displayWidth;
            item.Loading = false;
        }

        // 按新宽度重算占位高度。
        await PrecomputeHeightsAsync(displayWidth);

        // 后台按新分辨率重新解码当前页附近，替换为更清晰版本。
        // 先置空旧 Source，避免新旧 BitmapSource 同时驻留造成内存峰值。
        for (var i = Math.Max(0, currentIndex - 2);
             i <= Math.Min(_comicItems.Count - 1, currentIndex + 3);
             i++)
        {
            var item = _comicItems[i];
            item.Source = null;
            var bmp = await ImageLoaderService.LoadAsync(item.Path, decodeWidth);
            if (bmp != null)
                item.Source = bmp;
        }

        // 布局稳定后回到当前页。
        await Dispatcher.InvokeAsync(
            () => ScrollComicToIndex(currentIndex), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 窗口尺寸变化（如切换全屏）时，仅等比调整已加载图片的显示尺寸，不重新解码，
    /// 避免切换全屏时图片消失；未加载的页保持懒加载，滚动时异步补齐。
    /// </summary>
    private void ResizeComicDisplay()
    {
        if (_comicItems.Count == 0) return;

        // 记录当前页，缩放后重新定位回同一页。
        var currentIndex = GetComicCurrentImageIndex();
        var newWidth = GetComicDisplayWidth();

        foreach (var item in _comicItems)
        {
            var oldWidth = item.DisplayWidth;
            if (oldWidth <= 0) oldWidth = newWidth;
            var scale = newWidth / oldWidth;
            item.DisplayHeight *= scale;
            item.DisplayWidth = newWidth;
        }

        // 布局稳定后回到当前页（等比缩放后累计高度变化，需重新定位）。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            (Action)(() => ScrollComicToIndex(currentIndex)));
    }

    #endregion

    #region 图片模式

    private async Task ShowImageAsync(int index)
    {
        if (index < 0 || index >= _imageFiles.Count) return;
        _currentImageIndex = index;

        var path = _imageFiles[index];
        var bmp = await ImageLoaderService.LoadAsync(path, 0);
        SingleImage.Source = bmp;
        _imageTempZoom = 1.0;
        ImageScale.ScaleX = 1.0;
        ImageScale.ScaleY = 1.0;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_imageFiles.Count > 0 && _currentImageIndex < _imageFiles.Count)
        {
            var name = Path.GetFileName(_imageFiles[_currentImageIndex]);
            StatusText.Text = $"{_currentImageIndex + 1}/{_imageFiles.Count}  {name}";
            if (!_isFullscreen)
                StatusBar.Visibility = Visibility.Visible;
        }
    }

    #endregion

    #region 鼠标交互（点击翻页、缩放、宽度调节）

    /// <summary>
    /// 漫画模式左键：滚一页；已到末页 → 下一文件夹。
    /// 图片模式左键：下一张；已末张 → 下一文件夹。
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsOverInteractiveControl(e)) return;

        HandleLeftClickAction();
        e.Handled = true;
    }

    /// <summary>
    /// 左键动作（漫画翻一页 / 图片下一张，末页进入下一本）。
    /// 供鼠标左键与回车键共用。
    /// </summary>
    private void HandleLeftClickAction()
    {
        if (_config.Mode == ViewMode.Comic)
        {
            // 漫画模式：左键滚一页；末页再点 → 下一文件夹
            if (IsAtComicLastPage())
            {
                _ = GoToNextSiblingFolderAsync();
            }
            else
            {
                ScrollComicByOnePage();
            }
        }
        else
        {
            // 图片模式：左键下一张；末张 → 下一文件夹
            if (_currentImageIndex >= _imageFiles.Count - 1)
            {
                _ = GoToNextSiblingFolderAsync();
            }
            else
            {
                GoNext();
            }
        }
    }

    /// <summary>图片模式右键：上一张；首张 → 上一文件夹。</summary>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (IsOverInteractiveControl(e)) return;

        if (_config.Mode == ViewMode.Image)
        {
            if (_currentImageIndex <= 0)
            {
                _ = GoToPrevSiblingFolderAsync();
            }
            else
            {
                GoPrev();
            }
            e.Handled = true;
        }
    }

    /// <summary>判断点击是否落在交互控件（按钮/滚动条/侧栏/顶栏）上。</summary>
    private bool IsOverInteractiveControl(MouseEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            // 侧栏/顶栏覆盖层内的点击交给各自处理，绝不触发右侧图片区翻页。
            if (d.IsDescendantOf(Sidebar)) return true;
            if (d.IsDescendantOf(TopBar)) return true;
            if (d.FindAncestorOrSelf<Button>() != null) return true;
            if (d.FindAncestorOrSelf<ScrollBar>() != null) return true;
        }
        return false;
    }

    /// <summary>
    /// 滚轮处理：
    /// - Ctrl + 滚轮：漫画模式调宽度（记忆），图片模式临时缩放（不记忆）。
    /// - 普通滚轮：漫画模式加速滚动（模拟浏览器）。
    /// </summary>
    private async void MainWindow_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 鼠标位于左侧目录区/顶栏时，滚轮归属该区域（侧栏自己滚动），不作用于图片区。
        if (e.OriginalSource is DependencyObject src &&
            (src.IsDescendantOf(Sidebar) || src.IsDescendantOf(TopBar)))
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (_config.Mode == ViewMode.Comic)
            {
                var delta = e.Delta > 0 ? 0.05 : -0.05;
                _config.ComicWidthRatio = Math.Clamp(
                    _config.ComicWidthRatio + delta, 0.1, 1.0);
                ConfigService.Save(_config);
                await ApplyComicWidthAsync();
            }
            else
            {
                var delta = e.Delta > 0 ? 0.1 : -0.1;
                _imageTempZoom = Math.Clamp(_imageTempZoom + delta, 0.2, 8.0);
                ImageScale.ScaleX = _imageTempZoom;
                ImageScale.ScaleY = _imageTempZoom;
            }
            e.Handled = true;
            return;
        }

        // 漫画模式：惯性滚动（浏览器手感）。滚轮给一个速度，动画按摩擦衰减滑行。
        if (_config.Mode == ViewMode.Comic && _comicItems.Count > 0)
        {
            var step = _config.ScrollStepPixels;
            if (step <= 0) step = 300;
            var lines = e.Delta / 120.0;
            // 每格滚轮累计约滚动 step 的惯性速度（像素/秒），连续滚动可叠加
            _scrollVelocity -= step * lines * (1 - ScrollFriction) * 60.0;
            _scrollVelocity = Math.Clamp(_scrollVelocity, -MaxScrollVelocity, MaxScrollVelocity);
            StartScrollAnimation();
            e.Handled = true;
        }
        // 图片模式：滚轮不滚动，交给默认（无滚动条）
    }

    /// <summary>启动/继续平滑滚动动画。</summary>
    private void StartScrollAnimation()
    {
        if (!_scrollAnimating)
        {
            _scrollAnimating = true;
            _lastScrollTick = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 与屏幕渲染帧同步的惯性滚动。按帧间真实时间差推进速度，
    /// 帧率无关，滚动更顺滑；速度归零即停止。
    /// </summary>
    private void ScrollAnim_Rendering(object? sender, EventArgs e)
    {
        if (!_scrollAnimating) return;

        var now = DateTime.UtcNow;
        var dt = (now - _lastScrollTick).TotalSeconds;
        _lastScrollTick = now;
        // 帧间隔异常大（如窗口卡顿）时限制单帧位移，避免跳变
        if (dt <= 0) dt = 1.0 / 60.0;
        if (dt > 0.05) dt = 0.05;

        // 摩擦系数按 60fps 基准换算到当前帧间隔
        var friction = Math.Pow(ScrollFriction, dt * 60.0);

        var current = ComicScroll.VerticalOffset;
        var max = ComicScroll.ExtentHeight - ComicScroll.ViewportHeight;
        if (max < 0) max = 0;

        // 速度以“像素/秒”为单位，乘以帧间隔得到本帧位移
        var next = current + _scrollVelocity * dt;
        if (next <= 0)
        {
            next = 0;
            if (_scrollVelocity < 0) _scrollVelocity = 0;
        }
        else if (next >= max)
        {
            next = max;
            if (_scrollVelocity > 0) _scrollVelocity = 0;
        }
        ComicScroll.ScrollToVerticalOffset(next);

        // 摩擦衰减
        _scrollVelocity *= friction;
        if (Math.Abs(_scrollVelocity) < 1.0)
        {
            _scrollVelocity = 0;
            _scrollAnimating = false;
        }
    }

    /// <summary>漫画模式：左键平滑滚一页（约一屏高度）。</summary>
    private void ScrollComicByOnePage()
    {
        var step = ComicScroll.ViewportHeight * 0.95;
        if (step <= 0) step = 300;
        // 给一个刚好滑行约一屏的惯性速度（像素/秒）
        _scrollVelocity += step * (1 - ScrollFriction) * 60.0;
        _scrollVelocity = Math.Clamp(_scrollVelocity, -MaxScrollVelocity, MaxScrollVelocity);
        StartScrollAnimation();
    }

    /// <summary>漫画模式：是否已到最后一页（滚动到末尾）。</summary>
    private bool IsAtComicLastPage()
    {
        if (_comicItems.Count == 0) return true;
        return ComicScroll.VerticalOffset + ComicScroll.ViewportHeight
               >= ComicScroll.ExtentHeight - 5;
    }

    #endregion

    #region 翻页与文件夹跳转

    private void GoNext()
    {
        if (_imageFiles.Count == 0) return;
        if (_currentImageIndex < _imageFiles.Count - 1)
        {
            _ = ShowImageAsync(_currentImageIndex + 1);
        }
        else
        {
            _ = GoToNextSiblingFolderAsync();
        }
    }

    private void GoPrev()
    {
        if (_imageFiles.Count == 0) return;
        if (_currentImageIndex > 0)
        {
            _ = ShowImageAsync(_currentImageIndex - 1);
        }
        else
        {
            _ = GoToPrevSiblingFolderAsync();
        }
    }

    private async Task GoToNextSiblingFolderAsync()
    {
        if (_currentFolder == null) return;
        var next = FolderNavigationService.GetNextSiblingFolder(_currentFolder);
        if (next != null)
        {
            // 右侧阅读下一本；左侧目录同步选中该目录并高亮，但不进入（列表不变）。
            await OpenFolderAsync(next, updateSidebar: false);
            SelectSidebarItem(next);
        }
        else
        {
            StatusText.Text = "已是最后一个文件夹";
            if (!_isFullscreen)
                StatusBar.Visibility = Visibility.Visible;
        }
    }

    private async Task GoToPrevSiblingFolderAsync()
    {
        if (_currentFolder == null) return;
        var prev = FolderNavigationService.GetPrevSiblingFolder(_currentFolder);
        if (prev != null)
        {
            await OpenFolderAsync(prev, updateSidebar: false);
            SelectSidebarItem(prev);
        }
        else
        {
            StatusText.Text = "已是第一个文件夹";
            if (!_isFullscreen)
                StatusBar.Visibility = Visibility.Visible;
        }
    }

    #endregion

    #region 侧栏文件目录（本级平铺列表）

    /// <summary>
    /// 构建侧栏目录列表。
    /// _sidebarFolder 为 null 时显示磁盘根；否则显示该目录下的子目录（非隐藏）。
    /// </summary>
    private void BuildSidebarFolderList()
    {
        FolderList.Children.Clear();
        _sidebarItems.Clear();
        // 列表重建后，旧选中项的视觉引用失效，需重置。
        _selectedBorder = null;

        // 标题：显示当前侧栏所在目录
        SidebarTitle.Text = _sidebarFolder == null
            ? "计算机"
            : GetDisplayName(_sidebarFolder);

        // "返回上一级"按钮可用性
        BtnUp.IsEnabled = _sidebarFolder != null;

        List<string> dirs;
        if (_sidebarFolder == null)
        {
            dirs = FolderNavigationService.ListLogicalDrives();
        }
        else
        {
            dirs = FolderNavigationService.ListSubFolders(_sidebarFolder);
        }

        foreach (var dir in dirs)
        {
            var name = GetDisplayName(dir);
            var isCurrent = _currentFolder != null &&
                string.Equals(dir, _currentFolder, StringComparison.OrdinalIgnoreCase);
            var item = CreateFolderListItem(name, dir, isCurrent);
            FolderList.Children.Add(item);
            _sidebarItems[dir] = item;
        }

        // 重建后若有应保持选中的目录，恢复高亮（返回上一级时选中刚离开的子目录）。
        if (_selectedPath != null && _sidebarItems.TryGetValue(_selectedPath, out var selected))
        {
            _selectedBorder = selected;
            _selectedBorder.Background = GetSelectedBrush();
        }
    }

    /// <summary>创建侧栏目录项（支持单击/双击）。</summary>
    private Border CreateFolderListItem(string text, string path, bool isCurrent)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var border = new Border
        {
            Child = tb,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(2, 1, 2, 1),
            Background = isCurrent
                ? new SolidColorBrush(Color.FromRgb(0x33, 0x50, 0x6d))
                : Brushes.Transparent,
            Tag = path,
            Cursor = Cursors.Hand,
        };

        // 单击：选中高亮 + 立即阅读该目录（加载其图片）；双击：进入该目录（侧栏显示其下一级）。
        // 用 MouseLeftButtonDown 检测（ClickCount 可靠），并立刻标记 Handled，
        // 防止事件继续冒泡到窗口而误触右侧翻页。单击立即响应，不再等待双击时间窗。
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;

            if (e.ClickCount >= 2)
            {
                // 双击：进入该目录
                ClearSelection();
                _sidebarFolder = path;
                BuildSidebarFolderList();
                return;
            }

            // 单击：立即高亮选中并尝试阅读
            UpdateSelectedHighlight(border);
            _ = OnSidebarFolderClick(path);
        };

        return border;
    }

    /// <summary>清除侧栏选中高亮。</summary>
    private void ClearSelection()
    {
        if (_selectedBorder != null)
            _selectedBorder.Background = Brushes.Transparent;
        _selectedPath = null;
        _selectedBorder = null;
    }

    /// <summary>按路径选中并高亮侧栏目录项（用于返回上一级、末页跳转同步选中）。</summary>
    private void SelectSidebarItem(string? path)
    {
        // 还原旧选中项背景
        if (_selectedBorder != null && _selectedBorder.Tag is string oldPath &&
            !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _selectedBorder.Background = Brushes.Transparent;
        }

        _selectedPath = path;
        _selectedBorder = null;
        if (path != null && _sidebarItems.TryGetValue(path, out var border))
        {
            _selectedBorder = border;
            _selectedBorder.Background = GetSelectedBrush();
        }
    }

    /// <summary>更新单击选中高亮：还原旧项背景、高亮新项。</summary>
    private void UpdateSelectedHighlight(Border? newBorder)
    {
        if (_selectedBorder != null && !ReferenceEquals(_selectedBorder, newBorder))
            _selectedBorder.Background = Brushes.Transparent;
        _selectedBorder = newBorder;
        _selectedPath = newBorder == null ? null : (string?)newBorder.Tag;
        if (newBorder != null)
            newBorder.Background = GetSelectedBrush();
    }

    /// <summary>选中项高亮背景色。</summary>
    private static Brush GetSelectedBrush() =>
        new SolidColorBrush(Color.FromRgb(0x55, 0x68, 0x88));

    /// <summary>
    /// 侧栏单击目录：尝试阅读该目录（仅查当前目录自身图片，不递归更深子目录）。
    /// 有图片则在右侧显示；无图片则右侧保持不变。
    /// 左侧列表永不因单击而改变，进入目录由双击完成。
    /// </summary>
    private async Task OnSidebarFolderClick(string path)
    {
        if (!Directory.Exists(path)) return;
        // 仅当前目录自身的图片
        var images = FolderNavigationService.ListImages(path);
        if (images.Count > 0)
            await OpenFolderAsync(path, updateSidebar: false);
    }

    /// <summary>获取文件夹显示名（磁盘用 "C:\"，文件夹用名称）。</summary>
    private static string GetDisplayName(string path)
    {
        try
        {
            if (path.Length == 3 && path.EndsWith(":\\"))
                return path; // C:\
            return Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>"返回上一级"按钮：侧栏回到父目录，并高亮选中刚离开的子目录。</summary>
    private void BtnUp_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sidebarFolder == null) return;

        // 记住即将离开的目录，返回上一级后在新列表中高亮它
        var child = _sidebarFolder;
        var parent = Directory.GetParent(_sidebarFolder);
        _sidebarFolder = parent?.FullName;

        // 选中态指向刚离开的子目录（在父级列表中可见）
        _selectedPath = child;
        BuildSidebarFolderList();
    }

    private void MainWindow_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen) return;

        var pos = e.GetPosition(this);

        // 全屏时鼠标移至屏幕左侧（40 像素内）显示侧栏
        if (pos.X <= 40)
        {
            Sidebar.Visibility = Visibility.Visible;
            _sidebarHovering = true;
        }
        else if (pos.X > 280)
        {
            _sidebarHovering = false;
        }

        // 全屏时鼠标移至屏幕顶部（40 像素内）显示顶栏
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

        // 侧栏：鼠标离开左侧区域后隐藏
        if (!_sidebarHovering && Sidebar.Visibility == Visibility.Visible)
        {
            Sidebar.Visibility = Visibility.Collapsed;
        }

        // 顶栏：鼠标离开顶部区域后隐藏
        if (!_topBarHovering && TopBar.Visibility == Visibility.Visible)
        {
            TopBar.Visibility = Visibility.Collapsed;
        }
    }

    #endregion

    #region 拖放打开

    private async void MainWindow_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
            {
                var path = files[0];
                if (Directory.Exists(path))
                    await OpenFolderAsync(path);
                else if (File.Exists(path))
                {
                    var dir = Path.GetDirectoryName(path);
                    if (dir != null)
                    {
                        await OpenFolderAsync(dir);
                        var idx = _imageFiles.FindIndex(f =>
                            string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0)
                            _ = ShowImageAsync(idx);
                    }
                }
            }
        }
    }

    #endregion

    #region 设置窗口

    private void BtnSettings_OnClick(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_config)
        {
            Owner = this,
        };
        win.ShowDialog();
        if (win.DialogResult == true)
        {
            ConfigService.Save(_config);
            if (_config.Mode == ViewMode.Comic)
                _ = ApplyComicWidthAsync();
        }
    }

    #endregion
}

/// <summary>
/// 漫画模式绑定的图片项。
/// </summary>
public sealed class ComicImageItem : INotifyPropertyChanged
{
    private ImageSource? _source;
    private double _displayWidth;
    private double _displayHeight;

    public string Path { get; set; } = "";
    public bool Loading { get; set; }

    /// <summary>图片显示宽度（像素），所有图片统一宽度。</summary>
    public double DisplayWidth
    {
        get => _displayWidth;
        set
        {
            _displayWidth = value;
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(DisplayWidth)));
        }
    }

    /// <summary>图片显示高度（像素），按原始宽高比由显示宽度计算，用于占位与避免滚动时布局跳动。</summary>
    public double DisplayHeight
    {
        get => _displayHeight;
        set
        {
            _displayHeight = value;
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(DisplayHeight)));
        }
    }

    public ImageSource? Source
    {
        get => _source;
        set
        {
            _source = value;
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(Source)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
