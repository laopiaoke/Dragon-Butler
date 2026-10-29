using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DragonButler;

/// <summary>
/// 自定义主题编辑器：左侧实时预览、右侧参数。
///
/// 交互约定：
///  - 打开即把当前参数应用到整个应用（所见即所得），取消时回滚到打开前的主题；
///  - 「应用」写入 settings.json 并把 Theme 置为 Custom；
///  - 「恢复默认主题」清空自定义参数并切回「跟随系统」。
/// </summary>
public partial class CustomThemeWindow : Window
{
    private string _kind = "Color";
    private string _color = "#FF1B2430";
    private string _image = "";
    private int _overlay = 55;
    private string _scheme = "Auto";

    private readonly string _origTheme;
    private bool _applied;
    private bool _ready;

    /// <summary>调色板预设：12 深 + 6 浅，覆盖常见壁纸色调</summary>
    private static readonly string[] Presets =
    {
        "#FF1B2430", "#FF0F172A", "#FF1E1B4B", "#FF2A1B3D", "#FF3B1F2B", "#FF0F2A24",
        "#FF0B3B4A", "#FF13343B", "#FF2D2A1F", "#FF3A2E1B", "#FF111111", "#FF2B2B2B",
        "#FFF5F7FA", "#FFEDE7DD", "#FFE8F0EA", "#FFE9EDF7", "#FFF7EAEA", "#FFF2ECDC",
    };

    private readonly List<Border> _swatches = new();

    public CustomThemeWindow()
    {
        InitializeComponent();

        var s = AppState.Settings;
        _origTheme = string.IsNullOrWhiteSpace(s.Theme) ? "System" : s.Theme;
        _kind = string.Equals(s.CustomBgKind, "Image", StringComparison.OrdinalIgnoreCase) ? "Image" : "Color";
        _color = string.IsNullOrWhiteSpace(s.CustomBgColor) ? "#FF1B2430" : s.CustomBgColor;
        _image = s.CustomBgImage ?? "";
        _overlay = Math.Clamp(s.CustomOverlay, 0, 90);
        _scheme = string.IsNullOrWhiteSpace(s.CustomScheme) ? "Auto" : s.CustomScheme;

        BuildSwatches();
        _ready = true;
        SyncUi();
        ApplyPreview();

        Closed += (_, _) => RestoreIfNotApplied();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
    }

    // ===== 初始化 =====

    private void BuildSwatches()
    {
        foreach (string hex in Presets)
        {
            var b = new Border
            {
                Style = TryFindResource("Swatch") as Style,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Tag = hex,
            };
            b.MouseLeftButtonDown += (_, _) =>
            {
                _color = (string)b.Tag;
                _kind = "Color";
                SyncUi();
                ApplyPreview();
            };
            SwatchPanel.Children.Add(b);
            _swatches.Add(b);
        }
    }

    /// <summary>把字段回填到控件（不触发事件循环）</summary>
    private void SyncUi()
    {
        _ready = false;
        try
        {
            SegColor.IsChecked = _kind == "Color";
            SegImage.IsChecked = _kind == "Image";
            ColorPanel.Visibility = _kind == "Color" ? Visibility.Visible : Visibility.Collapsed;
            ImagePanel.Visibility = _kind == "Image" ? Visibility.Visible : Visibility.Collapsed;

            SegAuto.IsChecked = _scheme == "Auto";
            SegLightText.IsChecked = _scheme == "Light";
            SegDarkText.IsChecked = _scheme == "Dark";

            SldOverlay.Value = _overlay;
            TbOverlayValue.Text = _overlay + "%";
            TbImageName.Text = string.IsNullOrWhiteSpace(_image) ? "未选择图片" : System.IO.Path.GetFileName(_image);

            foreach (var b in _swatches)
            {
                bool on = string.Equals((string)b.Tag, _color, StringComparison.OrdinalIgnoreCase);
                b.BorderThickness = new Thickness(on ? 2 : 1);
                b.BorderBrush = (on
                    ? TryFindResource("BrandBrush")
                    : TryFindResource("BorderStrongBrush")) as Brush ?? Brushes.Gray;
            }
        }
        finally
        {
            _ready = true;
        }
    }

    // ===== 预览 =====

    private void ApplyPreview()
    {
        Theme.ApplyCustom(_kind, _color, _image, _overlay, _scheme);
        UpdateReadout();
    }

    /// <summary>对比度读数：正文（卡片底 + TextPrimary）实时计算，并按 WCAG 给结论</summary>
    private void UpdateReadout()
    {
        double ratio = Theme.Contrast(Theme.LastCardColor, Theme.LastTextColor);
        string level = ratio >= 7 ? "AAA" : ratio >= 4.5 ? "AA" : "偏低";
        TbContrast.Text = $"正文对比度 {ratio:F1}:1 · {level}";

        string scheme = Theme.LastSchemeLight ? "浅底深字" : "深底浅字";
        TbTip.Text = $"自动判定为「{scheme}」，背景亮度 {Theme.LastBgLuminance:F2}（0=黑 1=白）。"
                   + "文字始终落在卡片上，卡片底色已按方案锚定，换背景不会稀释对比度。";
        TbPreviewHint.Text = _kind == "Image"
            ? $"图片背景 · 遮罩 {_overlay}% · 平均色 {Theme.LastBgColor.R:X2}{Theme.LastBgColor.G:X2}{Theme.LastBgColor.B:X2}"
            : $"纯色背景 · {_color}";
    }

    // ===== 事件 =====

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void SegBgKind_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _kind = (sender as FrameworkElement)?.Tag as string ?? "Color";
        SyncUi();
        ApplyPreview();
    }

    private void SegScheme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _scheme = (sender as FrameworkElement)?.Tag as string ?? "Auto";
        ApplyPreview();
    }

    private void SldOverlay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _overlay = (int)Math.Round(e.NewValue);
        TbOverlayValue.Text = _overlay + "%";
        ApplyPreview();
    }

    private void BtnPickImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _image = dlg.FileName;
            _kind = "Image";
            SyncUi();
            ApplyPreview();
        }
    }

    private void BtnPickColor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var cd = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
            };
            var c = (Color)ColorConverter.ConvertFromString(_color);
            cd.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            if (cd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _color = $"#FF{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
                _kind = "Color";
                SyncUi();
                ApplyPreview();
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("选取自定义颜色失败：" + ex.Message);
        }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        s.Theme = "Custom";
        s.CustomBgKind = _kind;
        s.CustomBgColor = _color;
        s.CustomBgImage = _image;
        s.CustomOverlay = _overlay;
        s.CustomScheme = _scheme;
        AppState.SaveSettings();
        Theme.ApplyCustom(_kind, _color, _image, _overlay, _scheme);
        _applied = true;
        DialogResult = true;
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        s.Theme = "System";
        s.CustomBgKind = "Color";
        s.CustomBgColor = "#FF1B2430";
        s.CustomBgImage = "";
        s.CustomOverlay = 55;
        s.CustomScheme = "Auto";
        AppState.SaveSettings();
        Theme.Set("System");
        _applied = true;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>取消 / 关闭时把主题还原成打开前的状态（设置未写入）</summary>
    private void RestoreIfNotApplied()
    {
        if (_applied) return;
        try
        {
            var s = AppState.Settings;
            s.Theme = _origTheme;
            Theme.ApplyFromSettings();
        }
        catch (Exception ex)
        {
            LogService.LogEvent("还原主题失败：" + ex.Message);
        }
    }
}
