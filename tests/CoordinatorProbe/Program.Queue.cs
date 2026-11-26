using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using DragonButler;

namespace CoordinatorProbe;

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。
internal static partial class Program
{
    /// <summary>两/三工具队列：顺序、间隔、汇总。</summary>
    private static int RunQueue(string stub, string mode)
    {
        QueueService.IntervalOverride = TimeSpan.FromSeconds(1);
        AppState.Settings.FailStrategy = mode == "queueabort" ? "Abort" : "Skip";
        AppState.Settings.AfterRunAction = "Notify";

        var list = new List<ToolProfile>();
        if (mode == "queue")
        {
            list.Add(Stub("stub-a", stub, "--seconds 2 --exit 0"));
            list.Add(Stub("stub-b", stub, "--seconds 3 --exit 0"));
        }
        else if (mode == "queuefail")
        {
            list.Add(Stub("stub-a", stub, "--seconds 1 --exit 0"));
            list.Add(Stub("stub-b", stub, "--seconds 1 --exit 3", retry: 1));
            list.Add(Stub("stub-c", stub, "--seconds 1 --exit 0"));
        }
        else
        {
            list.Add(Stub("stub-a", stub, "--seconds 1 --exit 3", retry: 0));
            list.Add(Stub("stub-b", stub, "--seconds 1 --exit 0"));
        }

        var sw = Stopwatch.StartNew();
        QueueService.RunProfilesAsync(list, QueueTrigger.Manual).GetAwaiter().GetResult();
        sw.Stop();
        Console.WriteLine($"QUEUE mode={mode} elapsed={sw.Elapsed.TotalSeconds:F1}s");
        foreach (var r in QueueService.LastResults)
            Console.WriteLine($"RESULT {r.ToolId} state={r.State} attempts={r.Attempts} skipped={r.Skipped} reason={r.Reason}");
        return 0;
    }

    /// <summary>
    /// v1.3.1：BGI 副信号含"进程退出"后，锚点到达 + 进程正常退出应判 Done（不再空等宽限）。
    /// </summary>
    private static int RunBgiSecondary(string stub)
    {
        var profile = Stub("stub-bgi", stub, "--seconds 3 --exit 0", maxMinutes: 5,
            primary: CompletionSignalKind.LogPattern, grace: 1);
        profile.SecondarySignals = new[] { CompletionSignalKind.ProcessExit0 };

        var sw = Stopwatch.StartNew();
        var task = QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Manual);
        Thread.Sleep(1000);
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(stub) ?? "", "log");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "better-genshin-impact-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            File.AppendAllText(file, "一条龙和配置组任务结束" + Environment.NewLine);
            Console.WriteLine("FEED anchor");
        }
        catch (Exception ex) { Console.WriteLine("FEED-FAIL " + ex.Message); }

        task.GetAwaiter().GetResult();
        sw.Stop();
        var r = QueueService.LastResults.FirstOrDefault();
        Console.WriteLine($"RESULT bgisecondary state={r?.State} elapsed={sw.Elapsed.TotalSeconds:F1}s");
        return r?.State == ToolRunState.Done && sw.Elapsed.TotalSeconds < 10 ? 0 : 1;
    }

    /// <summary>悬置：主信号（日志锚点）到达但进程不退 → 宽限耗尽 Halted，队列停止不再跑下一项。</summary>
    private static int RunQueueHalted(string stub)
    {
        QueueService.IntervalOverride = TimeSpan.FromSeconds(1);
        var a = Stub("stub-log", stub, "--hang", maxMinutes: 5,
            primary: CompletionSignalKind.LogPattern, grace: 1);
        var b = Stub("stub-b", stub, "--seconds 1 --exit 0");

        var task = QueueService.RunProfilesAsync(new[] { a, b }, QueueTrigger.Manual);
        Thread.Sleep(2000);
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(stub) ?? "", "log");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "better-genshin-impact-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            File.AppendAllText(file, "一条龙和配置组任务结束" + Environment.NewLine);
            Console.WriteLine("FEED anchor");
        }
        catch (Exception ex) { Console.WriteLine("FEED-FAIL " + ex.Message); }

        task.GetAwaiter().GetResult();
        foreach (var r in QueueService.LastResults)
            Console.WriteLine($"RESULT {r.ToolId} state={r.State} skipped={r.Skipped}");
        bool halted = QueueService.LastResults.Any(r => r.State == ToolRunState.Halted);
        bool secondRan = QueueService.LastResults.Any(r => r.ToolId == "stub-b");
        Console.WriteLine($"RESULT halted={halted} secondRan={secondRan}");
        return halted && !secondRan ? 0 : 1;
    }

    /// <summary>锚点防误判：只有"任务结束"不得触发，完整锚点才触发。</summary>
    private static int RunAnchor(string stub)
    {
        string dir = Path.Combine(Path.GetDirectoryName(stub) ?? "", "log");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "better-genshin-impact-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        File.WriteAllText(file, "");

        int hits = 0;
        var watcher = new LogWatcher("一条龙和配置组任务结束", dir);
        watcher.OnEvent += ev => { if (ev.Source == "log-pattern") hits++; };
        watcher.Start();

        File.AppendAllText(file, "任务结束" + Environment.NewLine);
        for (int i = 0; i < 3; i++) { Thread.Sleep(300); watcher.Pump(); }
        Console.WriteLine("ANCHOR after-bare=" + hits);

        File.AppendAllText(file, "一条龙和配置组任务结束" + Environment.NewLine);
        for (int i = 0; i < 6 && hits == 0; i++) { Thread.Sleep(300); watcher.Pump(); }
        Console.WriteLine("ANCHOR after-full=" + hits);
        return hits == 1 ? 0 : 1;
    }

    /// <summary>超时且无锚点命中 → Timeout + WARNING 日志。</summary>
    private static int RunTimeoutAnchor(string stub)
    {
        var profile = Stub("stub-timeout", stub, "--hang", maxMinutes: 1,
            primary: CompletionSignalKind.LogPattern, grace: 0);
        QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Manual).GetAwaiter().GetResult();
        var r = QueueService.LastResults.FirstOrDefault();
        Console.WriteLine($"RESULT timeout state={r?.State} reason={r?.Reason}");
        return r?.State == ToolRunState.Timeout ? 0 : 1;
    }

    /// <summary>残留清理：先起一个 --hang 的 StubTool，定时触发应杀掉再跑。</summary>
    private static int RunResidual(string stub)
    {
        AppState.Settings.AutoKillResidual = true;
        AppState.Settings.IdleGuardEnabled = false;
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = stub,
            Arguments = "--hang",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Thread.Sleep(800);
        Console.WriteLine("RESIDUAL pre-running=" + ToolRunner.IsRunning(Stub("stub", stub, "")));

        var profile = Stub("stub", stub, "--seconds 1 --exit 0");
        QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Timer).GetAwaiter().GetResult();
        var r = QueueService.LastResults.FirstOrDefault();
        Console.WriteLine($"RESULT residual state={r?.State} skipped={r?.Skipped}");
        try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
        return r?.State == ToolRunState.Done ? 0 : 1;
    }

    /// <summary>游戏进程冲突：起一个名为 StarRail 的替身 → 跳过。</summary>
    private static int RunConflict(string stub)
    {
        string dir = Path.Combine(Path.GetTempPath(), "db_conflict");
        Directory.CreateDirectory(dir);
        string fake = Path.Combine(dir, "StarRail.exe");
        // 用独立可执行文件做替身（StubTool 是 apphost，单独拷走会缺少 dll 起不来）
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fake, true);
        var p = Process.Start(new ProcessStartInfo { FileName = fake, Arguments = "/c ping -n 12 127.0.0.1 >nul", UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(800);

        var profile = Stub("stub", stub, "--seconds 1 --exit 0");
        profile.GameProcessNames = new[] { "StarRail" };
        QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Manual).GetAwaiter().GetResult();
        var r = QueueService.LastResults.FirstOrDefault();
        Console.WriteLine($"RESULT conflict skipped={r?.Skipped} reason={r?.Reason}");
        try { if (p is { HasExited: false }) p.Kill(); } catch { }
        return r?.Skipped == true ? 0 : 1;
    }

    /// <summary>参数模板与档案字段校验（不启动进程）。</summary>
    private static int RunArgs()
    {
        ProfileRegistry.Initialize();
        foreach (var p in ProfileRegistry.All)
        {
            Console.WriteLine($"PROFILE {p.ToolId} name={p.DisplayName} proc={p.ManagedProcessName} " +
                              $"games={string.Join("/", p.GameProcessNames)} primary={p.PrimarySignal} " +
                              $"max={p.MaxDurationMinutes} grace={p.PostExitGraceMinutes}");
        }
        var bgi = ProfileRegistry.BetterGi;
        Console.WriteLine("ARGS empty=[" + ToolRunner.BuildArguments(bgi, "") + "]");
        Console.WriteLine("ARGS named=[" + ToolRunner.BuildArguments(bgi, "日常一条龙") + "]");
        Console.WriteLine("ARGS march=" + ToolRunner.BuildArguments(ProfileRegistry.Get("march7th"), ""));
        Console.WriteLine("ARGS okww=" + ToolRunner.BuildArguments(ProfileRegistry.Get("ok-ww"), ""));
        Console.WriteLine("ARGS port=" + ProfileRegistry.BetterGiWebhookPort);
        return 0;
    }

}
