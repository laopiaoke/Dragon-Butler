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
    private void Step0()
    {
            if (mode == "minibar" || mode == "minibar_run" || mode == "all")
            {
                if (mode != "minibar") { SeedSampleArchive(); SeedRunningState(); }
                var w = new MiniBarWindow();
                // v1.13：浮条宽 180，跑起来时主行是「组名 · 已跑」，次行是「还要多久」——
                // 这两句都得塞进 180 里，只有渲染出来才知道会不会被截。
                string file = mode == "minibar_run" ? "minibar_run.png" : "minibar.png";
                bool ok = Save(w, Path.Combine(spot, file), 180, 44);
                Console.WriteLine($"RENDER {Path.GetFileNameWithoutExtension(file)}={ok}");
                w.Close();
            }
    }

    private void Step1()
    {
            if (mode == "today" || mode == "all")
            {
                var w = new TodayWindow();
                bool ok = Save(w, Path.Combine(spot, "today.png"), 720, 0);
                Console.WriteLine($"RENDER today={ok}");
                w.Close();
            }
    }

    private void Step2()
    {
            if (mode == "logrules" || mode == "all")
            {
                SeedSampleLogRules();
                var w = new LogRulesWindow();
                bool ok = Save(w, Path.Combine(spot, "logrules.png"), 720, 0);
                Console.WriteLine($"RENDER logrules={ok}");
                w.Close();
            }
    }

    private void Step3()
    {
            if (mode == "palette" || mode == "all")
            {
                var w = new CommandPalette();
                bool ok = Save(w, Path.Combine(spot, "palette.png"), 560, 0);
                Console.WriteLine($"RENDER palette={ok}");
                w.Close();
            }
    }

    private void Step4()
    {
            // v1.15：主卡五种脸各截一张。不摆道具就只能看到「先告诉我在哪」，
            // 而这张卡的全部价值在它怎么变——只截一种等于没验。
            // v1.20：多截一张 recap——复盘贴在主卡下方，光验内核不算数，
            // 得看到它跟标题、按钮挤在同一张卡里是什么样。
            // v1.22：左页（运行）单独出一张。这一页新写，内容是否挤在一起、
            // 三句话有没有说清，只有在真窗口里看才知道。
            if (mode == "runpage" || mode == "all")
            {
                SeedSampleQueue();
                SeedSampleArchive();
                SeedRecapToday();
                SeedDecisions();
                SeedHeroState("done");
                AppState.Settings.LastRunAt = DateTime.Now.AddMinutes(-49).ToString("yyyy-MM-dd HH:mm");
                AppState.Settings.LastRunSeconds = 49 * 60;
                var w = new MainWindow();
                w.UpdateLayout();
                // v1.41 / M1-1：日志不再是一页，改成了覆盖层。
                // 以前这里调 ShowPage(0) —— 0 现在是最左边的日报，所以改成直接开日志视图。
                var open = typeof(MainWindow).GetMethod("OpenLogView",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                open?.Invoke(w, new object[] { LogFilter.None });
                w.UpdateLayout();
                bool ok = Save(w, Path.Combine(spot, "runpage.png"), 980, 460);
                Console.WriteLine($"RENDER runpage={ok}");
                w.Close();
            }
    }

    private void Step5()
    {
            // v1.37：任务书要求「最小窗口 900×480 下底部导航不压住任何按钮」。
            // 960×480 是这台机器工作区的实际下界（WPF 会把窗口夹在可用高度内），
            // 用 980 宽 × 480 高近似最小的可读状态，滚动到底看最后一行。
            if (mode == "small" || mode == "all")
            {
                SeedSampleQueue();
                SeedSampleArchive();
                SeedHeroState("ready");
                var w = new MainWindow();
                // v1.40 起主窗是固定高度（Height="780"），不再 SizeToContent。
                // 以前这里要先改成 Manual，否则只设 Height 会被内容顶穿、
                // 截到的 480 只是长图上半截、底部导航在画面外，等于没验。
                w.Width = 900;
                w.Height = 480;
                w.UpdateLayout();
                var refresh = typeof(MainWindow).GetMethod("RefreshHomeHero",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                refresh?.Invoke(w, null);
                for (int pass = 0; pass < 2; pass++)
                {
                    if (w.FindName("ScrollHome") is System.Windows.Controls.ScrollViewer sh)
                    {
                        if (pass == 0) sh.ScrollToTop(); else sh.ScrollToEnd();
                    }
                    w.UpdateLayout();
                    // v1.40：导航栏会跟着滚动方向躲开。ScrollChanged 已经触发过，
                    // 但 220ms 的动画要靠消息泵推进，截图这一刻它才刚起步。
                    // 拍「稳定后的样子」才对——直接把它摆到终点，别拍到半路。
                    SettleNav(w, hide: pass != 0);
                    w.UpdateLayout();
                    string nm = pass == 0 ? "small_top.png" : "small_bottom.png";
                    bool ok = Save(w, Path.Combine(spot, nm), 900, 480);
                    Console.WriteLine($"RENDER {System.IO.Path.GetFileNameWithoutExtension(nm)}={ok}");
                }
                // 任务书要求两个尺寸都看：大窗下同样的内容不该出现新的截断或错位。
                w.Width = 1280;
                w.Height = 800;
                if (w.FindName("ScrollHome") is System.Windows.Controls.ScrollViewer sh2) sh2.ScrollToTop();
                w.UpdateLayout();
                bool big = Save(w, Path.Combine(spot, "small_wide.png"), 1280, 800);
                Console.WriteLine($"RENDER small_wide={big}");
                w.Close();
            }
    }

    private void Step6()
    {
            if (mode == "hero" || mode == "recap" || mode == "all")
            {
                var states = mode == "recap"
                    ? new[] { "recap" }
                    : new[] { "first", "ready", "running", "trouble", "done" };
                foreach (string st in states)
                {
                    SeedSampleQueue();
                    SeedSampleArchive();
                    if (mode == "recap") SeedRecapToday();
                    SeedHeroState(st == "recap" ? "done" : st);
                    // 标题那句读 LastRunAt、复盘那句读台账 At，两边差两小时会让人以为
                    // 「跑了两轮」。截图里对齐成同一轮，看着才像真的。
                    if (st == "recap")
                    {
                        AppState.Settings.LastRunAt = DateTime.Now.AddMinutes(-49).ToString("yyyy-MM-dd HH:mm");
                        AppState.Settings.LastRunSeconds = 49 * 60;
                    }
                    var w = new MainWindow();
                    w.UpdateLayout();
                    var refresh = typeof(MainWindow).GetMethod("RefreshHomeHero",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    refresh?.Invoke(w, null);
                    w.UpdateLayout();
                    // v1.15 / L3：320 高只能看到定时卡顶部，L3 的一键默认在卡片中间，
                    // 看不到就等于没验。放宽到 460。
                    string file = st == "recap" ? "recap.png" : $"hero_{st}.png";
                    bool ok = Save(w, Path.Combine(spot, file), 980, 460);
                    Console.WriteLine($"RENDER {System.IO.Path.GetFileNameWithoutExtension(file)}={ok}");
                    w.Close();
                }
            }
    }

    private void Step7()
    {
            // v1.16：新版提示挂在首屏主卡下面。这条只在真有新版时出现，
            // 所以要手动把它点亮一次——不然永远截不到，等于没验版式。
            if (mode == "update" || mode == "all")
            {
                SeedSampleQueue();
                SeedHeroState("ready");
                var w = new MainWindow();
                w.UpdateLayout();
                if (w.FindName("TbUpdateHint") is TextBlock hint)
                {
                    hint.Text = "↑ 有新版本 v1.16（你现在是 1.15.0）。";
                    hint.Tag = UpdateCheck.ReleasesUrl;
                    hint.Visibility = Visibility.Visible;
                }
                w.UpdateLayout();
                bool ok = Save(w, Path.Combine(spot, "update_hint.png"), 980, 340);
                Console.WriteLine($"RENDER update_hint={ok}");
                w.Close();
            }
    }

    private void Step8()
    {
            // v1.16：首次向导是每个新用户看到的第一个界面，从来没渲染验证过。
            // 三步都要看——第 2 步是自动寻找的列表，第 3 步是汇总，两处都是运行时生成的。
            if (mode == "wizard" || mode == "all")
            {
                SeedHeroState("ready");
                // v1.38：加了第 3 步「开机自启」，现在一共五步。
                // 第 3 步是这一版新做的界面，必须拍到它真长什么样——
                // 那两个开关用的是窗口级资源，拿不到就会静默退回控件默认样子，
                // 那种「看起来没做完」的样子只有截图能看出来。
                for (int step = 1; step <= 5; step++)
                {
                    var w = new FirstRunWindow();
                    // 步骤切换走私有方法，反射调它比自己摆一遍 Visibility 更接近真实
                    var setter = typeof(FirstRunWindow).GetField("_step",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    setter?.SetValue(w, step);
                    var update = typeof(FirstRunWindow).GetMethod("UpdateUi",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    update?.Invoke(w, null);
                    w.UpdateLayout();
                    // v1.41 / M2-9：卡片宽度写死 760，截图窗口得比它更宽，
                    // 否则截出来两边各被切一截，看着像布局坏了。
                    bool ok = Save(w, Path.Combine(spot, $"wizard_{step}.png"), 800, 0);
                    Console.WriteLine($"RENDER wizard_{step}={ok}");
                    w.Close();
                }
            }
    }

    private void Step9()
    {
            // v1.15 / L2：工具设置收成一行之后，得看两件事——
            // 收起时那一行的摘要说得对不对，展开后原来三张卡还剩没剩。
            // v1.21：桌面网格。一张就够——格子本身的形态是固定的，
            // 要看的是「三个内置 + 一个加号格」和长名字会不会把格子撑歪。
            // v1.27：桌面能翻页了，所以造两页，顺便看页码点长什么样。
            if (mode == "grid" || mode == "all")
            {
                SeedSampleQueue();
                SeedHeroState("ready");
                var w = new MainWindow();
                w.UpdateLayout();
                // 造一个名字很长的自定义工具，验证格子里的省略号
                try
                {
                    var extra = new ToolProfile
                    {
                        ToolId = "demo-long",
                        DisplayName = "一个名字特别特别特别长的第三方自动化工具用来试试省略号到底有没有生效",
                        ExecutablePath = Path.Combine(Path.GetTempPath(), "dragon-ui", "fake-bgi", "BetterGI.exe"),
                        LaunchArguments = "-t 1",
                        ManagedProcessName = "demo-long",
                    };
                    ProfileRegistry.Save(extra);
                    AppState.Settings.ToolOrder = new[] { "bettergi", "march7th", "ok-ww", "demo-long" };
                    // v1.27：桌面内容以 ToolPages 为准，ToolOrder 只是它的拍平结果。
                    // 只设 ToolOrder 会渲染出一张空的第一页——页码点也只有一个。
                    AppState.Settings.ToolPages = new List<List<string>>
                    {
                        new() { "bettergi", "march7th", "ok-ww", "demo-long" },
                        new(),
                    };
                }
                catch (Exception ex) { Console.WriteLine("SEED-GRID-FAIL " + ex.Message); }

                var render = typeof(MainWindow).GetMethod("RenderToolGrid",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                // v1.27：签名加了 bool animate，反射要按新签名传参，否则抛 TargetParameterCountException 被吞掉、格子根本没重画。
                render?.Invoke(w, new object[] { false });
                w.UpdateLayout();

                // v1.26：格子搬回启动页（中页）那张卡里——它现在就是「睡前队列」那栏，
                // 所以截图要落在中页，而不是原来那张设置页。
                // BringIntoView 在没 Show 过的窗口上不生效（没有滚动宿主测量），只能直接给偏移。
                var showPage = typeof(MainWindow).GetMethod("ShowPage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                showPage?.Invoke(w, new object[] { Pager.Home, false });
                w.UpdateLayout();
                if (w.FindName("ScrollHome") is System.Windows.Controls.ScrollViewer svGrid)
                {
                    svGrid.ScrollToVerticalOffset(0);
                    w.UpdateLayout();
                }

                // v1.27：一页 2 列 3 行（比 v1.26 少一行，多出页码点那一行），
                // 加上标题行与下面的「一键开跑」，620 够用。
                bool ok = Save(w, Path.Combine(spot, "grid.png"), 980, 640);
                Console.WriteLine($"RENDER grid={ok}");
                w.Close();
            }
    }

    private void Step10()
    {
            // v1.27：时间选择器的滚筒视图单独出一张。
            // 用户报的「数字偏上」只有在真窗口里按真字号画出来才看得见——
            // 槽位是 44 高、字号 30，差 3px 就能看出来，探针断言测不到。
            if (mode == "timepicker" || mode == "all")
            {
                var w = new Window
                {
                    Background = (Brush)(Application.Current.TryFindResource("CardBgBrush") ?? Brushes.Black),
                    Width = 420,
                    SizeToContent = SizeToContent.Height,
                };
                var ctrl = new TimePickerControl();
                ctrl.InitFrom(10, 2);
                // SetMode 是私有的：滚筒视图只能从里面切，外面没有公开入口。
                var setMode = typeof(TimePickerControl).GetMethod("SetMode",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                setMode?.Invoke(ctrl, new object[] { Enum.Parse(setMode.GetParameters()[0].ParameterType, "Drum") });
                w.Content = ctrl;
                w.UpdateLayout();
                bool ok = Save(w, Path.Combine(spot, "timepicker.png"), 420, 0);
                Console.WriteLine($"RENDER timepicker={ok}");
                w.Close();
            }
    }

    private void Step11()
    {
            // v1.28：消息中心。这一张要证明的是「崩溃也能翻成人话」——
            // 种三条假消息（一条程序自己踩空、一条权限、一条普通提醒），
            // 落盘换到临时文件，绝不碰用户真实的 notices.json。
            if (mode == "notice" || mode == "all")
            {
                string sandbox = Path.Combine(Path.GetTempPath(), "dragon-notice-render.json");
                NoticeCenter.PathOverride = sandbox;
                NoticeCenter.Reload();
                try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }

                NoticeCenter.AddCrash("上次没有正常收尾",
                    "System.NullReferenceException: 未将对象引用设置到对象的实例。\n   在 DragonButler.QueueService.StartAsync()",
                    "上次没有正常收尾，程序是被硬关掉或崩掉的");
                NoticeCenter.AddCrash("工具没能启动",
                    "System.UnauthorizedAccessException: 对路径\"C:\\Program Files\\BetterGI\\BetterGI.exe\"的访问被拒绝。\n   在 System.Diagnostics.Process.StartWithCreateProcess()",
                    "BetterGI 没能启动");
                NoticeCenter.AddError("ok-ww 没跑成",
                    "它自己退出了，但没有给出「我跑完了」的信号——可能是崩了，也可能是被别的程序关掉了。",
                    "state=Failed attempts=2 elapsed=03:41 reason=退出码 1");

                var w = new NoticeWindow();
                bool ok = Save(w, Path.Combine(spot, "notice.png"), 720, 560);
                Console.WriteLine($"RENDER notice={ok}");
                w.Close();

                NoticeCenter.PathOverride = null;
                NoticeCenter.Reload();
                try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
            }
    }
}
