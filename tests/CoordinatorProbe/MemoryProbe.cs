using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DragonButler;

namespace CoordinatorProbe;

/// <summary>
/// 内存占用实测（v1.21 调研用，不是功能）。
///
/// <para>
/// 要回答两个问题：①「只加载点开的东西」到底能省多少；②按需分配会不会在点开的一瞬间卡一下。
/// 两个都得有数才谈得上方案，所以这里分五段量：
/// </para>
/// <list type="number">
/// <item>分段量：空进程 / 起了 WPF / 空窗口基线 / 主窗构造 / 主窗显示。</item>
/// <item>逐个窗口量：每个对话框自己值多少，关掉之后能不能还回去。</item>
/// <item>拆折叠区：把看不见的子树整块摘掉，看能回收多少——这是「按需加载」的理论上限。</item>
/// <item>关掉主窗再重建：如果关窗能把内存还回去，这条方案才成立。</item>
/// <item>开窗代价：分配量 + Gen0 次数 + 最长 GC 停顿，这三个数才回答「会不会卡」。</item>
/// </list>
/// <para>
/// 读数同时给 WorkingSet 与 PrivateBytes：前者含共享的框架页，
/// 后者才是「这个进程真的占了多少」，只看前者结论会偏大且不稳。
/// </para>
/// </summary>
public static class MemoryProbe
{
    private static Window? _trimTarget;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
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
    /// <summary>
    /// v1.21 / 内存方案 B 的干净验收：全程只做一件事——建主窗、收进托盘、销毁、再打开。
    ///
    /// 为什么不复用 <see cref="Run"/> 里那段：那一趟已经建关过十几个窗口，
    /// 堆里全是碎片和死对象，Private 的读数在 ±4 MB 之间抖，
    /// 根本量不出「销毁省了多少」。而且它把「对象没回收」和「内存没回落」混在一起看，
    /// 分不清是泄漏还是测量噪声。这里单独起一个进程级空环境，只量这一条路径。
    /// </summary>
    public static int RunTrim()
    {
        int exit = 1;
        var worker = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Startup += (_, _) =>
                {
                    try
                    {
                        Theme.Set("Dark");
                        try
                        {
                            AppState.Settings.FirstRunDone = true;
                            AppState.Settings.MiniBarEnabled = false;
                        }
                        catch { }

                        Collect();
                        long baseline = Private();
                        Console.WriteLine($"BASE 起完 WPF Private {baseline / 1048576.0:F1} MB");

                        // 三步分开量、而且每步都在独立栈帧里做：
                        // 同一个方法里持有过的引用会被 JIT 留在临时槽上，
                        // 「对象没回收」很容易是这个假象而不是真泄漏（v1.21 已经吃过一次亏）。
                        long shown = ShowAndMeasure();
                        long hidden = HideAndMeasure();
                        var weak = TrimAndMeasure();
                        long trimmed = Private();
                        Console.WriteLine($"TRIM 销毁后 Private {trimmed / 1048576.0:F1} MB（比显示少 {(shown - trimmed) / 1024.0:F0} KB）");

                        Pump();
                        Collect();
                        Pump();
                        Collect();
                        Console.WriteLine($"TRIM-ALIVE 窗口对象已被回收: {!weak.IsAlive}");

                        // 唯一的代价：下次打开要重建。
                        var sw = Stopwatch.StartNew();
                        var again = new MainWindow();
                        again.Show();
                        Pump();
                        sw.Stop();
                        Collect();
                        long reopened = Private();
                        Console.WriteLine($"REOPEN 从托盘重新打开 {sw.ElapsedMilliseconds} ms，Private {reopened / 1048576.0:F1} MB");
                        again.Close();
                        Pump();
                        Collect();

                        Console.WriteLine("===DONE===");
                        exit = 0;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("TRIM-FAIL " + ex);
                        exit = 1;
                    }
                    finally { app.Shutdown(); }
                };
                app.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine("TRIM-FAIL " + ex.Message);
                exit = 1;
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join(TimeSpan.FromSeconds(120));
        return exit;
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
                        MeasureChild<DependsWindow>("依赖");
                        MeasureChild<GroupsWindow>("配置组切片");
                        MeasureChild<WeeklyPlanWindow>("按日计划");
                        MeasureChild<CalendarWindow>("挂机日历");
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

    private static void Measure(string label)
    {
        Collect();
        string line = $"{label,-40} WS {WorkingSet() / 1048576.0,6:F1} MB   Private {Private() / 1048576.0,6:F1} MB";
        Console.WriteLine(line);
    }

    /// <summary>
    /// 在独立栈帧里建一个主窗、可选关掉，然后把引用丢掉，只返回弱引用。
    /// 放在别的栈帧里做才测得准——同方法内持有过的引用可能被 JIT 留在临时槽上，
    /// 会让「回收不掉」变成假象。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndCloseMainWindow(bool closeIt)
    {
        var w = new MainWindow();
        w.Show();
        Pump();
        if (closeIt)
        {
            // StayInTray 默认开着，Window_Closing 会把关闭改成 Hide —— 不关掉它，
            // 这里量到的永远是「没关」而不是「关不掉」。这是真机上的真实行为，
            // 也正因为如此，主窗在托盘形态下根本不会被销毁。
            try { AppState.Settings.StayInTray = false; } catch { }
            // 四个 DispatcherTimer 一直 Start 着，Tick 闭包抓着窗口本身；
            // 窗口关了 timer 不停，对象就永远回收不了。先停再关，看能不能回收。
            foreach (string f in new[] { "_scheduleTimer", "_countdownTimer", "_groupScanTimer", "_uiRefreshTimer", "_statusBarTimer" })
            {
                try
                {
                    var fi = typeof(MainWindow).GetField(f,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (fi?.GetValue(w) is System.Windows.Threading.DispatcherTimer t) t.Stop();
                }
                catch { }
            }
            w.Close();
        }
        var weak = new WeakReference(w);
        return weak;
    }

    /// <summary>量一个窗口：建之前、建之后、关掉回收之后。</summary>
    private static void MeasureChild<T>(string name, Func<Window>? factory = null) where T : Window
    {
        long before = Private();
        var sw = Stopwatch.StartNew();
        Window w;
        try
        {
            w = factory != null ? factory() : (Window)Activator.CreateInstance(typeof(T))!;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{name,-20} 建不起来：{ex.Message}");
            return;
        }
        sw.Stop();
        long afterBuild = Private();

        // 布局一次，让模板/字体真正落地——只构造不布局会低估。
        try
        {
            w.Measure(new Size(800, 900));
            w.Arrange(new Rect(0, 0, 800, 900));
            w.UpdateLayout();
        }
        catch { }
        long afterLayout = Private();

        try { w.Close(); } catch { }
        Collect();
        long afterClose = Private();

        Console.WriteLine($"{name,-20} 建 {sw.Elapsed.TotalMilliseconds,6:F1} ms   增量 {(afterLayout - before) / 1024.0,8:F0} KB   关后残留 {(afterClose - before) / 1024.0,8:F0} KB");
    }

    /// <summary>
    /// 打开一个窗口的真实代价：分配多少内存、GC 停多久、总共多少毫秒。
    /// GC 的停顿才是「卡一下」的来源，分配量只是诱因——两个都要看。
    /// </summary>
    private static void MeasureOpenCost(string name, Func<Window> build)
    {
        Collect();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        int gc0 = GC.CollectionCount(0);

        var sw = Stopwatch.StartNew();
        Window? w = null;
        try
        {
            w = build();
            w.Measure(new Size(800, 900));
            w.Arrange(new Rect(0, 0, 800, 900));
            w.UpdateLayout();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"COST {name,-12} 建不起来：{ex.Message}");
            return;
        }
        sw.Stop();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        int gcCount = GC.CollectionCount(0) - gc0;

        // 只报「开窗这段时间里真的发生了几次 GC」。
        // 不要拿 GC.GetGCMemoryInfo().PauseDurations 当停顿——那是「上一次 GC」的停顿，
        // 而上次 GC 是这行代码之前我们自己 Collect() 出来的，报出去会吓到人。
        Console.WriteLine($"COST {name,-12} 总计 {sw.Elapsed.TotalMilliseconds,6:F1} ms   分配 {allocated / 1024.0,7:F0} KB   期间 GC {gcCount} 次");

        try { w?.Close(); } catch { }
        Collect();
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>把 Dispatcher 队列跑空——Show/Hide 之后渲染是异步的，不等一拍就量不到真实值。</summary>
    private static void Pump()
    {
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ContextIdle,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Thread.Sleep(120);
            }
        }
        catch { }
    }

    /// <summary>数一棵视觉树有多少个元素——对象多不代表占内存多，但能说明「折叠省的是布局不是对象」。</summary>
    private static void CountTree(DependencyObject root, string label)
    {
        int n = 0;
        Count(root, ref n);
        Console.WriteLine($"TREE {label,-20} {n,5} 个元素");
    }

    private static void CountNamed(DependencyObject root, string name, string label)
    {
        if (root is not FrameworkElement fe || fe.FindName(name) is not DependencyObject d)
        {
            Console.WriteLine($"TREE {label,-20} 未找到 {name}");
            return;
        }
        int n = 0;
        Count(d, ref n);
        Console.WriteLine($"TREE {label,-20} {n,5} 个元素");
    }

    private static void Count(DependencyObject node, ref int n)
    {
        n++;
        int kids = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < kids; i++)
            Count(System.Windows.Media.VisualTreeHelper.GetChild(node, i), ref n);
    }

    /// <summary>把某个命名元素从父容器里摘掉——摘掉的对象没人引用后才会被回收。</summary>
    private static void Detach(DependencyObject root, string name)
    {
        if (root is not FrameworkElement fe) return;
        if (fe.FindName(name) is not FrameworkElement target) return;
        try
        {
            if (target.Parent is Panel panel) panel.Children.Remove(target);
            else if (target.Parent is ContentControl cc) cc.Content = null;
            else if (target.Parent is Decorator dec) dec.Child = null;
            else if (System.Windows.Media.VisualTreeHelper.GetParent(target) is Panel p2) p2.Children.Remove(target);
        }
        catch { }
    }

    private static long WorkingSet()
    {
        try { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.WorkingSet64; }
        catch { return 0; }
    }

    private static long Private()
    {
        try { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64; }
        catch { return 0; }
    }
}
