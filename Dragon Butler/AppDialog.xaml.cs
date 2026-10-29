using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DragonButler;

/// <summary>
/// 统一的应用内弹窗（替代系统 MessageBox）：
/// 圆角卡片 + 主题色 + 项目图标 + 品牌主按钮，深浅色跟随 Theme，字体与主界面一致。
///
/// 设计约束：
/// 1) 启动早期（单实例检测 / 上次异常退出提示）就要能弹，因此 Show 内部会先确保主题资源已写入
///    （Theme.Set 幂等），不依赖 MainWindow 是否已创建。
/// 2) 不设置 DialogResult（非模态测试也能用），结果放在 <see cref="Result"/>，Show 返回它。
/// 3) 所有文案入口都走这里，代码里不再出现 MessageBox.Show。
/// </summary>
public partial class AppDialog : Window
{
    /// <summary>弹窗类型：只影响徽章颜色与符号，不改变布局</summary>
    public enum Kind
    {
        Info,
        Warning,
        Error,
        Question,
    }

    /// <summary>主按钮 = true（知道了/确定），次按钮或关闭 = false</summary>
    public bool Result { get; private set; }

    private Action? _secondaryAction;

    public AppDialog()
    {
        InitializeComponent();
    }

    // ===== 对外入口 =====

    /// <summary>
    /// 显示一个主题化弹窗（模态）。返回 true 表示点了主按钮。
    /// </summary>
    public static bool Show(string message,
                            string? detail = null,
                            Kind kind = Kind.Info,
                            string? title = null,
                            string primary = "知道了",
                            string? secondary = null,
                            Action? secondaryAction = null,
                            Window? owner = null,
                            string? copyText = null)
    {
        try
        {
            var dlg = Create(kind, title ?? "一条龙管家", message, detail, primary, secondary, secondaryAction, owner, copyText);
            dlg.ShowDialog();
            return dlg.Result;
        }
        catch (Exception ex)
        {
            // 弹窗自身失败不能影响主流程：至少写进日志，避免"点了没反应"
            LogService.LogEvent("弹窗显示失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>创建但不显示（自动化验证用；Show 内部同样走这里）</summary>
    public static AppDialog Create(Kind kind,
                                   string title,
                                   string message,
                                   string? detail = null,
                                   string primary = "知道了",
                                   string? secondary = null,
                                   Action? secondaryAction = null,
                                   Window? owner = null,
                                   string? copyText = null)
    {
        EnsureThemeResources();
        var dlg = new AppDialog();
        // 启动早期的提示往往在后台进程里弹出：先置顶确保用户看得见，
        // 一旦拿到焦点就恢复普通层级（避免长期压在其它窗口之上）
        dlg.Topmost = true;
        dlg.Setup(kind, title, message, detail, primary, secondary, secondaryAction, copyText);
        if (owner != null && owner.IsVisible && !ReferenceEquals(owner, dlg))
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dlg;
    }

    // ===== 初始化 =====

    private void Setup(Kind kind, string title, string message, string? detail,
                       string primary, string? secondary, Action? secondaryAction,
                       string? copyText)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "一条龙管家" : title;
        TitleText.Text = Title;
        MessageText.Text = message;

        if (string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailText.Text = detail;
            DetailText.Visibility = Visibility.Visible;
        }

        bool hasCopyBox = !string.IsNullOrWhiteSpace(copyText);
        if (hasCopyBox)
        {
            CopyBox.Text = copyText;
            CopyBox.Visibility = Visibility.Visible;
        }
        else
        {
            CopyBox.Visibility = Visibility.Collapsed;
        }

        BtnPrimary.Content = string.IsNullOrWhiteSpace(primary) ? "知道了" : primary;
        if (string.IsNullOrWhiteSpace(secondary))
        {
            BtnSecondary.Visibility = Visibility.Collapsed;
        }
        else
        {
            BtnSecondary.Content = secondary;
            BtnSecondary.Visibility = Visibility.Visible;
            _secondaryAction = secondaryAction;
        }

        var (key, glyph) = kind switch
        {
            Kind.Warning => ("Warning", "!"),
            Kind.Error => ("Danger", "!"),
            Kind.Question => ("Brand", "?"),
            _ => ("Brand", "i"),
        };
        var color = ThemeColor(key);
        Badge.Background = new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
        BadgeGlyph.Foreground = new SolidColorBrush(color);
        BadgeGlyph.Text = glyph;

        var icon = AppIconSource();
        if (icon != null)
        {
            Icon = icon;
            HeaderIcon.Source = icon;
        }
        else
        {
            HeaderIcon.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) =>
        {
            if (hasCopyBox)
            {
                // 兜底场景：聚焦文本框并全选，用户按一次 Ctrl+C 即可复制
                CopyBox.Focus();
                CopyBox.SelectAll();
            }
            else
            {
                BtnPrimary.Focus();
            }
            Activate();
        };
        Activated += (_, _) =>
        {
            try { Topmost = false; } catch { }
        };
    }

    /// <summary>启动早期 Application.Resources 可能还没有主题令牌（Theme.Set 尚未执行）</summary>
    private static void EnsureThemeResources()
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res == null) return;
            if (res.Contains("CardBgBrush") && res.Contains("BrandBrush")) return;
            Theme.Set(AppState.Settings.Theme);
        }
        catch { }
    }

    private static Color ThemeColor(string key)
    {
        try
        {
            if (Application.Current?.Resources[key + "Brush"] is SolidColorBrush b) return b.Color;
        }
        catch { }
        return Color.FromRgb(0x3D, 0xDC, 0x84);
    }

    /// <summary>取 exe 内嵌图标（与托盘/任务栏同源），失败则隐藏图标位</summary>
    private static ImageSource? _appIcon;

    private static ImageSource? AppIconSource()
    {
        if (_appIcon != null) return _appIcon;
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (ico == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            src.Freeze();
            _appIcon = src;
        }
        catch { }
        return _appIcon;
    }

    // ===== 交互 =====

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button)
                DragMove();
        }
        catch { }
    }

    private void BtnPrimary_Click(object sender, RoutedEventArgs e) => CloseWith(true);

    private void BtnSecondary_Click(object sender, RoutedEventArgs e)
    {
        try { _secondaryAction?.Invoke(); } catch { }
        CloseWith(false);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => CloseWith(false);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWith(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            CloseWith(true);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void CloseWith(bool result)
    {
        Result = result;
        try { Close(); } catch { }
    }
}
