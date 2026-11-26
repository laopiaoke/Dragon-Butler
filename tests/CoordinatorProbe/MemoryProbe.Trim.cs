using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DragonButler;

namespace CoordinatorProbe;

// v1.45.3 / #15：内存调研探针按用途拆成多个 partial 文件，单文件 <= 20 KB。
// 量测逻辑一字未改，只换了所在文件。
public static partial class MemoryProbe
{
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
}
