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
    private void Step12()
    {
            // v1.42 / M8-2：任务清单卡里的横向工具行。要证明的三件事——
            // ① 一行一个工具，手柄/图标/名字/徽章/摘要/箭头都在一条基线上；
            // ② 展开区缩进到图标左边对齐（不是跟手柄对齐），圆角与深一档底色；
            // ③ BetterGI 那行展开后能勾配置组，右侧实时「今晚 N 项」。
            // 原来这里截的是「托管配置」独立窗口，v1.42 起那个窗口按 D9 删掉了。
            if (mode == "rows" || mode == "tray" || mode == "all")
            {
                // v1.42.1 / M9-3：零滚动契约量的是「装好的人」的主页。
                // 不设这两个标志，HomeHero 会顶上首访引导卡（装好的人看不到它），
                // 多出来的 100 多像素会把结论带偏。
                AppState.Settings.FirstRunDone = true;
                AppState.Settings.OnboardingCompleted = true;
                // 工具行和配置组合行都不能是前面几段 mode 留下的残留：
                // ui all 会先跑 grid / presets，那两段往设置里塞了 4 行工具和一套组合，
                // 量出来 722 而不是 623。量的必须是固定夹具，不然这个数字每次跑都不一样。
                ResetHomeFixture();
                foreach (var id in new[] { "zzz-od", "maaend", "ok-nte", "src" })
                {
                    try { ProfileRegistry.Save(ProfileRegistry.Get(id)); } catch { }
                }
                AppState.SaveSettings();

                var win = new MainWindow();
                win.Show();
                win.UpdateLayout();
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                try
                {
                    var showPage = typeof(MainWindow).GetMethod("ShowPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    showPage?.Invoke(win, new object[] { Pager.Home, false });
                    win.UpdateLayout();
                    win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    bool ok1 = Save(win, Path.Combine(spot, "toolrows.png"), 1120, 720);
                    Console.WriteLine($"RENDER toolrows={ok1}");

                    // v1.42.1 / M9-3（D17）：收起态零滚动契约。
                    // 720 高是笔记本上最常见的窗口高度，摘要和一键开跑必须一眼都在。
                    // 判据用 ScrollableHeight——比「截图看起来没被挡」硬，挡没挡住不靠眼睛。
                    if (win.FindName("ScrollHome") is System.Windows.Controls.ScrollViewer homeScroll)
                    {
                        win.UpdateLayout();
                        Console.WriteLine($"ZERO-SCROLL viewport={homeScroll.ViewportHeight:F0} " +
                            $"extent={homeScroll.ExtentHeight:F0} scrollable={homeScroll.ScrollableHeight:F1}");
                        Console.WriteLine($"ZERO-SCROLL collapsed_ok={(homeScroll.ScrollableHeight <= 0.5 ? "YES" : "NO")}");
                        // v1.42.1 / M9-3（D17）：契约说的是「摘要、一键开跑、添加行都看得见」，
// 不是「整页一点都滚不动」——「每个工具怎么跑」那一段本来就在折叠线下面，
// 注释里写明了「上面挑、下面配，往下滚一点就到」。拿整页 ExtentHeight 当判据
// 会把它算成赤字，逼着人去砍一段本来就不该进首屏的内容。
// 所以量三块东西的底边：都在视口里，才算达成。
bool AboveFold(System.Windows.FrameworkElement? el)
{
    if (el == null || el.ActualHeight <= 0) return false;
    try
    {
var p = el.TransformToAncestor(homeScroll).Transform(new System.Windows.Point(0, el.ActualHeight));
return p.Y <= homeScroll.ViewportHeight + 0.5;
    }
    catch { return false; }
}
var fnSummary = win.FindName("TbTonightSummary") as System.Windows.FrameworkElement;
var fnStart = win.FindName("BtnQueueStart") as System.Windows.FrameworkElement;
bool okSummary = fnSummary != null && AboveFold(fnSummary);
bool okStart = fnStart != null && AboveFold(fnStart);
// 添加行没有名字，按位置找：ToolRowHost 里最后一个 Border（BuildAddRow 出来的那条）。
bool okAdd = false;
if (win.FindName("ToolRowHost") is System.Windows.Controls.StackPanel hostRows)
{
    System.Windows.Controls.Border? lastBorder = null;
    foreach (System.Windows.UIElement r in hostRows.Children)
if (r is System.Windows.Controls.Border rb) lastBorder = rb;
    okAdd = lastBorder != null && AboveFold(lastBorder);
}
Console.WriteLine($"ZERO-SCROLL fold summary={okSummary} start={okStart} add={okAdd}");
if (mode == "rows" && !(okSummary && okStart && okAdd)) zeroScrollFail = true;
                        // 主卡内部也打一遍：250 的主卡是最大的一块，得知道钱花在哪。
                        if (win.FindName("HomeHero") is System.Windows.Controls.Border hero && hero.Child is System.Windows.Controls.Panel hp)
                            foreach (System.Windows.UIElement hc in hp.Children)
                                if (hc is System.Windows.Controls.Panel hsp)
                                    foreach (System.Windows.UIElement hcc in hsp.Children)
                                        if (hcc is System.Windows.FrameworkElement hf && hf.ActualHeight > 0)
                                            Console.WriteLine($"ZERO-SCROLL       hero={hf.Name}/{hf.GetType().Name} h={hf.ActualHeight:F0}");
                        // 超预算时要知道是哪一块撑出来的，不然只能瞎调间距。
                                                        // 工具行宿主与「每个工具怎么跑」块也逐行量。
                        if (win.FindName("ToolRowHost") is System.Windows.Controls.StackPanel rh)
                            foreach (System.Windows.UIElement r in rh.Children)
                                if (r is System.Windows.FrameworkElement rfe && rfe.ActualHeight > 0)
                                    Console.WriteLine($"ZERO-SCROLL       rowhost={rfe.Name}/{rfe.GetType().Name} h={rfe.ActualHeight:F0} margin={rfe.Margin}");
                        if (win.FindName("ToolSetupBlock") is System.Windows.Controls.StackPanel tsb)
                            foreach (System.Windows.UIElement s in tsb.Children)
                                if (s is System.Windows.FrameworkElement sfe && sfe.ActualHeight > 0)
                                    Console.WriteLine($"ZERO-SCROLL       setup={sfe.Name}/{sfe.GetType().Name} h={sfe.ActualHeight:F0} margin={sfe.Margin}");                                // 底部动作区逐行量：超 20px 时得知道钱花在哪一行。
                        if (win.FindName("QueueCard") is System.Windows.Controls.Border qcard && qcard.Child is System.Windows.Controls.Panel qpanel)
                            foreach (System.Windows.UIElement qchild in qpanel.Children)
                                if (qchild is System.Windows.Controls.StackPanel bottom && System.Windows.Controls.Grid.GetRow(bottom) == 5)
                                    foreach (System.Windows.UIElement bline in bottom.Children)
                                        if (bline is System.Windows.FrameworkElement bfe && bfe.ActualHeight > 0)
                                            Console.WriteLine($"ZERO-SCROLL       bottom={bfe.Name}/{bfe.GetType().Name} h={bfe.ActualHeight:F0}");if (homeScroll.Content is System.Windows.Controls.Panel panel)
                        {
                            foreach (System.Windows.UIElement child in panel.Children)
                            {
                                if (child is System.Windows.FrameworkElement fe && fe.ActualHeight > 0)
                                    Console.WriteLine($"ZERO-SCROLL part={fe.Name}/{fe.GetType().Name} h={fe.ActualHeight:F0}");
                                // Grid 这一行里就是左定时卡 + 右任务清单卡。
                                if (child is System.Windows.Controls.Panel inner)
                                    foreach (System.Windows.UIElement g in inner.Children)
                                    {
                                        if (g is System.Windows.FrameworkElement gf && gf.ActualHeight > 0)
                                            Console.WriteLine($"ZERO-SCROLL   sub={gf.Name}/{gf.GetType().Name} h={gf.ActualHeight:F0}");
                                        if (g is System.Windows.Controls.Border qb && qb.Name == "QueueCard"
                                            && qb.Child is System.Windows.Controls.Panel qp)
                                            foreach (System.Windows.UIElement qpChild in qp.Children)
                                                if (qpChild is System.Windows.FrameworkElement qf && qf.ActualHeight > 0)
                                                    Console.WriteLine($"ZERO-SCROLL     qq={qf.Name}/{qf.GetType().Name} row={System.Windows.Controls.Grid.GetRow(qf)} h={qf.ActualHeight:F0}");
                                    }
                            }
                        }

                    }
                    else
                    {
                        Console.WriteLine("ZERO-SCROLL collapsed_ok=MISSING(ScrollHome)");
                    }

                    // 展开 BetterGI 那一行：组勾选面板。
                    var expanded = typeof(MainWindow).GetField("_expandedRows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(win);
                    if (expanded is System.Collections.Generic.ICollection<string> list) list.Add("bettergi");
                    var render = typeof(MainWindow).GetMethod("RenderToolRows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    render?.Invoke(win, null);
                    win.UpdateLayout();
                    win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    bool ok2 = Save(win, Path.Combine(spot, "toolrows_open.png"), 1120, 720);
                    Console.WriteLine($"RENDER toolrows_open={ok2}");

                    // v1.42：添加面板（虚线行点开后的内联图标网格）。
                    // 先把展开区收掉、再滚到底，否则面板会被卡片下沿和悬浮导航挡住。
                    if (expanded is System.Collections.Generic.ICollection<string> nowExpanded) nowExpanded.Clear();
                    typeof(MainWindow).GetField("_addingTool", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(win, true);
                    render?.Invoke(win, null);
                    win.UpdateLayout();
                    if (win.FindName("ScrollHome") is System.Windows.Controls.ScrollViewer sc) sc.ScrollToEnd();
                    win.UpdateLayout();
                    win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    bool ok3 = Save(win, Path.Combine(spot, "toolrows_add.png"), 1120, 720);
                    Console.WriteLine($"RENDER toolrows_add={ok3}");

                    // v1.44 / 修复 #4：收尾反悔窗口的那条提示。
                    // 只走睡眠分支——关机分支会真发 shutdown /s /t 60，探针不该碰。
                    // 睡眠是进程内倒计时，取消只是关窗，不产生任何系统动作。
                    bool okWrap = false;
                    bool okWrapGone = false;
                    try
                    {
                        if (WrapUpCountdown.Start(WrapUpKind.Sleep))
                        {
                            win.UpdateLayout();
                            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                            var bar = win.FindName("WrapUpBar") as System.Windows.FrameworkElement;
                            var left = win.FindName("TbWrapUpLeft") as System.Windows.Controls.TextBlock;
                            var txt = win.FindName("TbWrapUpText") as System.Windows.Controls.TextBlock;
                            Console.WriteLine($"WRAPUP bar={bar?.Visibility} left={left?.Text} text={txt?.Text}");
                            bool shot = Save(win, Path.Combine(spot, "wrapupbar.png"), 1120, 720);
                            Console.WriteLine($"RENDER wrapupbar_shot={shot}");
                            okWrap = bar != null && bar.Visibility == System.Windows.Visibility.Visible
                                && left != null && left.Text.Length > 0
                                && txt != null && txt.Text.Contains("秒后睡眠");
                        }
                        else
                        {
                            Console.WriteLine("WRAPUP 开窗失败：Start 返回 false");
                        }

                        if (WrapUpCountdown.Cancel())
                        {
                            win.UpdateLayout();
                            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                            var bar2 = win.FindName("WrapUpBar") as System.Windows.FrameworkElement;
                            okWrapGone = bar2 != null && bar2.Visibility != System.Windows.Visibility.Visible;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("WRAPUP failed: " + ex.Message);
                    }
                    Console.WriteLine($"WRAPUP shown={okWrap} cleared={okWrapGone}");
                    if (!(okWrap && okWrapGone)) wrapUpFail = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("RENDER toolrows failed: " + ex.Message);
                }
                win.Close();

                // v1.42：移除确认框（右键「移除…」弹的那个）。
                try
                {
                    var dlg = AppDialog.Create(AppDialog.Kind.Question, "移除",
                        "把 BetterGI 从今晚清单移除？配置会保留，随时可以再加回来。",
                        primary: "移除", secondary: "算了");
                    bool ok4 = Save(dlg, Path.Combine(spot, "removedialog.png"), 640, 0);
                    Console.WriteLine($"RENDER removedialog={ok4}");
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("RENDER removedialog failed: " + ex.Message);
                }
            }
    }

    private void Step13()
    {
            // v1.34：插件变成导航第四项，独立一页。
            // 这一张要证明的是「它不再是一张默认收起的折叠卡」——
            // 进来就看得见目录在哪、装了什么（这里种两条：一条能读、一条读不懂），
            // 以及下面那两条「能做什么/不能做什么」。
            if (mode == "plugins" || mode == "all")
            {
                SeedSampleQueue();
                var sandbox = Path.Combine(Path.GetTempPath(), "dragon-plugins-render");
                try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
                PluginLoader.RootOverride = sandbox;
                try
                {
                    var okDir = Path.Combine(sandbox, "daily-report");
                    Directory.CreateDirectory(okDir);
                    File.WriteAllText(Path.Combine(okDir, "metadata.json"),
                        "{\"id\":\"daily-report\",\"name\":\"每天一份小结\",\"version\":\"1.2.0\"," +
                        "\"author\":\"社区\",\"needs\":\"需要 Python 3.10+\",\"hosts\":[\"api.example.com\"]}",
                        new System.Text.UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(okDir, "Plugin.dll"), "", new System.Text.UTF8Encoding(false));

                    var badDir = Path.Combine(sandbox, "half-done");
                    Directory.CreateDirectory(badDir);
                    File.WriteAllText(Path.Combine(badDir, "metadata.json"),
                        "{\"id\":\"half-done\"}", new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex) { Console.WriteLine("SEED-PLUGINS-FAIL " + ex.Message); }

                var w = new MainWindow();
                w.ShowActivated = false;
                w.Show();
                SwitchToPluginsPage(w);
                w.UpdateLayout();
                bool ok = Save(w, Path.Combine(spot, "plugins.png"), 980, 0);
                Console.WriteLine($"RENDER plugins={ok}");
                w.Close();

                PluginLoader.RootOverride = null;
                try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
            }
    }
}
