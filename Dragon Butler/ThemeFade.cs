using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DragonButler;

/// <summary>
/// v1.47 / M11-5（D24）：主题切换时让旧画面淡出，而不是「一格一格刷地变掉」。
///
/// <para>
/// 为什么是截图淡出：WPF 换一套资源字典不是一次原子操作——背景、字色、边框各走各的，
/// 中间会闪一帧半新半旧的样子。人眼对这种「跳一下」特别敏感，而真正想传达的只是
/// 「换风格了」，那就把旧画面盖在上面慢慢退掉，中间那一帧谁也没看见。
/// </para>
///
/// <para>
/// 不做径向扩散那类动画：它把注意力引到「动画本身」，而主题切换是个配角动作。
/// 250ms / CubicEase EaseOut 是先快后慢，结束得干净，不会让下一个人觉得界面卡。
/// </para>
/// </summary>
internal static class ThemeFade
{
    /// <summary>淡出时长。数字只在这一处写。</summary>
    public const int DurationMs = 250;

    /// <summary>圆角与窗口外框一致，否则截图会从圆角里冒出四个直角。</summary>
    private const double CornerRadius = 16;

    /// <summary>
    /// 探针模式下要关掉：截一张位图再动画 250ms 会污染渲染截图的时序，
    /// 而且无头环境里动画时钟不推进，测试会卡在「永远没结束」上。
    /// </summary>
    public static bool Enabled { get; set; } = true;

    private static bool _running;

    /// <summary>
    /// 把 <paramref name="swap"/> 包在淡出里执行：截旧画面 → 切主题 → 盖上去淡出。
    ///
    /// <para>
    /// 亮度没变就不做动画（暗→暗、亮→亮）：那种切换观感上只是配色微调，
    /// 加一层全屏位图纯属浪费，还会让相邻两次点击看起来像卡了一下。
    /// </para>
    /// </summary>
    public static void Run(Window? window, Action swap)
    {
        if (swap == null) return;

        // 连点防护：上一轮还没退完就再来一次，只切主题、不叠第二层截图
        // （叠上去会看到「旧旧旧←新」三层混色，比不做动画还糟）。
        if (_running || !Enabled || window == null)
        {
            swap();
            return;
        }

        Panel? host = FindHost(window);
        if (host == null || window.ActualWidth < 2 || window.ActualHeight < 2)
        {
            swap();
            return;
        }

        BitmapSource? shot = Capture(window);
        bool wasLight = Theme.LastSchemeLight;

        _running = true;
        try
        {
            swap();

            if (shot == null || wasLight == Theme.LastSchemeLight)
            {
                // 亮度没变，或者根本没截到图：主题已经切完了，没什么要淡出的。
                return;
            }

            var image = new Image
            {
                Source = shot,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true,
            };
            image.Clip = new RectangleGeometry(
                new Rect(0, 0, window.ActualWidth, window.ActualHeight), CornerRadius, CornerRadius);

            var layer = new Grid
            {
                IsHitTestVisible = false,
                Background = Brushes.Transparent,
            };
            Grid.SetRowSpan(layer, 3);
            layer.Children.Add(image);
            host.Children.Add(layer);

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(DurationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            fade.Completed += (_, _) =>
            {
                host.Children.Remove(layer);
                image.Source = null;
                _running = false;
            };
            layer.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        catch
        {
            _running = false;
            throw;
        }
    }

    /// <summary>截图。多显示器上只截窗口自己那块，不是整块屏幕。</summary>
    private static BitmapSource? Capture(Window window)
    {
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            int w = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY);
            if (w < 2 || h < 2) return null;

            var bmp = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bmp.Render(window);
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 覆盖层挂哪儿：窗口内容根。主窗是 <c>Border(WindowFrame) → Grid(三行)</c>，
    /// 所以要多走一层，直接往 Grid 上加才能盖住标题栏、内容区和底栏三块。
    /// </summary>
    private static Panel? FindHost(Window window)
    {
        if (window.Content is Panel p) return p;
        if (window.Content is Border b && b.Child is Panel bp) return bp;
        if (window.Content is ContentControl cc && cc.Content is Panel cp) return cp;
        return null;
    }
}
