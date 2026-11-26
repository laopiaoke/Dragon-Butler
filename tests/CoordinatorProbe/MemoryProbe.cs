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
public static partial class MemoryProbe
{
    private static Window? _trimTarget;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
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
