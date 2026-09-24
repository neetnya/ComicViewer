using System.Windows;

namespace ComicViewer;

public partial class SettingsWindow : Window
{
    private readonly Models.AppConfig _config;

    public SettingsWindow(Models.AppConfig config)
    {
        InitializeComponent();
        _config = config;
        WidthSlider.Value = config.ComicWidthRatio;
        FullscreenCheck.IsChecked = config.StartFullscreen;
        ScrollStepBox.Text = config.ScrollStepPixels.ToString("0");
        UpdateWidthLabel();
    }

    private void WidthSlider_OnValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateWidthLabel();
        _config.ComicWidthRatio = WidthSlider.Value;
    }

    private void UpdateWidthLabel()
    {
        if (WidthLabel == null) return;
        WidthLabel.Text = $"{WidthSlider.Value * 100:0}%";
    }

    private void ScrollStepBox_OnTextChanged(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        // 仅做实时校验提示，实际值在保存时读取
    }

    private void BtnSave_OnClick(object sender, RoutedEventArgs e)
    {
        _config.ComicWidthRatio = WidthSlider.Value;
        _config.StartFullscreen = FullscreenCheck.IsChecked ?? true;

        // 解析滚动距离像素，非法或过小则回退到默认 300
        if (double.TryParse(ScrollStepBox.Text, out var px) && px >= 50)
            _config.ScrollStepPixels = px;
        else
            _config.ScrollStepPixels = 300;

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
            Services.FileAssociationService.TrySetAsDefault();
            MessageBox.Show(this,
                "已打开系统默认应用设置。\n请在「照片查看器」中选择 ComicViewer 作为默认。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开设置：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
