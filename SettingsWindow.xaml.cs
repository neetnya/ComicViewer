using System.Windows;
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
        WidthSlider.Value = _working.ComicWidthRatio;
        FullscreenCheck.IsChecked = _working.StartFullscreen;
        ScrollStepBox.Text = _working.ScrollStepPixels.ToString("0");
        PreloadUpBox.Text = _working.PreloadUpCount.ToString();
        PreloadDownBox.Text = _working.PreloadDownCount.ToString();
        UpdateWidthLabel();
        RefreshAssocStatus();
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
        var on = FileAssociationService.IsRegistered();
        AssocStatus.Text = on
            ? "已写入本机关联条目。系统默认应用仍需在 Windows 设置里手动选择。"
            : "尚未注册。注册后会出现在系统「默认应用」候选列表中。";
        BtnUnassoc.IsEnabled = on;
    }

    private void BtnSave_OnClick(object sender, RoutedEventArgs e)
    {
        _working.ComicWidthRatio = WidthSlider.Value;
        _working.StartFullscreen = FullscreenCheck.IsChecked ?? true;

        if (double.TryParse(ScrollStepBox.Text, out var px) && px >= 50)
            _working.ScrollStepPixels = px;
        else
            _working.ScrollStepPixels = 300;

        if (int.TryParse(PreloadUpBox.Text, out var up) && up >= 0)
            _working.PreloadUpCount = up;
        else
            _working.PreloadUpCount = 2;

        if (int.TryParse(PreloadDownBox.Text, out var down) && down >= 0)
            _working.PreloadDownCount = down;
        else
            _working.PreloadDownCount = 5;

        _target.CopyFrom(_working);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnAssoc_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociationService.RegisterAndOpenSettings();
            RefreshAssocStatus();
            MessageBox.Show(this,
                "已注册图片类型关联。\n请在打开的「默认应用」列表中选择 ComicViewer 作为默认图片查看器。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
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
