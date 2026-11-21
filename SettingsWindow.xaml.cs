using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ComicViewer.Models;
using ComicViewer.Services;

namespace ComicViewer;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _target;
    private readonly AppConfig _working;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _target = config;
        // 在副本上编辑，取消时不改主窗口正在用的配置。
        _working = config.Clone();
        _working.Sanitize();

        WidthSlider.Value = _working.ComicWidthRatio;
        PageGapBox.Text = _working.ComicPageGap.ToString("0", CultureInfo.InvariantCulture);
        SelectComboByTag(BgColorCombo, _working.BackgroundColor, "#1a1a1a");

        ScrollStepBox.Text = _working.ScrollStepPixels.ToString("0", CultureInfo.InvariantCulture);
        AutoScrollSpeedBox.Text = _working.AutoScrollSpeed.ToString("0", CultureInfo.InvariantCulture);
        PreloadUpBox.Text = _working.PreloadUpCount.ToString(CultureInfo.InvariantCulture);
        PreloadDownBox.Text = _working.PreloadDownCount.ToString(CultureInfo.InvariantCulture);

        SelectComboByTag(FitModeCombo, _working.FitMode.ToString(), nameof(ImageFitMode.FitWindow));
        DualPageCheck.IsChecked = _working.DualPageMode;
        RtlCheck.IsChecked = _working.RightToLeft;

        FullscreenCheck.IsChecked = _working.StartFullscreen;
        TruncateCheck.IsChecked = _working.TruncateBeforeStartFile;
        ConfirmDeleteCheck.IsChecked = _working.ConfirmBeforeDelete;

        UpdateWidthLabel();
        RefreshAssocStatus();
    }

    private static void SelectComboByTag(ComboBox combo, string? value, string fallback)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, fallback, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private void WidthSlider_OnValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateWidthLabel();
        _working.ComicWidthRatio = WidthSlider.Value;
    }

    private void UpdateWidthLabel()
    {
        if (WidthLabel == null) return;
        WidthLabel.Text = $"{WidthSlider.Value * 100:0}%";
    }

    private void RefreshAssocStatus()
    {
        var (registered, pathMatches) = FileAssociationService.GetRegistrationState();
        if (registered && pathMatches)
        {
            AssocStatus.Text = "已写入本机关联条目（路径匹配当前程序）。系统默认应用仍需在 Windows 设置里手动选择。";
        }
        else if (registered)
        {
            AssocStatus.Text = "检测到已注册的关联条目指向旧路径（便携程序可能已移动），请重新点击「关联图片类型」更新路径。";
        }
        else
        {
            AssocStatus.Text = "尚未注册。注册后会出现在系统「默认应用」候选列表中。";
        }
        BtnUnassoc.IsEnabled = registered;
    }

    private void BtnSave_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryParseDoubleInRange(PageGapBox.Text, 0, 40, out var gap))
        {
            ShowValidationError("漫画页面间距需为 0 ~ 40 之间的数字。");
            PageGapBox.Focus();
            return;
        }
        if (!TryParseDoubleInRange(ScrollStepBox.Text, 50, 5000, out var stepPx))
        {
            ShowValidationError("滚轮滚动步长需为 50 ~ 5000 之间的数字。");
            ScrollStepBox.Focus();
            return;
        }
        if (!TryParseDoubleInRange(AutoScrollSpeedBox.Text, 20, 3000, out var autoSpeed))
        {
            ShowValidationError("自动滚动速度需为 20 ~ 3000 之间的数字。");
            AutoScrollSpeedBox.Focus();
            return;
        }
        if (!TryParseIntInRange(PreloadUpBox.Text, 0, 50, out var up))
        {
            ShowValidationError("向上预加载页数需为 0 ~ 50 之间的整数。");
            PreloadUpBox.Focus();
            return;
        }
        if (!TryParseIntInRange(PreloadDownBox.Text, 0, 50, out var down))
        {
            ShowValidationError("向下预加载页数需为 0 ~ 50 之间的整数。");
            PreloadDownBox.Focus();
            return;
        }

        _working.ComicWidthRatio = WidthSlider.Value;
        _working.ComicPageGap = gap;
        _working.BackgroundColor = (BgColorCombo.SelectedValue as string) ?? "#1a1a1a";
        _working.ScrollStepPixels = stepPx;
        _working.AutoScrollSpeed = autoSpeed;
        _working.PreloadUpCount = up;
        _working.PreloadDownCount = down;

        if (FitModeCombo.SelectedValue is string fitTag &&
            Enum.TryParse<ImageFitMode>(fitTag, out var fitMode))
        {
            _working.FitMode = fitMode;
        }

        _working.DualPageMode = DualPageCheck.IsChecked ?? false;
        _working.RightToLeft = RtlCheck.IsChecked ?? false;
        _working.StartFullscreen = FullscreenCheck.IsChecked ?? true;
        _working.TruncateBeforeStartFile = TruncateCheck.IsChecked ?? false;
        _working.ConfirmBeforeDelete = ConfirmDeleteCheck.IsChecked ?? false;

        _target.CopyFrom(_working);
        DialogResult = true;
        Close();
    }

    private static bool TryParseDoubleInRange(string? text, double min, double max, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        !double.IsNaN(value) && !double.IsInfinity(value) &&
        value >= min && value <= max;

    private static bool TryParseIntInRange(string? text, int min, int max, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
        value >= min && value <= max;

    private void ShowValidationError(string message) =>
        MessageBox.Show(this, message, "参数无效", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void BtnCancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnAssoc_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var opened = FileAssociationService.RegisterAndOpenSettings();
            RefreshAssocStatus();
            var msg = opened
                ? "已注册图片类型关联。\n请在打开的「默认应用」列表中选择 ComicViewer 作为默认图片查看器。"
                : "已注册图片类型关联，但未能自动打开 Windows 设置页。\n请手动前往「设置 → 应用 → 默认应用」选择 ComicViewer。";
            MessageBox.Show(this, msg, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            RefreshAssocStatus();
            MessageBox.Show(this, $"关联失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnUnassoc_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociationService.Unregister();
            RefreshAssocStatus();
            MessageBox.Show(this,
                "已移除本软件写入的图片关联条目。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"取消关联失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
