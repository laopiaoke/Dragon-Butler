using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DragonButler;

namespace CoordinatorProbe;

// v1.45.3 / #15：内存探针的量测底座（工作集/私有字节、显隐一次、摘子树），≤ 20 KB。
// 量测逻辑一字未改。
public static partial class MemoryProbe
{
    private static long ShowAndMeasure()
    {
        var w = new MainWindow();
        w.Show();
        Pump();
        Collect();
        _trimTarget = w;
        long shown = Private();
        Console.WriteLine($"SHOW 主窗画到屏幕上 Private {shown / 1048576.0:F1} MB");
        return shown;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long HideAndMeasure()
    {
        var w = _trimTarget;
        if (w == null) return 0;
        try { AppState.Settings.StayInTray = true; } catch { }
        w.Close();          // Window_Closing 会把它改成 Hide —— 这就是真实行为
        Pump();
        Collect();
        long hidden = Private();
        Console.WriteLine($"HIDE 收进托盘 Private {hidden / 1048576.0:F1} MB（IsVisible={w.IsVisible}）");
        return hidden;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference TrimAndMeasure()
    {
        var w = _trimTarget ?? new MainWindow();
        _trimTarget = null;
        try
        {
            typeof(MainWindow)
                .GetMethod("PrepareForTrim", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(w, null);
        }
        catch (Exception ex) { Console.WriteLine("TRIM-PREP-FAIL " + ex.Message); }

        try { AppState.Settings.StayInTray = false; } catch { }
        var weak = new WeakReference(w);
        w.Close();
        // Application.MainWindow 强引用着它；不清掉这根引用，对象永远回收不了。
        try { Application.Current.MainWindow = null; } catch { }
        return weak;
    }

    public static int Run()
    {
        int exit = 1;
        var worker = new Thread(() =>
        {
            try
            {
                Measure("0 空进程（只有 .NET 运行时 + 探针）");

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Startup += (_, _) =>
                {
                    try
                    {
                        Measure("1 起了 WPF Application");

                        Theme.Set("Dark");
                        Measure("2 装好主题令牌");

                        // 基线必须先量：一个空窗口值多少。放在主窗之前，
                        // 否则 WPF 的固定开销已经摊进主窗的增量里，两头都说不清。
                        var empty = new Window { Width = 980, Height = 700 };
                        empty.Content = new TextBlock { Text = "x" };
                        empty.Measure(new Size(980, 700));
                        empty.Arrange(new Rect(0, 0, 980, 700));
                        empty.UpdateLayout();
                        Measure("2b 空窗口基线（只有一个 TextBlock）");
                        empty.Close();
                        Collect();

                        // 向导是模态的，Show() 会被它堵死；探针里先当「已经配好了」。
                        try
                        {
                            AppState.Settings.FirstRunDone = true;
                            AppState.Settings.MiniBarEnabled = false;
                        }
                        catch { }

                        var sw = Stopwatch.StartNew();
                        var main = new MainWindow();
                        sw.Stop();
                        Measure($"3 建完主窗（构造 {sw.ElapsedMilliseconds} ms，未显示）");

                        sw.Restart();
                        main.Measure(new Size(980, 2000));
                        main.Arrange(new Rect(0, 0, 980, 2000));
                        main.UpdateLayout();
                        sw.Stop();
                        Measure($"4 布局一遍（{sw.ElapsedMilliseconds} ms）");

                        // 窗口「显示出来」和「只构造」差多少。真机上主窗是显示的，
                        // 探针里不显示——这个差额决定「探针数字」能不能当结论用。
                        main.Show();
                        Pump();
                        Measure("4b 主窗 Show + 渲染完成");
                        long shown = Private();

                        main.Hide();
                        Pump();
                        Measure("4c 主窗隐藏（只剩托盘形态）");
                        Console.WriteLine($"SHOW 显示/隐藏差 {(shown - Private()) / 1024.0:F0} KB");

                        // 决定方案能不能成立的一测：关掉主窗并把引用丢掉，那些内存还得了吗？
                        // 注意必须真的把引用置 null——只 Close 不丢引用，GC 回收不了任何东西，
                        // 那样量出来的是「没还」，而不是「还不掉」。
                        main.Close();
                        Pump();
                        Measure("4d-1 主窗关掉（引用还在）");
                        WeakReference weak = new(main);
                        main = null!;
                        Collect();
                        Pump();
                        Collect();
                        Console.WriteLine($"4d-2 引用置 null 后对象是否已回收: {!weak.IsAlive}");
                        Measure("4d-3 主窗彻底释放并回收");

                        // 上面那条测不准：同一个方法里持有过引用，JIT 可能把临时槽留在栈上，
                        // 于是「还不掉」也可能是假象。改成在独立方法里开、关、丢引用，
                        // 让栈帧真的弹掉，再看对象到底能不能被回收。
                        var weak3 = OpenAndCloseMainWindow(closeIt: true);
                        Collect();
                        Pump();
                        Collect();
                        Console.WriteLine($"4f 独立方法里开关后，主窗是否已回收: {!weak3.IsAlive}");

                        var weak4 = OpenAndCloseMainWindow(closeIt: false);
                        Collect();
                        Pump();
                        Collect();
                        Console.WriteLine($"4g 对照（只建不关）是否已回收: {!weak4.IsAlive}");

                        // 「能回收」还要看「回收多少」。上面证明了对象能释放，
                        // 但 Private 到底回落多少才是方案的实际收益。
                        Collect();
                        Pump();
                        long beforeRound = Private();
                        var weakRound = OpenAndCloseMainWindow(closeIt: true);
                        Collect();
                        Pump();
                        Collect();
                        long afterRound = Private();
                        Console.WriteLine($"ROUND 开一次主窗再关掉，净增 {(afterRound - beforeRound) / 1024.0:F0} KB（已回收={!weakRound.IsAlive}）");

                        // 常驻服务的开销：托盘、Webhook、哨兵各自值多少。
                        // 这些是「即使你不开窗也在跑」的部分，也是唯一能靠延迟启动省下的。
                        long svc = Private();
                        try { DragonButler.WebhookHub.Start(1, false); } catch { }
                        Measure("5 起了 Webhook（端口 1，仅测开销）");
                        Console.WriteLine($"SVC Webhook 增量 {(Private() - svc) / 1024.0:F0} KB");

                        var rebuild = Stopwatch.StartNew();
                        var main2 = new MainWindow();
                        main2.Show();
                        Pump();
                        rebuild.Stop();
                        Measure($"4e 重建主窗并显示（{rebuild.ElapsedMilliseconds} ms）");
                        main2.Close();
                        Pump();
                        Collect();

                        // 折叠区到底占多少：Collapsed 只省布局不省对象，
                        // 所以「按需加载」唯一可能省到的地方就是这些建了却看不见的子树。
                        // 必须在 Show 之后数，否则视觉树还没建，数出来只有 1 个元素。
                        var probeTree = new MainWindow();
                        probeTree.Show();
                        Pump();
                        CountTree(probeTree, "主窗合计");
                        CountNamed(probeTree, "AdvancedBody", "高级设置（折叠）");
                        CountNamed(probeTree, "SystemBody", "系统（折叠）");
                        CountNamed(probeTree, "NotifyBody", "通知（折叠）");
                        CountNamed(probeTree, "ToolCardsHost", "工具设置（折叠）");

                        long capped = Private();
                        Detach(probeTree, "ToolCardsHost");
                        Detach(probeTree, "AdvancedBody");
                        Detach(probeTree, "SystemBody");
                        Detach(probeTree, "NotifyBody");
                        Pump();
                        Collect();
                        long afterDetach = Private();
                        Console.WriteLine($"DETACH 四个折叠区拆掉后回收 {(capped - afterDetach) / 1024.0:F0} KB（拆前 {capped / 1024.0:F0} KB）");
                        probeTree.Close();
                        Pump();
                        Collect();

                        // 各子窗口单独量：新建 → 量 → 关 → 回收 → 再量。
                        // 差值就是「这个窗口值多少」，关后的残留说明内存能不能还回去。
                        // v1.42.1 / M9-4：依赖 / 配置组切片 / 按日计划 / 挂机日历
                        // 四个窗口已按 D18 删除，不再有可量的对象。
                        MeasureChild<TodayWindow>("今日运行");
                        MeasureChild<LogRulesWindow>("日志规则");
                        MeasureChild<MiniBarWindow>("迷你浮条");
                        MeasureChild<PreflightWindow>("开跑前体检",
                            () => new PreflightWindow(() => null, _ => { }));
                        MeasureChild<HandoverWindow>("出门前看一眼",
                            () => new HandoverWindow(_ => null, _ => null));
                        MeasureChild<TidyWindow>("跑完收拾一下");
                        MeasureChild<PresetsWindow>("配置组合");

                        // 反复开关同一个窗口：看会不会越开越大（句柄/事件泄漏的典型症状）。
                        long before = Private();
                        for (int i = 0; i < 5; i++)
                        {
                            var w = new TodayWindow();
                            w.Measure(new Size(720, 800));
                            w.Close();
                        }
                        Collect();
                        Console.WriteLine($"REPEAT 今日运行 ×5 后增量 {(Private() - before) / 1024.0:F0} KB");

                        // 卡顿的量化：开一个窗口要分配多少、触发几次 GC、最长一次停多久。
                        // 「按需加载会不会卡」的答案全在这三个数里。
                        MeasureOpenCost("今日运行", () => new TodayWindow());
                        MeasureOpenCost("开跑前体检", () => new PreflightWindow(() => null, _ => { }));
                        MeasureOpenCost("迷你浮条", () => new MiniBarWindow());

                        Console.WriteLine("===DONE===");
                        exit = 0;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("MEM-FAIL " + ex);
                        exit = 1;
                    }
                    finally
                    {
                        app.Shutdown();
                    }
                };
                app.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine("MEM-FAIL " + ex.Message);
                exit = 1;
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join(TimeSpan.FromSeconds(180));
        return exit;
    }

}
