using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragonButler;
using DragonButler.Plugins;

namespace CoordinatorProbe;

/// <summary>
/// 界面离屏渲染（v1.8 起用于人工验收）。
///
/// 为什么要在进程内画，而不是截屏：
/// 桌面上随时可能有别的窗口盖住主界面，CopyFromScreen 截到的是别人的画面；
/// PrintWindow 对 WPF 的合成层又常常只拿到一张白/黑图。
/// 用 RenderTargetBitmap 直接渲染视觉树，不受桌面状态影响，CI 里也能跑。
/// </summary>
internal static partial class RenderProbe
{
    /// <summary>把某个 Window 渲染成 PNG。必须在 STA 线程里调用。</summary>
    public static bool Save(Window window, string path, int width, int height, bool show = true)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (show)
            {
                // 用 1×1 的透明位置「显示」一次：WPF 需要一个真实窗口句柄来完成布局与模板展开，
                // 但位置在屏幕外，肉眼看不到闪窗。
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                if (width > 0) window.Width = width;
                if (height > 0) window.Height = height;
                window.Show();
            }

            window.UpdateLayout();

            // 注：v1.40 起主窗是固定高度（Height="780"），WPF 仍会把它夹在工作区以内（这台机器 816-24）。
            // 高度传 0 表示「按窗口现在的实际高度截」，截出来的就是用户平时看到的那一屏。
            // 想看清底部就换 ui top / 展开折叠卡，别试图把窗口拉得比屏幕还高。
            int realWidth = width > 0 ? width : (int)Math.Ceiling(window.ActualWidth);
            int realHeight = height > 0 ? height : (int)Math.Ceiling(window.ActualHeight);
            if (realWidth <= 0) realWidth = 640;
            if (realHeight <= 0) realHeight = 480;

            var visual = (Visual)window.Content;
            var size = new Size(realWidth, realHeight);
            var bitmap = new RenderTargetBitmap(realWidth, realHeight, 96, 96, PixelFormats.Pbgra32);

            // 先用窗口背景铺一层，否则半透明窗口渲染出来是黑底
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var bg = window.Background;
                dc.DrawRectangle(bg ?? Brushes.Transparent, null, new Rect(size));
            }
            bitmap.Render(dv);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);

            if (show) window.Hide();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("RENDER-FAIL " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 给渲染用的样例队列：三个工具、两条依赖、一处切片。
    /// 只在本进程内改内存，不写盘——渲染探针不该改用户的配置。
    /// </summary>
    /// <remarks>
    /// v1.22：内容区拆成三页之后，右页（工具设置 / 高级 / 系统 / 通知）默认不在可视区里。
    /// 渲染前必须先把页面切过去，否则截出来会是中页。用反射调 ShowPage(index, false)：
    /// 不带动画、立即到位，截的才是稳定画面。
    /// </remarks>
    internal static void SwitchToSettingsPage(MainWindow w)
    {
        try
        {
            var m = typeof(MainWindow).GetMethod("ShowPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            // v1.40：设置页从第 2 位挪到第 3 位（汇报插在日志与主页之间），这里必须跟着走，
            // 否则截出来的是主页——2026-11-21 就踩过一次，看图才发现导航亮的是「主页」。
            m?.Invoke(w, new object[] { Pager.Settings, false });
            var pillSet = typeof(MainWindow).GetMethod("MoveNavPill",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            pillSet?.Invoke(w, new object[] { Pager.Settings, false });
            w.UpdateLayout();
        }
        catch (Exception ex) { Console.WriteLine("SWITCH-PAGE-FAIL " + ex.Message); }
    }

    /// <summary>
    /// v1.34：插件有了自己的一页（导航第四项）。截那一页同样要先把页面切过去。
    /// </summary>
    internal static void SwitchToPluginsPage(MainWindow w)
    {
        try
        {
            var m = typeof(MainWindow).GetMethod("ShowPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            m?.Invoke(w, new object[] { 3, false });
            // ShowPage 里的标签块会走 380ms 回弹动画，截图若在这之前拍，
            // 拍到的就是「底还停在上一格」。渲染要的是稳定画面，直接让它一步到位。
            var pill = typeof(MainWindow).GetMethod("MoveNavPill",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            pill?.Invoke(w, new object[] { 3, false });
            w.UpdateLayout();
        }
        catch (Exception ex) { Console.WriteLine("SWITCH-PAGE-FAIL " + ex.Message); }
    }

    /// <summary>
    /// v1.40：汇报有了自己的一页（导航第二项，日志与主页之间）。
    /// 与插件页同一套做法：先 ShowPage，再把标签块一步到位，否则截图会拍到「底还在上一格」。
    /// </summary>
    /// <summary>
    /// v1.42.1 / M9-3（D17）：把主页夹具按固定形状重建，不受前面几段 mode 的影响。
    /// 存在的理由：零滚动量的是「装好的人、两行工具」这一种主页。
    /// ui all 会先跑 grid / presets，那两段往设置里塞了 4 行工具和一套配置组合，
    /// 量出来 722 而不是 623——数字随顺序漂，契约就成了摆设。
    /// </summary>
    internal static void ResetHomeFixture()
    {
        try
        {
            AppState.Settings.Queue = new List<QueueEntry>
            {
                new() { ToolId = "bettergi", Enabled = true, WeekdayMask = 0x7F, Groups = new List<string> { "日常", "活动" } },
                new() { ToolId = "march7th", Enabled = true, WeekdayMask = 0x7F },
            };
            AppState.Settings.ToolPages = new List<List<string>> { new() { "bettergi", "march7th", "ok-ww" } };
            AppState.Settings.ToolOrder = ToolPages.Flatten(AppState.Settings.ToolPages).ToArray();
            AppState.Settings.Presets = new List<ConfigPreset>();
            AppState.Settings.ActivePresetId = "";
            AppState.Settings.LastRunAt = "";
            AppState.Settings.LastRunOk = false;
            AppState.Settings.ShieldAlertAt = null;
            SeedSampleBgiDir();
            // 契约量的是「装好的人」：档案里得真有一个存在的 exe，否则 HomeState 判 FirstRun，
            // 量到的是一张压暗的引导页——那页当然不超预算，等于没测。
            // 同时把 ToolSetupBlock 亮出来：装好的人才会看到「每个工具怎么跑」那一段，
            // 首次运行它是收起的，撑出来的高度比真用户还矮。
            try
            {
                string exe = Path.Combine(Path.GetTempPath(), "dragon-ui", "fake-bgi", "BetterGI.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                if (!File.Exists(exe)) File.WriteAllBytes(exe, Array.Empty<byte>());
                AppState.Settings.BgiPath = exe;
                var bgi = ProfileRegistry.Get("bettergi");
                bgi.ExecutablePath = exe;
                ProfileRegistry.Save(bgi);
            }
            catch (Exception ex) { Console.WriteLine("RESET-EXE-FAIL " + ex.Message); }
        }
        catch (Exception ex)
        {
            Console.WriteLine("RESET-FAIL " + ex.Message);
        }
    }

    /// <summary>
    /// v1.40：把导航胶囊直接摆到终点（要么藏在下面，要么在原位）。
    /// 渲染截图要的是稳定画面，动画本身靠 220ms 的消息泵推进，截不到。
    /// </summary>
    internal static void SettleNav(MainWindow w, bool hide)
    {
        try
        {
            var m = typeof(MainWindow).GetMethod("SetNavHidden",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            m?.Invoke(w, new object[] { hide, false });
        }
        catch (Exception ex) { Console.WriteLine("SETTLE-NAV-FAIL " + ex.Message); }
    }

    internal static void SwitchToReportPage(MainWindow w)
    {
        try
        {
            var m = typeof(MainWindow).GetMethod("ShowPage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            m?.Invoke(w, new object[] { Pager.Report, false });
            var pill = typeof(MainWindow).GetMethod("MoveNavPill",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            pill?.Invoke(w, new object[] { Pager.Report, false });
            w.UpdateLayout();
        }
        catch (Exception ex) { Console.WriteLine("SWITCH-PAGE-FAIL " + ex.Message); }
    }

    /// <summary>
    /// 渲染一个独立窗口（浮动条、命令面板、日历），跑完就关。
    /// 这些窗口没有主窗的依赖，可以单独实例化。
    /// </summary>
    public static int RunUi(string mode) => new UiRunner(mode).Run();
}
