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
using static CoordinatorProbe.RenderProbe;

namespace CoordinatorProbe;

// v1.45.3 / #15：界面渲染探针按截图分组拆成多个 partial 文件，单文件 <= 20 KB。
// 每张图的渲染逻辑与输出文件名一字未改，只换了所在文件。
internal sealed partial class UiRunner
{
    private void Step24()
    {
            // v1.41 / M4-1：设置页从「一屏全是开关」改成「一级入口卡 + 二级才铺开关」。
            // v1.47 / M11-3：多了「工具配置」这一区。
            // 老的 advanced/top 只能拍到一级那五张卡，二级到底长什么样需要单独拍。
            if (mode == "panes" || mode == "all")
            {
                var w = new MainWindow();
                SwitchToSettingsPage(w);
                w.UpdateLayout();
                var show = typeof(MainWindow).GetMethod("ShowSettingsPane",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                foreach (var (paneName, file) in new[]
                {
                    ("Guard", "pane_guard.png"),
                    ("Timing", "pane_timing.png"),
                    ("Notify", "pane_notify.png"),
                    ("Api", "pane_api.png"),
                    ("Tools", "pane_tools.png"),
                    ("Ui", "pane_ui.png"),
                })
                {
                    try
                    {
                        var paneType = typeof(MainWindow).GetNestedType("SettingsPane",
                            System.Reflection.BindingFlags.NonPublic);
                        object value = Enum.Parse(paneType!, paneName);
                        show?.Invoke(w, new[] { value });
                        w.UpdateLayout();
                        // 说明折叠（M4-2）要等排版完再跑一遍才收得起来
                        w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                        w.UpdateLayout();
                        bool ok = Save(w, Path.Combine(spot, file), 1120, 0);
                        Console.WriteLine($"RENDER {Path.GetFileNameWithoutExtension(file)}={ok}");
                    }
                    catch (Exception ex) { Console.WriteLine("RENDER pane fail " + paneName + ": " + ex.Message); }
                }
                w.Close();
            }
    }
}
