using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragonButler;

namespace CoordinatorProbe;

/// <summary>
/// 界面离屏渲染（v1.8 起用于人工验收）。
///
/// 为什么要在进程内画，而不是截屏：
/// 桌面上随时可能有别的窗口盖住主界面，CopyFromScreen 截到的是别人的画面；
/// PrintWindow 对 WPF 的合成层又常常只拿到一张白/黑图。
/// 用 RenderTargetBitmap 直接渲染视觉树，不受桌面状态影响，CI 里也能跑。
/// </summary>
internal static class RenderProbe
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

            // SizeToContent 的窗口高度由内容决定，硬编码会把下方留成空白
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
    /// 渲染一个独立窗口（浮动条、命令面板、日历），跑完就关。
    /// 这些窗口没有主窗的依赖，可以单独实例化。
    /// </summary>
    public static int RunUi(string mode)
    {
        int exit = 1;
        var worker = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (_, _) =>
            {
                try
                {
                    // 主题令牌（CardBgBrush 等）是在代码里写进 Application.Current.Resources 的，
                    // 不调这一步，DynamicResource 全部落空，渲染出来是白底黑字的空壳。
                    Theme.Set("Dark");

                    var spot = Path.Combine(Path.GetTempPath(), "dragon-ui");
                    Directory.CreateDirectory(spot);

                    if (mode == "minibar" || mode == "all")
                    {
                        var w = new MiniBarWindow();
                        bool ok = Save(w, Path.Combine(spot, "minibar.png"), 180, 44);
                        Console.WriteLine($"RENDER minibar={ok}");
                        w.Close();
                    }

                    if (mode == "calendar" || mode == "all")
                    {
                        var w = new CalendarWindow();
                        bool ok = Save(w, Path.Combine(spot, "calendar.png"), 0, 0);
                        Console.WriteLine($"RENDER calendar={ok}");
                        w.Close();
                    }

                    if (mode == "palette" || mode == "all")
                    {
                        var w = new CommandPalette();
                        bool ok = Save(w, Path.Combine(spot, "palette.png"), 560, 0);
                        Console.WriteLine($"RENDER palette={ok}");
                        w.Close();
                    }

                    if (mode == "main" || mode == "all")
                    {
                        var w = new MainWindow();
                        bool ok = Save(w, Path.Combine(spot, "main.png"), 980, 792);
                        Console.WriteLine($"RENDER main={ok}");
                        w.Close();
                    }

                    Console.WriteLine("RENDER-DIR " + spot);
                    exit = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("RENDER-FAIL " + ex.Message);
                    exit = 1;
                }
                finally
                {
                    app.Shutdown();
                }
            };
            app.Run();
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join(TimeSpan.FromSeconds(90));
        return exit;
    }
}
