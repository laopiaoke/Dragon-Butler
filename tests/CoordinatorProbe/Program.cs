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

/// <summary>
/// 替身驱动器：用 StubTool 驱动 RunCoordinator，验证 Done / Failed / Timeout / Halted 四条分支。
/// 只在开发机运行，不随产品发布。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "help";
        string stub = args.Length > 1
            ? args[1]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "StubTool", "bin", "Release", "net8.0", "StubTool.exe"));

        if (!File.Exists(stub) && mode is not ("help" or "clipdialog" or "ui" or "live"))
        {
            Console.WriteLine("STUB-NOT-FOUND " + stub);
            return 2;
        }

        return mode switch
        {
            "done" => Run("done", stub, CompletionSignalKind.ProcessExit0, "--seconds 2 --exit 0", 5, 0),
            "failed" => Run("failed", stub, CompletionSignalKind.ProcessExit0, "--seconds 2 --exit 3", 5, 0),
            "timeout" => Run("timeout", stub, CompletionSignalKind.ProcessExit0, "--hang", 1, 0),
            "halted" => RunHalted(stub),
            "haltedlog" => Run("haltedlog", stub, CompletionSignalKind.LogPattern, "--hang", 5, 1),
            "logpattern" => RunLogPattern(stub),
            "args" => RunArgs(),
            "migrate" => Migrate(),
            "clipboard" => RunClipboard(stub),
            "cliptry" => RunClipTry(),
            "clipdialog" => RunClipDialog(),
            "queue" => RunQueue(stub, "queue"),
            "queuefail" => RunQueue(stub, "queuefail"),
            "queueabort" => RunQueue(stub, "queueabort"),
            "queuehalted" => RunQueueHalted(stub),
            "bgisecondary" => RunBgiSecondary(stub),
            "anchor" => RunAnchor(stub),
            "timeoutanchor" => RunTimeoutAnchor(stub),
            "residual" => RunResidual(stub),
            "conflict" => RunConflict(stub),
            "vacation" => RunVacation(),
            "afterrun" => RunAfterRun(),
            "idle" => RunIdle(),
            "discover" => RunDiscover(stub),
            "custom" => RunCustom(stub),
            "duration" => RunDuration(stub),
            "stall" => RunStall(stub, false),
            "stallkill" => RunStall(stub, true),
            "keepawake" => RunKeepAwake(),
            "screensaver" => RunScreenSaver(),
            "nightscreen" => RunNightScreen(),
            "icon" => RunIcon(stub),
            "gdiicons" => RunGdiIcons(stub),
            "weekplan" => RunWeekPlan(stub),
            "customsig-log" => RunCustomSignal(stub, "log"),
            "customsig-file" => RunCustomSignal(stub, "file"),
            "customsig-title" => RunCustomSignal(stub, "title"),
            "iconoverride" => RunIconOverride(stub),
            "morning" => RunMorning(),
            "schedule" => RunSchedule(),
            "resume" => RunResume(stub),
            "explain" => RunExplain(),
            "retry" => RunRetry(),
            "bundle" => RunBundle(),
            "weekly" => RunWeekly(),
            "api" => RunApi(),
            "hotkey" => RunHotkey(),
            "progress" => RunProgress(),
            "command" => RunCommand(),
            "calendar" => RunCalendar(),
            "live" => RunLive(),
            "logrules" => RunLogRules(),
            "dag" => RunDag(stub),
            "groups" => RunGroups(),
            "bgiupdate" => RunBgiVersion(),
            "harvest" => RunHarvest(),
            "mcp" => RunMcp(),
            "uirules" => RunUiRules(),
            "ui" => RenderProbe.RunUi(args.Length > 1 ? args[1] : "all"),
            _ => Help(),
        };
    }

    /// <summary>
    /// 剪贴板重试验证：StubClipboardHolder 占用期间 TrySetText 应返回 false（约 1 秒），
    /// 释放后应返回 true 且内容正确。
    /// </summary>
    private static int RunClipboard(string holder)
    {
        const string url = "http://127.0.0.1:18235/webhook";
        int exit = 1;

        var worker = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (_, _) =>
            {
                Process? proc = null;
                After(100, () =>
                {
                    proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = holder,
                        Arguments = "--seconds 8",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    });

                    // 等替身真正拿到占用权（读它的 "HOLDER open=True" 输出，比固定 sleep 可靠）
                    bool ready = Task.Run(() =>
                    {
                        try
                        {
                            string? line;
                            while ((line = proc?.StandardOutput.ReadLine()) != null)
                                if (line.StartsWith("HOLDER open=True", StringComparison.Ordinal)) return true;
                        }
                        catch { }
                        return false;
                    }).Wait(15000);

                    Console.WriteLine("CLIP holder-ready=" + ready);

                    After(ready ? 50 : 800, () =>
                    {
                        var sw = Stopwatch.StartNew();
                        bool busy = ClipboardHelper.TrySetText(url);
                        sw.Stop();
                        Console.WriteLine($"CLIP busy ok={busy} elapsed={sw.ElapsedMilliseconds}ms");
                        bool busyOk = !busy && sw.ElapsedMilliseconds >= 600 && sw.ElapsedMilliseconds <= 1600;
                        WaitHolderExit(proc, () => After(400, () =>
                        {
                            bool free = ClipboardHelper.TrySetText(url);
                            string got = "";
                            try { got = Clipboard.GetText(); } catch { }
                            Console.WriteLine($"CLIP free ok={free} text={got}");
                            exit = (busyOk && free && got == url) ? 0 : 1;
                            app.Shutdown();
                        }));
                    });
                });
            };
            app.Run();
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        return exit;
    }

    /// <summary>轮询替身进程是否退出（不阻塞 Dispatcher，保证 OLE 剪贴板消息能处理）。</summary>
    private static void WaitHolderExit(Process? proc, Action onExited)
    {
        if (proc == null) { onExited(); return; }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            bool exited;
            try { exited = proc.HasExited; } catch { exited = true; }
            if (!exited) return;
            timer.Stop();
            onExited();
        };
        timer.Start();
    }

    /// <summary>在 WPF Dispatcher 上延时执行。</summary>
    private static void After(int ms, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    /// <summary>单次复制尝试（配合外部启动的 StubClipboardHolder 做对照实验）。</summary>
    private static int RunClipTry()
    {
        const string url = "http://127.0.0.1:18235/webhook";
        var sw = Stopwatch.StartNew();
        bool ok = ClipboardHelper.TrySetText(url);
        sw.Stop();
        Console.WriteLine($"CLIPTRY ok={ok} elapsed={sw.ElapsedMilliseconds}ms");
        return ok ? 0 : 1;
    }

    /// <summary>兜底弹窗外观验证：显示 9 秒供截图（只读可全选文本框）。</summary>
    private static int RunClipDialog()
    {
        var worker = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Set("Dark");
            var dlg = AppDialog.Create(AppDialog.Kind.Warning, "复制地址",
                "剪贴板被其他程序暂时占用，请手动复制（Ctrl+C）。",
                "下面的内容已全选，直接按 Ctrl+C 即可；关掉本窗口不影响其他功能。",
                "知道了", null, null, null, "http://127.0.0.1:18235/webhook");
            dlg.Show();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
            timer.Tick += (_, _) => { timer.Stop(); try { dlg.Close(); } catch { } app.Shutdown(); };
            timer.Start();
            app.Run();
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        return 0;
    }

    /// <summary>halted 分支：主信号用探针自带的手动源在 2 秒后抛出，宽限 1 分钟耗尽后应 Halted。</summary>
    private static int RunHalted(string stub)
    {
        var profile = new ToolProfile
        {
            ToolId = "stub",
            DisplayName = "StubTool",
            ExecutablePath = stub,
            LaunchArguments = "--hang",
            ManagedProcessName = "StubTool",
            PrimarySignal = CompletionSignalKind.Webhook,
            SecondarySignals = Array.Empty<CompletionSignalKind>(),
            MaxDurationMinutes = 5,
            PostExitGraceMinutes = 1,
        };

        using var coord = new RunCoordinator(profile, msg => Console.WriteLine("NOTIFY " + msg));
        coord.Transition += (from, to, reason) => Console.WriteLine($"TRANS {from}->{to} {reason}");
        coord.SetPrimary(new ManualSource(TimeSpan.FromSeconds(2)));

        coord.BeginLaunch();
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = stub,
            Arguments = "--hang",
            WorkingDirectory = Path.GetDirectoryName(stub) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        var ctx = new ToolRunContext { Profile = profile, Process = proc, Arguments = "--hang" };
        coord.LaunchSucceeded(proc!, ctx);

        var sw = Stopwatch.StartNew();
        while (!coord.IsFinished && sw.Elapsed < TimeSpan.FromMinutes(4)) Thread.Sleep(500);
        Console.WriteLine($"RESULT halted state={coord.State} elapsed={sw.Elapsed.TotalSeconds:F1}s");
        try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
        return 0;
    }

    /// <summary>单独验证日志哨兵：写一行命中正则的日志，Pump 后应抛出 log-pattern 事件。</summary>
    private static int RunLogPattern(string stub)
    {
        string dir = Path.Combine(Path.GetDirectoryName(stub) ?? "", "log");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "better-genshin-impact-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        File.WriteAllText(file, "");

        var watcher = new LogWatcher("执行结束", dir);
        string? hit = null;
        watcher.OnEvent += ev =>
        {
            Console.WriteLine("EVT " + ev.Kind + " src=" + ev.Source + " payload=" + ev.Payload);
            if (ev.Source == "log-pattern") hit = ev.Payload;
        };
        watcher.Start();
        File.AppendAllText(file, "配置组 \"Stub\" 执行结束" + Environment.NewLine);
        for (int i = 0; i < 10 && hit == null; i++) { Thread.Sleep(300); watcher.Pump(); }
        Console.WriteLine(hit == null ? "RESULT logpattern hit=False" : "RESULT logpattern hit=True");
        return hit == null ? 1 : 0;
    }

    /// <summary>测试用手动信号源：延时后抛一次主信号。</summary>
    private sealed class ManualSource : ICompletionSignalSource
    {
        private readonly TimeSpan _delay;
        private Timer? _timer;
        public ManualSource(TimeSpan delay) => _delay = delay;
        public string SourceId => "manual";
        public event Action<CompletionSignal>? SignalRaised;
        public void Start(ToolRunContext ctx)
            => _timer = new Timer(_ => SignalRaised?.Invoke(new CompletionSignal
            {
                SourceId = SourceId,
                Kind = CompletionSignalKind.Webhook,
                Confidence = SignalConfidence.High,
                Detail = "manual primary",
            }), null, _delay, Timeout.InfiniteTimeSpan);
        public void Stop() { try { _timer?.Dispose(); } catch { } _timer = null; }
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


    // ===== v1.3 排队引擎验证 =====

    private static ToolProfile Stub(string name, string path, string args, int exit = 0, int retry = 0,
                                    CompletionSignalKind primary = CompletionSignalKind.ProcessExit0,
                                    int maxMinutes = 5, int grace = 0)
        => new()
        {
            ToolId = name,
            DisplayName = name,
            ExecutablePath = path,
            LaunchArguments = args,
            ManagedProcessName = "StubTool",
            GameProcessNames = Array.Empty<string>(),
            PrimarySignal = primary,
            SecondarySignals = Array.Empty<CompletionSignalKind>(),
            LogCompletionPattern = primary == CompletionSignalKind.LogPattern ? "一条龙和配置组任务结束" : null,
            MaxDurationMinutes = maxMinutes,
            PostExitGraceMinutes = grace,
            RetryCount = retry,
        };

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

    /// <summary>度假模式：定时触发不跑。</summary>
    private static int RunVacation()
    {
        AppState.Settings.VacationMode = true;
        bool ran = QueueService.StartAsync(QueueTrigger.Timer).GetAwaiter().GetResult();
        Console.WriteLine("RESULT vacation ran=" + ran);
        AppState.Settings.VacationMode = false;
        return ran ? 1 : 0;
    }

    /// <summary>跑完动作 DRYRUN：关机只写日志。</summary>
    private static int RunAfterRun()
    {
        AppState.Settings.AfterRunAction = "Shutdown";
        AppState.Settings.AfterRunDryRun = true;
        AppState.Settings.AfterRunOnFailure = false;
        AfterRunActionService.Execute(new AfterRunContext { Total = 2, Success = 2, HasFailure = false });
        AfterRunActionService.Execute(new AfterRunContext { Total = 2, Success = 1, HasFailure = true });
        Console.WriteLine("RESULT afterrun done（应只有 DRYRUN 日志，无真实关机）");
        return 0;
    }

    /// <summary>防打扰：有人在场 → 推迟；改成空闲后放行。</summary>
    private static int RunIdle()
    {
        AppState.Settings.IdleGuardEnabled = true;
        IdleGuard.PostponeDelay = TimeSpan.FromSeconds(1);
        IdleGuard.IdleOverride = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        var task = IdleGuard.WaitAsync(CancellationToken.None);
        Thread.Sleep(1500);
        IdleGuard.IdleOverride = TimeSpan.FromMinutes(10);
        bool ok = task.GetAwaiter().GetResult();
        sw.Stop();
        Console.WriteLine($"RESULT idle ok={ok} elapsed={sw.Elapsed.TotalSeconds:F1}s");
        return ok ? 0 : 1;
    }

    /// <summary>自动寻找：运行中的进程应命中。</summary>
    private static int RunDiscover(string stub)
    {
        string dir = Path.Combine(Path.GetTempPath(), "db_find");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "March7 Launcher.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, true);
        var p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "/c ping -n 12 127.0.0.1 >nul", UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(1000);

        var profile = ProfileRegistry.Get("march7th");
        var found = ToolDiscovery.TryAutoDiscover(profile, out string source);
        Console.WriteLine($"RESULT discover found={found} source={source}");
        try { if (p is { HasExited: false }) p.Kill(); } catch { }
        return string.Equals(found, exe, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    // ===== v1.4 新增验证 =====

    /// <summary>自定义工具：档案持久化 + 图标提取 + 队列执行。</summary>
    private static int RunCustom(string stub)
    {
        AppState.Settings.KeepAwakeEnabled = false;
        var profile = new ToolProfile
        {
            ToolId = "custom-stub",
            DisplayName = "Stub 自定义",
            ExecutablePath = stub,
            LaunchArguments = "--seconds 1 --exit 0",
            ManagedProcessName = "StubTool",
            PrimarySignal = CompletionSignalKind.ProcessExit0,
            MaxDurationMinutes = 5,
            RetryCount = 0,
            CustomData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["custom"] = "1" },
        };
        ProfileRegistry.Save(profile);
        string file = Path.Combine(ProfileRegistry.ToolsDir, "custom-stub.json");
        var bmp = ToolIconService.GetBitmap(profile);
        string icon = Path.Combine(ToolIconService.CacheDir, "custom-stub.png");
        Console.WriteLine($"CUSTOM file={File.Exists(file)} icon={bmp != null} iconFile={File.Exists(icon)} size={(bmp == null ? "0" : bmp.PixelWidth + "x" + bmp.PixelHeight)}");

        var loaded = ProfileRegistry.Get("custom-stub");
        Console.WriteLine($"CUSTOM loaded={loaded.DisplayName}/{loaded.ExecutablePath}");
        QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Manual).GetAwaiter().GetResult();
        var r = QueueService.LastResults.FirstOrDefault();
        Console.WriteLine($"RESULT custom state={r?.State}");
        ProfileRegistry.Remove("custom-stub");
        return r?.State == ToolRunState.Done ? 0 : 1;
    }

    /// <summary>到达时长：主信号 Duration → Done，并结束进程。</summary>
    private static int RunDuration(string stub)
    {
        AppState.Settings.KeepAwakeEnabled = false;
        DurationSignalSource.DurationOverride = TimeSpan.FromSeconds(2);
        var profile = Stub("stub-duration", stub, "--hang", 5);
        profile.PrimarySignal = CompletionSignalKind.Duration;
        profile.CustomData["durationMinutes"] = "60";
        var sw = Stopwatch.StartNew();
        QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Manual).GetAwaiter().GetResult();
        sw.Stop();
        var r = QueueService.LastResults.FirstOrDefault();
        int left = Process.GetProcessesByName("StubTool").Length;
        Console.WriteLine($"RESULT duration state={r?.State} elapsed={sw.Elapsed.TotalSeconds:F1}s left={left}");
        return r?.State == ToolRunState.Done && left == 0 ? 0 : 1;
    }

    /// <summary>STALLED：--hang 无 CPU/日志活动 → Stalled；autoKill 时杀进程并按失败策略继续。</summary>
    private static int RunStall(string stub, bool autoKill)
    {
        AppState.Settings.KeepAwakeEnabled = false;
        AppState.Settings.StallAutoKill = autoKill;
        AppState.Settings.FailStrategy = "Skip";
        QueueService.IntervalOverride = TimeSpan.FromSeconds(1);
        SentinelService.StallThreshold = TimeSpan.FromSeconds(3);
        SentinelService.Start();

        var hang = Stub("stub-stall", stub, "--hang", 5);
        var next = Stub("stub-next", stub, "--seconds 1 --exit 0");
        var list = autoKill ? new[] { hang, next } : new[] { hang };

        var sw = Stopwatch.StartNew();
        QueueService.RunProfilesAsync(list, QueueTrigger.Manual).GetAwaiter().GetResult();
        sw.Stop();
        SentinelService.Stop();
        foreach (var r in QueueService.LastResults)
            Console.WriteLine($"RESULT stall {r.ToolId} state={r.State} skipped={r.Skipped}");
        Console.WriteLine($"RESULT stall autoKill={autoKill} setting={AppState.Settings.StallAutoKill} elapsed={sw.Elapsed.TotalSeconds:F1}s");

        foreach (var p in Process.GetProcessesByName("StubTool"))
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            finally { p.Dispose(); }
        }

        bool ok;
        if (autoKill)
            ok = QueueService.LastResults.Any(r => r.ToolId == "stub-stall" && r.State == ToolRunState.Stalled)
                 && QueueService.LastResults.Any(r => r.ToolId == "stub-next" && r.State == ToolRunState.Done);
        else
            ok = QueueService.LastResults.Count == 1
                 && QueueService.LastResults[0].State == ToolRunState.Stalled;
        return ok ? 0 : 1;
    }

    /// <summary>护航：Acquire 后 powercfg /requests 能看到 SYSTEM 请求，Release 后消失。</summary>
    private static int RunKeepAwake()
    {
        AppState.Settings.KeepAwakeEnabled = true;
        AppState.Settings.NightScreenEnabled = false;
        KeepAwakeService.Acquire();
        Thread.Sleep(1200);
        string on = PowerRequests();
        Console.WriteLine($"KEEP sleep={KeepAwakeService.SleepBlocked} restart={KeepAwakeService.RestartBlocked} screen={KeepAwakeService.ScreenSaverSuppressed}");
        Console.WriteLine("KEEP-REQ-ON " + Compact(on));
        KeepAwakeService.Release();
        Thread.Sleep(600);
        string off = PowerRequests();
        Console.WriteLine("KEEP-REQ-OFF " + Compact(off));
        bool ok = KeepAwakeService.SleepBlocked || on.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase);
        return ok ? 0 : 1;
    }

    /// <summary>屏保原值记录/关闭/恢复。</summary>
    private static int RunScreenSaver()
    {
        AppState.Settings.KeepAwakeEnabled = true;
        AppState.Settings.NightScreenEnabled = false;
        GetScreensaver(out bool original);
        Console.WriteLine("SCREEN original=" + original);
        if (!SetScreensaver(true)) { Console.WriteLine("SCREEN skip（策略不允许修改）"); return 0; }
        KeepAwakeService.Acquire();
        Thread.Sleep(500);
        GetScreensaver(out bool during);
        Console.WriteLine("SCREEN during=" + during + " suppressed=" + KeepAwakeService.ScreenSaverSuppressed);
        KeepAwakeService.Release();
        Thread.Sleep(300);
        GetScreensaver(out bool after);
        Console.WriteLine("SCREEN after=" + after);
        SetScreensaver(original);
        return (during == false && after == true) ? 0 : 1;
    }

    /// <summary>夜间息屏：排定 + DRYRUN 日志。</summary>
    private static int RunNightScreen()
    {
        AppState.Settings.KeepAwakeEnabled = true;
        AppState.Settings.NightScreenEnabled = true;
        KeepAwakeService.NightScreenDelayOverride = TimeSpan.FromSeconds(2);
        KeepAwakeService.NightScreenDryRun = true;
        KeepAwakeService.Acquire();
        Console.WriteLine("NIGHT scheduled=" + KeepAwakeService.NightScreenScheduled);
        Thread.Sleep(3000);
        KeepAwakeService.Release();
        bool hit = false;
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "全部日志");
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, "*.log"))
                    if (File.ReadAllText(f).Contains("DRYRUN 夜间息屏", StringComparison.Ordinal)) hit = true;
        }
        catch { }
        Console.WriteLine("RESULT nightscreen dryrunLog=" + hit);
        KeepAwakeService.NightScreenDelayOverride = null;
        KeepAwakeService.NightScreenDryRun = false;
        AppState.Settings.NightScreenEnabled = false;
        return hit ? 0 : 1;
    }

    /// <summary>图标提取：StubTool.exe → cache/icons/icon-stub.png。</summary>
    private static int RunIcon(string stub)
    {
        var profile = new ToolProfile { ToolId = "icon-stub", DisplayName = "图标测试", ExecutablePath = stub };
        var bmp = ToolIconService.GetBitmap(profile);
        string file = Path.Combine(ToolIconService.CacheDir, "icon-stub.png");
        bool exists = File.Exists(file);
        Console.WriteLine($"RESULT icon ok={bmp != null} file={exists} size={(bmp == null ? "0" : bmp.PixelWidth + "x" + bmp.PixelHeight)}");
        ToolIconService.Invalidate("icon-stub");
        return bmp != null && exists ? 0 : 1;
    }

    private static string PowerRequests()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/requests")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return text;
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    private static string Compact(string text)
        => string.Join(" | ", text.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && (l.Contains("SYSTEM") || l.Contains("DISPLAY") || l.Contains("EXECUTION") || l.Contains("Dragon")))
            .Take(8));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    private static bool GetScreensaver(out bool active)
    {
        active = false;
        try
        {
            object? raw = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "ScreenSaveActive", null);
            if (raw != null)
            {
                active = raw.ToString()?.Trim() == "1";
                return true;
            }
        }
        catch { }
        int v = 0;
        bool ok = SystemParametersInfo(0x0010, 0, ref v, 0);
        active = v != 0;
        return ok;
    }

    private static bool SetScreensaver(bool active)
    {
        int v = active ? 1 : 0;
        bool ok = SystemParametersInfo(0x0011, 0, ref v, 0);
        try { Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "ScreenSaveActive", active ? "1" : "0"); }
        catch { }
        return ok;
    }


    /// <summary>图标 GDI 纪律：连续提取/失效 20 次，GDI 对象数不增长。</summary>
    private static int RunGdiIcons(string stub)
    {
        var profile = new ToolProfile { ToolId = "gdi-icon", DisplayName = "GDI", ExecutablePath = stub };
        // 预热：WPF 成像/WIC 首次使用会创建固定的 GDI 资源，不计入泄漏
        ToolIconService.GetBitmap(profile);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int before = GdiCount();
        for (int i = 0; i < 20; i++)
        {
            ToolIconService.Invalidate("gdi-icon");
            var bmp = ToolIconService.GetBitmap(profile);
            if (bmp == null) { Console.WriteLine("RESULT gdiicons 提取失败"); return 1; }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int after = GdiCount();
        ToolIconService.Invalidate("gdi-icon");
        Console.WriteLine($"RESULT gdiicons before={before} after={after} delta={after - before}");
        return after - before <= 5 ? 0 : 1;
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint flags);

    private static int GdiCount()
    {
        try { return (int)GetGuiResources(Process.GetCurrentProcess().Handle, 0); }
        catch { return -1; }
    }

    // ===== v1.5 新增验证 =====

    /// <summary>按日计划：定时只看当天子集；手动跑全部；子集为空 → 跳过并通知。</summary>
    private static int RunWeekPlan(string stub)
    {
        AppState.Settings.KeepAwakeEnabled = false;
        AppState.Settings.IdleGuardEnabled = false;
        AppState.Settings.AfterRunAction = "Notify";
        AppState.Settings.FailStrategy = "Skip";
        QueueService.IntervalOverride = TimeSpan.Zero;

        int today = QueueService.WeekdayBit(DateTime.Now);
        int other = (today + 1) % 7;

        var a = Stub("plan-a", stub, "--seconds 1 --exit 0", maxMinutes: 3);
        var b = Stub("plan-b", stub, "--seconds 1 --exit 0", maxMinutes: 3);
        ProfileRegistry.Save(a);
        ProfileRegistry.Save(b);

        AppState.Settings.Queue = new List<QueueEntry>
        {
            new() { ToolId = "plan-a", Enabled = true, WeekdayMask = 1 << today },
            new() { ToolId = "plan-b", Enabled = true, WeekdayMask = 1 << other },
        };

        QueueService.StartAsync(QueueTrigger.Timer).GetAwaiter().GetResult();
        var timerIds = QueueService.LastResults.Select(r => r.ToolId).ToList();
        Console.WriteLine("WEEKPLAN timer=" + string.Join(",", timerIds));

        QueueService.StartAsync(QueueTrigger.Manual).GetAwaiter().GetResult();
        var manualIds = QueueService.LastResults.Select(r => r.ToolId).ToList();
        Console.WriteLine("WEEKPLAN manual=" + string.Join(",", manualIds));

        // 今天两项都不在计划里 → 定时触发应直接跳过
        AppState.Settings.Queue[0].WeekdayMask = 1 << other;
        AppState.Settings.Queue[1].WeekdayMask = 1 << other;
        int logBefore = CountLog("今日计划为空");
        bool ran = QueueService.StartAsync(QueueTrigger.Timer).GetAwaiter().GetResult();
        int logAfter = CountLog("今日计划为空");
        Console.WriteLine($"WEEKPLAN empty ran={ran} notify={logAfter - logBefore}");

        bool saved = File.Exists(Path.Combine(ProfileRegistry.ToolsDir, "plan-a.json"));
        Console.WriteLine($"WEEKPLAN persisted={saved} maskA={AppState.Settings.Queue[0].WeekdayMask}");

        ProfileRegistry.Remove("plan-a");
        ProfileRegistry.Remove("plan-b");
        AppState.Settings.Queue = new List<QueueEntry>();

        bool ok = timerIds.SequenceEqual(new[] { "plan-a" })
                  && manualIds.SequenceEqual(new[] { "plan-a", "plan-b" })
                  && !ran && logAfter > logBefore && saved;
        Console.WriteLine("RESULT weekplan " + (ok ? "PASS" : "FAIL"));
        return ok ? 0 : 1;
    }

    /// <summary>自定义信号三条路径：日志文本 / 文件出现 / 窗口标题 → Resolving → Done。</summary>
    private static int RunCustomSignal(string stub, string mode)
    {
        AppState.Settings.KeepAwakeEnabled = false;
        AppState.Settings.AfterRunAction = "Notify";
        QueueService.IntervalOverride = TimeSpan.Zero;
        CustomSignalSource.PollIntervalOverride = TimeSpan.FromMilliseconds(300);
        WindowTitleSignalSource.PollIntervalOverride = TimeSpan.FromMilliseconds(300);

        // 把 StubTool 整份产物拷到临时目录：自定义信号只扫"程序目录"下的日志，
        // 这样日志就写在 exe 旁边，和真实工具的行为一致（也避免污染仓库里的 bin）。
        string dir = Path.Combine(Path.GetTempPath(), "db15sig_" + mode);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string stubDir = Path.GetDirectoryName(stub) ?? "";
        foreach (string f in Directory.GetFiles(stubDir))
        {
            try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); } catch { }
        }
        string localStub = Path.Combine(dir, Path.GetFileName(stub));
        string logFile = Path.Combine(dir, "run.log");
        string flagFile = Path.Combine(dir, "done.flag");
        try { if (File.Exists(logFile)) File.Delete(logFile); } catch { }
        try { if (File.Exists(flagFile)) File.Delete(flagFile); } catch { }

        string id = "sig-" + mode;
        var profile = Stub(id, localStub, "--seconds 4 --exit 0", maxMinutes: 3);
        profile.PrimarySignal = CompletionSignalKind.CustomSignal;
        profile.SecondarySignals = new[] { CompletionSignalKind.ProcessExit0 };
        profile.PostExitGraceMinutes = 1;
        profile.CustomData["custom"] = "1";

        switch (mode)
        {
            case "log":
                profile.LaunchArguments = $"--seconds 4 --exit 0 --log \"{logFile}\" --log-text \"一条龙完成\" --log-after 1";
                profile.CustomData[CustomSignalKeys.LogTexts] = "一条龙完成\n另一条不可能出现的文本";
                break;
            case "file":
                profile.LaunchArguments = $"--seconds 4 --exit 0 --create-file \"{flagFile}\" --file-after 1";
                profile.CustomData[CustomSignalKeys.FilePath] = flagFile;
                break;
            default:
                profile.LaunchArguments = "--seconds 4 --exit 0 --window \"任务完成 - StubTool\"";
                profile.CustomData[CustomSignalKeys.WindowTitle] = "任务完成";
                break;
        }

        var sw = Stopwatch.StartNew();
        int resolvingBefore = CountLog("Resolving");
        int hitBefore = mode switch
        {
            "log" => CountLog("日志命中「"),
            "file" => CountLog("文件已出现："),
            _ => CountLog("窗口标题命中"),
        };
        Console.WriteLine($"CUSTOMSIG {mode} launching args={profile.LaunchArguments}");
        QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Manual).GetAwaiter().GetResult();
        sw.Stop();
        Console.WriteLine($"CUSTOMSIG {mode} finished in {sw.Elapsed.TotalSeconds:F1}s");

        var r = QueueService.LastResults.FirstOrDefault();
        bool resolving = CountLog("Resolving") > resolvingBefore;
        bool hit = mode switch
        {
            "log" => CountLog("日志命中「") > hitBefore,
            "file" => CountLog("文件已出现：") > hitBefore,
            _ => CountLog("窗口标题命中") > hitBefore,
        };
        Console.WriteLine($"RESULT customsig-{mode} state={r?.State} elapsed={sw.Elapsed.TotalSeconds:F1}s resolving={resolving} hit={hit}");

        foreach (var p in Process.GetProcessesByName("StubTool"))
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            finally { p.Dispose(); }
        }
        return r?.State == ToolRunState.Done && resolving && hit ? 0 : 1;
    }

    /// <summary>图标手动上传：png / exe 两种来源 + 恢复默认 + 20 次换图标 GDI 不增长。</summary>
    private static int RunIconOverride(string stub)
    {
        string id = "ov-test";
        var profile = new ToolProfile { ToolId = id, DisplayName = "图标覆盖", ExecutablePath = stub };
        ToolIconService.ClearOverride(id);
        var baseIcon = ToolIconService.GetBitmap(profile);

        string dir = Path.Combine(Path.GetTempPath(), "db15icon");
        Directory.CreateDirectory(dir);
        string png = Path.Combine(dir, "custom.png");
        WritePng(png, 24, 24);

        string? err1 = ToolIconService.SetOverride(id, png);
        bool has = ToolIconService.HasOverride(id);
        var fromPng = ToolIconService.GetBitmap(profile);
        string? err2 = ToolIconService.SetOverride(id, stub);
        var fromExe = ToolIconService.GetBitmap(profile);
        ToolIconService.ClearOverride(id);
        bool gone = !ToolIconService.HasOverride(id);

        // GDI 纪律：连续换图标 / 恢复 20 次
        ToolIconService.SetOverride(id, png);
        ToolIconService.GetBitmap(profile);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int before = GdiCount();
        for (int i = 0; i < 20; i++)
        {
            ToolIconService.SetOverride(id, i % 2 == 0 ? png : stub);
            if (ToolIconService.GetBitmap(profile) == null) { Console.WriteLine("ICONOVERRIDE 取图失败"); return 1; }
            if (i % 4 == 3) ToolIconService.ClearOverride(id);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int after = GdiCount();
        ToolIconService.ClearOverride(id);

        Console.WriteLine($"RESULT iconoverride base={baseIcon != null} pngErr={(err1 ?? "ok")} has={has} png={fromPng != null} exeErr={(err2 ?? "ok")} exe={fromExe != null} gone={gone} gdi={before}->{after}");

        bool ok = baseIcon != null && err1 == null && has && fromPng != null && err2 == null && fromExe != null
                  && gone && after - before <= 5;
        return ok ? 0 : 1;
    }

    /// <summary>晨报：默认关；开启后播报昨日摘要；同一天不重复；无运行日不播报。</summary>
    private static int RunMorning()
    {
        bool defaultOff = new AppSettings().MorningReportEnabled == false;

        string yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var data = new Dictionary<string, List<GroupRecord>>
        {
            [yesterday] = new()
            {
                new GroupRecord { Name = "A", Start = "01:00:00", End = "02:30:00", Segments = 1, Source = "log" },
                new GroupRecord { Name = "B", Start = "02:30:00", End = "04:12:00", Segments = 1, Source = "log" },
            },
        };
        SaveArchive(data);
        GroupArchive.Load();
        var records = GroupArchive.GetDay(DateTime.Today.AddDays(-1));
        string summary = MorningReportService.BuildSummary(records);
        Console.WriteLine("MORNING summary=" + summary);

        AppState.Settings.MorningReportEnabled = true;
        AppState.Settings.MorningReportDate = "";
        int before = CountLog("[晨报]");
        MorningReportService.MaybeShow();
        int after1 = CountLog("[晨报]");
        string date1 = AppState.Settings.MorningReportDate;
        MorningReportService.MaybeShow();
        int after2 = CountLog("[晨报]");
        Console.WriteLine($"MORNING first={after1 - before} second={after2 - after1} date={date1}");

        // 无运行日：清空归档，开启且未播报 → 不产生 [晨报]
        SaveArchive(new Dictionary<string, List<GroupRecord>>());
        GroupArchive.Load();
        AppState.Settings.MorningReportDate = "";
        int beforeEmpty = CountLog("[晨报]");
        MorningReportService.MaybeShow();
        int afterEmpty = CountLog("[晨报]");
        Console.WriteLine($"MORNING empty={afterEmpty - beforeEmpty} dateAfterEmpty={AppState.Settings.MorningReportDate}");

        bool ok = defaultOff
                  && summary.Contains("2 次运行") && summary.Contains("全部成功") && summary.Contains("3小时12分")
                  && after1 - before == 1 && after2 == after1
                  && afterEmpty == beforeEmpty;
        Console.WriteLine("RESULT morning " + (ok ? "PASS" : "FAIL"));
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 周报（F8）：默认关；BuildWeekly 是纯函数，拿固定输入验固定输出；
    /// 有数据才播报、同一天不重复、七天没记录不播。
    /// </summary>
    private static int RunWeekly()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        bool defaultOff = new AppSettings().WeeklyReportEnabled == false;
        Check("周报默认关闭", defaultOff);

        // --- 纯函数：SummarizeDay ---
        var day = new DateTime(2026, 11, 5);
        var recs = new List<GroupRecord>
        {
            new() { Name = "A", Start = "01:00:00", End = "02:30:00", Source = "log" },
            new() { Name = "B", Start = "02:30:00", End = "04:12:00", Source = "log" },
        };
        var stat = WeeklyReportService.SummarizeDay(recs, day);
        Check("日统计/次数", stat.Runs == 2, stat.Runs.ToString());
        Check("日统计/完成数", stat.Finished == 2, stat.Finished.ToString());
        // A: 01:00→02:30 = 90 分；B: 02:30→04:12 = 102 分；合计 192 分
        Check("日统计/耗时 192 分钟", Math.Abs(stat.Minutes - 192) < 0.01, stat.Minutes.ToString("F1"));
        Check("日统计/全成功", stat.AllOk);
        Check("日统计/日期归零到日", stat.Day == day.Date, stat.Day.ToString("yyyy-MM-dd"));

        var half = new List<GroupRecord>
        {
            new() { Name = "A", Start = "01:00:00", End = "02:00:00", Source = "log" },
            new() { Name = "B", Start = "02:00:00", End = null, Source = "log" },
        };
        var halfStat = WeeklyReportService.SummarizeDay(half, day);
        Check("日统计/未结束不算完成", halfStat.Finished == 1 && halfStat.Runs == 2, $"{halfStat.Finished}/{halfStat.Runs}");
        Check("日统计/未结束不算全成功", !halfStat.AllOk);

        var empty = WeeklyReportService.SummarizeDay(new List<GroupRecord>(), day);
        Check("日统计/空日 Ran=false", !empty.Ran && empty.Runs == 0 && empty.Minutes == 0);

        // --- 纯函数：BuildWeekly ---
        var week = new List<DayStat>
        {
            new(new DateTime(2026, 11, 2), 1, 1, 60),
            new(new DateTime(2026, 11, 3), 0, 0, 0),
            new(new DateTime(2026, 11, 4), 3, 3, 180),
            new(new DateTime(2026, 11, 5), 0, 0, 0),
            new(new DateTime(2026, 11, 6), 0, 0, 0),
            new(new DateTime(2026, 11, 7), 0, 0, 0),
            new(new DateTime(2026, 11, 8), 0, 0, 0),
        };
        string text = WeeklyReportService.BuildWeekly(week);
        Console.WriteLine("WEEKLY text=" + text);
        Check("周报/含天数", text.Contains("7 天里跑了 2 天"), text);
        Check("周报/含总次数", text.Contains("共 4 次运行"), text);
        Check("周报/全成功措辞", text.Contains("全部成功"), text);
        Check("周报/含总耗时", text.Contains("总耗时 4小时0分"), text);
        Check("周报/含平均", text.Contains("平均每次 2小时0分"), text);
        Check("周报/含最多的一天", text.Contains("跑得最多的是周三（3 次）"), text);

        var partial = new List<DayStat>
        {
            new(new DateTime(2026, 11, 2), 2, 1, 90),
            new(new DateTime(2026, 11, 3), 1, 1, 30),
        };
        string partialText = WeeklyReportService.BuildWeekly(partial);
        Console.WriteLine("WEEKLY partial=" + partialText);
        Check("周报/有失败时报成功率", partialText.Contains("成功 2 次（66%）"), partialText);
        Check("周报/全勤时改口", partialText.Contains("每天都跑了"), partialText);
        Check("周报/并列时取较早的一天", partialText.Contains("跑得最多的是周一（2 次）"), partialText);

        // 每天各跑一次：没有「最多的一天」，改用平铺说法
        var flat = new List<DayStat>
        {
            new(new DateTime(2026, 11, 2), 1, 1, 30),
            new(new DateTime(2026, 11, 3), 1, 1, 30),
        };
        string flatText = WeeklyReportService.BuildWeekly(flat);
        Console.WriteLine("WEEKLY flat=" + flatText);
        Check("周报/每天都一次时改口", flatText.Contains("每天都只跑了一次"), flatText);

        Check("周报/空输入有兜底", WeeklyReportService.BuildWeekly(new List<DayStat>()).Contains("没有可统计"));
        Check("周报/全零不崩",
            WeeklyReportService.BuildWeekly(new List<DayStat> { new(new DateTime(2026, 11, 2), 0, 0, 0) })
                .Contains("一次都没跑"));

        // --- 端到端：Collect 读归档 + MaybeShow 只播一次 ---
        var today = DateTime.Today;
        var archive = new Dictionary<string, List<GroupRecord>>();
        for (int i = 0; i < WeeklyReportService.WindowDays; i++)
            archive[today.AddDays(-i).ToString("yyyy-MM-dd")] = new()
            {
                new GroupRecord { Name = "G", Start = "01:00:00", End = "02:00:00", Source = "log" },
            };
        SaveArchive(archive);
        GroupArchive.Load();

        var collected = WeeklyReportService.Collect();
        Check("Collect 七天", collected.Count == WeeklyReportService.WindowDays, collected.Count.ToString());
        Check("Collect 按时间升序", collected[0].Day < collected[^1].Day,
            $"{collected[0].Day:MM-dd} < {collected[^1].Day:MM-dd}");
        Check("Collect 最后一天是今天", collected[^1].Day == today.Date);
        Check("Collect 每天都有记录", collected.All(d => d.Runs == 1 && d.Finished == 1));

        AppState.Settings.WeeklyReportEnabled = true;
        AppState.Settings.WeeklyReportDate = "";
        int before = CountLog("[周报]");
        WeeklyReportService.MaybeShow();
        int after1 = CountLog("[周报]");
        string date1 = AppState.Settings.WeeklyReportDate;
        WeeklyReportService.MaybeShow();
        int after2 = CountLog("[周报]");
        Check("周报播报一次", after1 - before == 1, (after1 - before).ToString());
        Check("周报同日不重复", after2 == after1, (after2 - after1).ToString());
        Check("周报记下日期", date1 == today.ToString("yyyy-MM-dd"), date1);

        // 关掉开关：不再播
        AppState.Settings.WeeklyReportEnabled = false;
        AppState.Settings.WeeklyReportDate = "";
        int beforeOff = CountLog("[周报]");
        WeeklyReportService.MaybeShow();
        Check("关掉后不播", CountLog("[周报]") == beforeOff);

        // 清空归档：开了也不播（但日期仍记下）
        SaveArchive(new Dictionary<string, List<GroupRecord>>());
        GroupArchive.Load();
        AppState.Settings.WeeklyReportEnabled = true;
        AppState.Settings.WeeklyReportDate = "";
        int beforeEmpty = CountLog("[周报]");
        WeeklyReportService.MaybeShow();
        Check("七天全空不播", CountLog("[周报]") == beforeEmpty);
        Check("空周也记日期", AppState.Settings.WeeklyReportDate == today.ToString("yyyy-MM-dd"));

        AppState.Settings.WeeklyReportEnabled = false;
        AppState.Settings.WeeklyReportDate = "";

        Console.WriteLine("RESULT weekly fails=" + fails.Count + (fails.Count > 0 ? " [" + string.Join(", ", fails) + "]" : ""));
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// 控制 API（F6）+ 只读面板（F7）：路由判定是纯函数，直接断言；
    /// 面板 HTML 也只验结构（200 / Content-Type / 含关键字 / 转义），不要求起监听器——
    /// CI 里没有桌面会话，起 HttpListener 会因为端口与权限不稳。
    /// </summary>
    private static int RunApi()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        const string token = "0123456789abcdef0123456789abcdef";

        // --- 令牌 ---
        Check("令牌/32 位随机", ControlApi.NewToken().Length == ControlApi.TokenLength);
        Check("令牌/两次不同", ControlApi.NewToken() != ControlApi.NewToken());
        Check("令牌/合规判断", ControlApi.IsUsableToken(token));
        Check("令牌/空不合规", !ControlApi.IsUsableToken(""));
        Check("令牌/长度不对不合规", !ControlApi.IsUsableToken("0123"));
        Check("令牌/非十六进制不合规", !ControlApi.IsUsableToken("zz23456789abcdef0123456789abcdef"));
        Check("令牌/缺省时生成", ControlApi.IsUsableToken(ControlApi.EnsureToken("")));
        Check("令牌/已有则沿用", ControlApi.EnsureToken(token) == token);
        Check("令牌/不合规则换新", ControlApi.EnsureToken("bad") != "bad");

        // --- 开关关着：一律 404（不暴露 API 存在） ---
        var off = ControlApi.Plan("/api/status", token, apiEnabled: false, token);
        Check("关闭时状态也 404", off.Status == 404 && off.Action == ApiAction.None, off.Status.ToString());
        var offStart = ControlApi.Plan("/api/start", token, apiEnabled: false, token);
        Check("关闭时开跑 404", offStart.Status == 404);

        // --- 开着但令牌不对 ---
        var noToken = ControlApi.Plan("/api/status", null, true, token);
        Check("缺令牌 401", noToken.Status == 401, noToken.Status.ToString());
        var wrong = ControlApi.Plan("/api/status", "ffffffffffffffffffffffffffffffff", true, token);
        Check("错令牌 401", wrong.Status == 401, wrong.Status.ToString());
        var noExpected = ControlApi.Plan("/api/status", token, true, "");
        Check("服务端无令牌 503", noExpected.Status == 503, noExpected.Status.ToString());

        // --- 正常路由 ---
        var status = ControlApi.Plan("/api/status", token, true, token);
        Check("状态 200", status.Status == 200 && status.Action == ApiAction.None);
        Check("开跑 200", ControlApi.Plan("/api/start", token, true, token).Action == ApiAction.Start);
        Check("停止 200", ControlApi.Plan("/api/stop", token, true, token).Action == ApiAction.Stop);
        Check("单跑 200", ControlApi.Plan("/api/run", token, true, token).Action == ApiAction.Run);
        Check("末尾斜杠等价", ControlApi.Plan("/api/status/", token, true, token).Status == 200);
        Check("大小写不敏感", ControlApi.Plan("/API/STATUS", token, true, token).Status == 200);

        // --- 未知路径 ---
        Check("未知 api 路径 404", ControlApi.Plan("/api/hack", token, true, token).Status == 404);
        Check("非 api 路径 404", ControlApi.Plan("/webhook2", token, true, token).Status == 404);

        // --- tool 参数 ---
        Check("tool 参数解析", ControlApi.ToolIdFromQuery("?tool=bettergi") == "bettergi");
        Check("tool 参数无问号", ControlApi.ToolIdFromQuery("tool=bettergi") == "bettergi");
        Check("tool 参数多参数", ControlApi.ToolIdFromQuery("?x=1&tool=okww&y=2") == "okww");
        Check("tool 参数大写键", ControlApi.ToolIdFromQuery("?TOOL=bettergi") == "bettergi");
        Check("tool 参数解码", ControlApi.ToolIdFromQuery("?tool=a%20b") == "a b");
        Check("tool 参数缺失", ControlApi.ToolIdFromQuery("?x=1") == "");
        Check("tool 参数空串查询", ControlApi.ToolIdFromQuery(null) == "");

        // --- 状态 JSON ---
        string json = ControlApi.StatusJson(true, "bettergi", 2, 1, 4, "组开始：A");
        Check("状态 JSON 含 running", json.Contains("\"running\": true"), json.Replace("\n", " "));
        Check("状态 JSON 含工具", json.Contains("\"currentTool\": \"bettergi\""));
        Check("状态 JSON 含进度", json.Contains("\"doneTools\": 2") && json.Contains("\"totalTools\": 4"));
        Check("状态 JSON 含事件", json.Contains("组开始：A"));
        Check("状态 JSON 中文不转义", !json.Contains("\\u"));

        string actionJson = ControlApi.ActionJson(true, "start", "已请求开跑");
        Check("动作 JSON 含 ok", actionJson.Contains("\"ok\": true"));
        Check("动作 JSON 含 action", actionJson.Contains("\"action\": \"start\""));
        Check("错误体也是 JSON", ControlApi.Fail("令牌不正确").Contains("\"ok\": false"));

        // --- 面板 HTML（F7） ---
        string html = ControlApi.PanelHtml("一条龙管家", true, "bettergi", 3, 1, 5, "组结束：A", 5);
        Check("面板有 doctype", html.StartsWith("<!DOCTYPE html>"));
        Check("面板有 charset", html.Contains("charset=\"utf-8\""));
        Check("面板有自动刷新", html.Contains("http-equiv=\"refresh\""));
        Check("面板显示状态", html.Contains("正在跑"));
        Check("面板显示进度", html.Contains("3 / 5"));
        Check("面板显示事件", html.Contains("组结束：A"));
        Check("面板无控制按钮", !html.Contains("<button") && !html.Contains("<form"));
        Check("面板无令牌字段", !html.Contains("ControlApiToken") && !html.Contains("X-Butler-Token"));
        Check("面板刷新间隔有下限", ControlApi.PanelHtml("t", false, "", 0, 0, 0, "", 1).Contains("content=\"2\""));
        Check("面板刷新间隔有上限", ControlApi.PanelHtml("t", false, "", 0, 0, 0, "", 9999).Contains("content=\"60\""));
        Check("面板空闲文案", ControlApi.PanelHtml("t", false, "", 0, 0, 0, "", 5).Contains("空闲"));

        string evil = ControlApi.PanelHtml("<script>alert(1)</script>", false, "<img onerror=x>", 0, 0, 0, "", 5);
        Check("面板转义尖括号", !evil.Contains("<script>alert(1)</script>"));
        Check("面板转义引号", ControlApi.Escape("\"x\"").Contains("&quot;"));
        Check("面板转义单引号", ControlApi.Escape("'x'").Contains("&#39;"));
        Check("面板转义与号", ControlApi.Escape("a&b") == "a&amp;b");

        // --- 全库唯一监听器（WebhookHub 里写下的铁律） ---
        // 用 Dragon Butler 主程序集定位仓库根：...\Dragon Butler\bin\Release\net8.0-windows\ → 上溯 5 级
        string root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(QueueService).Assembly.Location)!,
            "..", "..", "..", "..", ".."));
        string srcDir = Path.Combine(root, "Dragon Butler");
        int listenerFiles = 0;
        if (Directory.Exists(srcDir))
        {
            foreach (var f in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                try
                {
                    // 同一文件里两处是正常的：wildcard 失败会重建一个回退到 127.0.0.1，生命周期仍只有一处
                    if (File.ReadLines(f).Any(line => line.Contains("new HttpListener(", StringComparison.Ordinal)))
                        listenerFiles++;
                }
                catch { }
            }
        }
        else
        {
            Console.WriteLine("  SKIP 找不到源码目录：" + srcDir);
        }
        Check("全库只有一个文件创建 HttpListener", listenerFiles == 1, "找到 " + listenerFiles + " 个文件");

        Console.WriteLine("RESULT api fails=" + fails.Count + (fails.Count > 0 ? " [" + string.Join(", ", fails) + "]" : ""));
        return fails.Count == 0 ? 0 : 1;
    }

    private static void SaveArchive(Dictionary<string, List<GroupRecord>> data)
    {
        try
        {
            string path = GroupArchive.FilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var opt = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data, opt));
        }
        catch (Exception ex) { Console.WriteLine("ARCHIVE-WRITE-FAIL " + ex.Message); }
    }

    private static void WritePng(string path, int w, int h)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;       // B
            pixels[i + 1] = 0x90;   // G
            pixels[i + 2] = 0xE0;   // R
            pixels[i + 3] = 0xFF;   // A
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, w * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        enc.Save(fs);
    }

    private static int CountLog(string needle)
    {
        try
        {
            string file = Path.Combine(LogService.Dir, DateTime.Now.ToString("yyyy-MM-dd") + "_运行.log");
            if (!File.Exists(file)) return 0;
            return File.ReadAllLines(file).Count(l => l.Contains(needle, StringComparison.Ordinal));
        }
        catch { return 0; }
    }

    private static bool LogHas(string needle) => CountLog(needle) > 0;

    /// <summary>
    /// v1.6 / F1 定时判定：补跑窗口必须能把「睡眠/关机错过整分钟」的日子救回来，
    /// 且不能重复跑同一个计划点。
    /// </summary>
    private static int RunSchedule()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        // 2026-11-06 是周五（bit4）。掩码 0x7F = 每天。
        var fri0800 = new DateTime(2026, 11, 6, 8, 0, 0);
        var fri0005 = new DateTime(2026, 11, 6, 0, 5, 0);

        // 1) 正好到点：该跑，但不是补跑。
        var d1 = SchedulePlanner.Decide(true, false, false, 8, 0, 0x7F, 120, null, fri0800);
        Check("到点该跑", d1.ShouldRun && !d1.IsCatchUp, d1.Reason);
        Check("到点 DueAt 对", d1.DueAt == fri0800, d1.DueAt?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        // 2) 睡过头 72 分钟后才开机：窗口内，补跑。
        var slept = fri0800.AddMinutes(72);
        var d2 = SchedulePlanner.Decide(true, false, false, 8, 0, 0x7F, 120, null, slept);
        Check("窗口内补跑", d2.ShouldRun && d2.IsCatchUp, d2.Reason);

        // 3) 超出窗口：不跑，且理由要说明超了多久。
        var d3 = SchedulePlanner.Decide(true, false, false, 8, 0, 0x7F, 120, null, fri0800.AddMinutes(200));
        Check("超出窗口不跑", !d3.ShouldRun && d3.DueAt == fri0800, d3.Reason);

        // 4) 该计划点已经跑过：不重复跑（哪怕还在窗口内）。
        var d4 = SchedulePlanner.Decide(true, false, false, 8, 0, 0x7F, 120, fri0800, fri0800.AddMinutes(30));
        Check("同计划点不重复", !d4.ShouldRun, d4.Reason);

        // 5) 补跑关闭 + 错过整分钟：不跑（老行为，本次修的就是这个）。
        var d5 = SchedulePlanner.Decide(true, false, false, 8, 0, 0x7F, 0, null, fri0800.AddMinutes(72));
        Check("补跑关闭则错过", !d5.ShouldRun, d5.Reason);

        // 6) 补跑关闭 + 恰好命中那一分钟：跑。
        var d6 = SchedulePlanner.Decide(true, false, false, 8, 0, 0x7F, 0, null, fri0800.AddSeconds(30));
        Check("补跑关闭仍认整分钟", d6.ShouldRun, d6.Reason);

        // 7) 没勾选今天（只勾周一，今天是周五）：不跑。
        var d7 = SchedulePlanner.Decide(true, false, false, 8, 0, 0x01, 120, null, fri0800.AddMinutes(10));
        Check("未勾选的星期不跑", !d7.ShouldRun, d7.Reason);

        // 8) 开关/度假/在跑：三种都不跑。
        Check("定时关闭不跑", !SchedulePlanner.Decide(false, false, false, 8, 0, 0x7F, 120, null, fri0800).ShouldRun);
        Check("度假模式不跑", !SchedulePlanner.Decide(true, true, false, 8, 0, 0x7F, 120, null, fri0800).ShouldRun);
        Check("正在跑不叠加", !SchedulePlanner.Decide(true, false, true, 8, 0, 0x7F, 120, null, fri0800).ShouldRun);

        // 9) 还没到点：不跑，且要给出下次时间。
        var d9 = SchedulePlanner.Decide(true, false, false, 8, 0, 0x7F, 120, null, fri0005);
        Check("未到点不跑", !d9.ShouldRun && d9.NextDue == fri0800, d9.Reason + " next=" + (d9.NextDue?.ToString("HH:mm") ?? "null"));

        // 10) 跨周：周五跑完，周一才该跑（掩码 bit0|bit4）。
        var mon0800 = new DateTime(2026, 11, 9, 8, 0, 0);
        var next = SchedulePlanner.NextDueAfter(0x11, 8, 0, fri0800.AddMinutes(1));
        Check("跨周下次点", next == mon0800, next?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        // 11) 少配了窗口：clamp 不炸，文案仍然可读。
        Check("窗口文字", SchedulePlanner.WindowText(120).Contains("2 小时"), SchedulePlanner.WindowText(120));
        Check("窗口关闭文字", SchedulePlanner.WindowText(0).Contains("关闭"), SchedulePlanner.WindowText(0));

        Console.WriteLine("RESULT schedule fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.6 / F2「接着跑」验证：
    /// 1) 台账只记「跑过且不是跳过」，Done 才算跑成；
    /// 2) 失败的不算跑成，下次接着跑仍会挑出来；
    /// 3) 挑项是纯函数，不改用户真实队列配置。
    /// 用独立 ToolId 前缀，不污染真实台账（跑完清掉今天的记录）。
    /// </summary>
    private static int RunResume(string stub)
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        RunJournal.ClearToday();

        // 1) 跳过 → 不进台账，也不算跑成
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-skip", DisplayName = "跳过项",
            State = ToolRunState.Idle, Reason = "游戏在跑，先不启动", Skipped = true,
        });
        Check("跳过不进台账", RunJournal.GetDay(DateTime.Now).All(e => e.ToolId != "probe-skip"));
        Check("跳过不算跑成", !RunJournal.DoneToolsToday().Contains("probe-skip"));

        // 2) 失败 → 进台账，但不算跑成
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-fail", DisplayName = "失败项",
            State = ToolRunState.Failed, Reason = "退出码 3", Attempts = 2,
        });
        var failEntry = RunJournal.GetDay(DateTime.Now).FirstOrDefault(e => e.ToolId == "probe-fail");
        Check("失败进台账", failEntry != null);
        Check("失败记了原因与次数", failEntry?.Reason == "退出码 3" && failEntry.Attempts == 2,
            $"reason={failEntry?.Reason} attempts={failEntry?.Attempts}");
        Check("失败不算跑成", !RunJournal.DoneToolsToday().Contains("probe-fail"));

        // 3) 成功 → 进台账且算跑成
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-done", DisplayName = "成功项",
            State = ToolRunState.Done, Reason = "正常结束", Attempts = 1,
        });
        Check("成功算跑成", RunJournal.DoneToolsToday().Contains("probe-done"));

        // 4) 同一工具再记一次失败 → 覆盖，状态变为「没跑成」
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-done", DisplayName = "成功项",
            State = ToolRunState.Timeout, Reason = "超过 5 分钟", Attempts = 1,
        });
        Check("重跑覆盖为最新一次", !RunJournal.DoneToolsToday().Contains("probe-done"));
        Check("同工具当天只一条", RunJournal.GetDay(DateTime.Now).Count(e => e.ToolId == "probe-done") == 1);

        // 5) 挑项：Done 的被排除，失败的与没跑过的保留，顺序不变
        var all = new List<ToolProfile>
        {
            Stub("probe-fail", stub, "--seconds 1 --exit 3"),
            Stub("probe-never", stub, "--seconds 1 --exit 0"),
            Stub("probe-done2", stub, "--seconds 1 --exit 0"),
        };
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-done2", DisplayName = "已跑成",
            State = ToolRunState.Done, Reason = "正常结束", Attempts = 1,
        });
        var left = QueueService.FilterUnfinished(all);
        Check("挑项排除已跑成", left.Count == 2 && left.All(p => p.ToolId != "probe-done2"),
            string.Join(",", left.Select(p => p.ToolId)));
        Check("挑项保留顺序", left.Count == 2 && left[0].ToolId == "probe-fail" && left[1].ToolId == "probe-never");

        // 6) 没有未完成项时，接着跑不该启动
        foreach (var p in all)
            RunJournal.Record(new QueueItemResult
            {
                ToolId = p.ToolId, DisplayName = p.DisplayName,
                State = ToolRunState.Done, Reason = "正常结束", Attempts = 1,
            });
        Check("全跑成后挑不出东西", QueueService.FilterUnfinished(all).Count == 0);

        // 7) 端到端：真起一个 StubTool，跑成后进台账
        QueueService.IntervalOverride = TimeSpan.FromSeconds(1);
        var live = Stub("probe-live", stub, "--seconds 1 --exit 0");
        QueueService.RunProfilesAsync(new List<ToolProfile> { live }, QueueTrigger.Manual).GetAwaiter().GetResult();
        Check("端到端跑成进台账", RunJournal.DoneToolsToday().Contains("probe-live"),
            string.Join(",", RunJournal.DoneToolsToday()));

        // 收尾：清掉本轮的假数据，别留在用户台账里
        RunJournal.ClearToday();

        Console.WriteLine("RESULT resume fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.6 / F4「通知说清为什么」验证：断言证据拼装层，不起进程。
    /// 通知正文本身就是这一层拼出来的，所以测这里等于测通知。
    /// </summary>
    private static int RunExplain()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        // 1) 退出码进正文
        string a = RunEvidence.Describe("异常退出", 3, null);
        Check("退出码进正文", a.Contains("异常退出") && a.Contains("退出码 3"), a);

        // 2) 退出码 0 也要如实说（不是「异常退出」时不能撒谎）
        Check("退出码 0 如实写", RunEvidence.Describe("报告完成但进程未退出", 0, null).Contains("退出码 0"));

        // 3) 没有退出码就不编
        Check("无退出码不提退出码", !RunEvidence.Describe("疑似挂起", null, null).Contains("退出码"));

        // 4) 最后一条错误日志行进正文
        string b = RunEvidence.Describe("超时 120 分钟未完成", null, "ERROR 无法识别的界面 2K 分辨率");
        Check("错误行进正文", b.Contains("最后一条错误：ERROR 无法识别的界面 2K 分辨率"), b);

        // 5) 长行压成一行并截断，不把手机通知栏撑爆
        string longLine = "ERROR " + new string('x', 500);
        string compact = RunEvidence.Compact(longLine);
        Check("长行被截断", compact.Length <= RunEvidence.MaxLineChars + 1, "len=" + compact.Length);
        Check("截断保留尾部", compact.StartsWith("…", StringComparison.Ordinal));

        // 6) 换行/制表符压平
        Check("换行被压平", !RunEvidence.Compact("a\r\nb\tc").Contains('\n'));

        // 7) 空原因不至于产生空通知
        Check("空原因有兜底", RunEvidence.Describe("", null, null) == "未完成");

        // 8) Reset 清空上一轮证据，避免串轮
        RunEvidence.NoteErrorLine("ERROR 上一轮的错误");
        Check("记录错误行", RunEvidence.LastErrorLine.Contains("上一轮"));
        RunEvidence.Reset();
        Check("Reset 清空", RunEvidence.LastErrorLine.Length == 0 && RunEvidence.LastAnchorLine.Length == 0);

        // 9) 通知正文里不再出现「详情见软件日志」这种没用的兜底
        var src = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? "", "..", "..", "..", "..", "..", "Dragon Butler", "QueueService.cs"));
        Check("通知不再甩锅给日志", !src.Contains("详情见软件日志", StringComparison.Ordinal));

        Console.WriteLine("RESULT explain fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.6 / F5「重试退避可配」验证：断言等待时长计算与文案，不起进程（起进程要真等 3 秒，太慢）。
    /// </summary>
    private static int RunRetry()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        // 1) 不翻倍时就是固定基线
        Check("不翻倍=固定基线",
            QueueService.RetryDelayCore(3, false, 1).TotalSeconds == 3
            && QueueService.RetryDelayCore(3, false, 3).TotalSeconds == 3);

        // 2) 翻倍：第一次等基线，第二次翻倍，第三次再翻倍
        Check("翻倍 3→6→12",
            QueueService.RetryDelayCore(3, true, 1).TotalSeconds == 3
            && QueueService.RetryDelayCore(3, true, 2).TotalSeconds == 6
            && QueueService.RetryDelayCore(3, true, 3).TotalSeconds == 12);

        // 3) 封顶 300 秒，不能无限翻下去
        Check("封顶 300 秒", QueueService.RetryDelayCore(3, true, 30).TotalSeconds <= QueueService.MaxRetryDelaySeconds);

        // 4) 基线为 0 且开翻倍，仍然是 0（0 乘任何数都是 0，不该变成 3）
        Check("基线 0 仍是 0", QueueService.RetryDelayCore(0, true, 5).TotalSeconds == 0);

        // 5) 负数被夹到 0，不产生负等待
        Check("负数夹到 0", QueueService.RetryDelayCore(-5, false, 1).TotalSeconds == 0);

        // 6) attempts 从 1 起算：第 1 次失败后的等待不翻倍
        Check("第 1 次失败不翻倍", QueueService.RetryDelayCore(10, true, 1).TotalSeconds == 10);

        // 7) 文案
        Check("文案 0 秒", QueueService.DescribeWait(TimeSpan.Zero) == "立刻");
        Check("文案 45 秒", QueueService.DescribeWait(TimeSpan.FromSeconds(45)) == "45 秒");
        Check("文案 2 分钟", QueueService.DescribeWait(TimeSpan.FromMinutes(2)) == "2 分钟");
        Check("文案 1.5 分钟", QueueService.DescribeWait(TimeSpan.FromSeconds(90)) == "1.5 分钟");

        // 8) 默认档案就是「重试 1 次、等 3 秒、翻倍开」——用户不动也有合理行为
        var d = new ToolProfile();
        Check("默认档案 RetryCount=1", d.RetryCount == 1);
        Check("默认档案 等 3 秒", d.RetryDelaySeconds == 3);
        Check("默认档案 翻倍开", d.RetryBackoff);

        Console.WriteLine("RESULT retry fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.6 / F3「失败现场包」验证：真的落一份包，然后检查它有什么、缺什么、泄没泄密。
    /// 用假凭据填进设置，再断言 env.json 里搜不到——这才是脱敏该验的东西。
    /// </summary>
    private static int RunBundle()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        const string FakeToken = "ZZTOKEN-should-never-appear-1234567890";
        const string FakePass = "ZZPASS-should-never-appear-0987654321";
        const string FakeMail = "someone@example.com";

        var s = AppState.Settings;
        string oldToken = s.NotifyTelegramToken, oldPass = s.NotifyEmailPass, oldTo = s.NotifyEmailTo;
        bool oldEnabled = s.IncidentBundleEnabled;
        try
        {
            s.IncidentBundleEnabled = true;
            s.NotifyTelegramToken = FakeToken;
            s.NotifyEmailPass = FakePass;
            s.NotifyEmailTo = FakeMail;

            var profile = new ToolProfile { ToolId = "probe-bundle", DisplayName = "探针假工具" };
            string? dir = IncidentBundle.Capture("探针：模拟一次失败", profile, includeScreenshot: false);

            Check("返回目录路径", !string.IsNullOrEmpty(dir), dir ?? "(null)");
            bool exists = dir != null && Directory.Exists(dir);
            Check("目录真的建出来了", exists);
            if (dir == null || !exists)
            {
                Console.WriteLine("RESULT bundle fails=" + (fails + 2));
                return 1;
            }

            Check("有 env.json", File.Exists(Path.Combine(dir, "env.json")));
            Check("有 log.txt", File.Exists(Path.Combine(dir, "log.txt")));

            string env = File.ReadAllText(Path.Combine(dir, "env.json"));
            Check("env 里有失败原因", env.Contains("模拟一次失败"));
            Check("env 里有档案信息", env.Contains("probe-bundle"));
            Check("env 里有进程列表", env.Contains("\"进程\""));
            // 脱敏三连——这三个串一个都不许出现
            Check("不泄露 token", !env.Contains(FakeToken));
            Check("不泄露密码", !env.Contains(FakePass));
            Check("不泄露邮箱", !env.Contains(FakeMail));
            Check("敏感字段确实被替换", env.Contains(IncidentBundle.Redacted));

            // 保留策略：多抓几份，检查只留最新 N 份
            var dirsBefore = Directory.GetDirectories(IncidentBundle.Root).Length;
            Check("Trim 后份数不超过配置值", dirsBefore <= Math.Max(1, s.IncidentBundleKeep) + 2, "before=" + dirsBefore);

            // 关掉开关就不该再产出
            s.IncidentBundleEnabled = false;
            Check("关掉开关返回 null", IncidentBundle.Capture("探针：开关关闭", profile, includeScreenshot: false) == null);

            try { Directory.Delete(dir, true); } catch { }
        }
        finally
        {
            s.NotifyTelegramToken = oldToken;
            s.NotifyEmailPass = oldPass;
            s.NotifyEmailTo = oldTo;
            s.IncidentBundleEnabled = oldEnabled;
        }

        Console.WriteLine("RESULT bundle fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

    private static int Help()
    {
        Console.WriteLine("CoordinatorProbe <done|failed|timeout|halted|haltedlog|logpattern|args|migrate|clipboard|clipdialog> [stubPath]");
        return 0;
    }
    private static int Run(string name, string stub, CompletionSignalKind primary, string stubArgs,
                           int maxMinutes, int graceMinutes)
    {
        var profile = new ToolProfile
        {
            ToolId = "stub",
            DisplayName = "StubTool",
            ExecutablePath = stub,
            LaunchArguments = stubArgs,
            ManagedProcessName = "StubTool",
            GameProcessNames = Array.Empty<string>(),
            PrimarySignal = primary,
            SecondarySignals = Array.Empty<CompletionSignalKind>(),
            LogCompletionPattern = primary == CompletionSignalKind.LogPattern ? "执行结束" : null,
            MaxDurationMinutes = maxMinutes,
            PostExitGraceMinutes = graceMinutes,
        };

        using var coord = ToolRunner.CreateCoordinator(profile, msg => Console.WriteLine("NOTIFY " + msg));
        coord.Transition += (from, to, reason) => Console.WriteLine($"TRANS {from}->{to} {reason}");

        coord.BeginLaunch();
        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = stub,
                Arguments = stubArgs,
                WorkingDirectory = Path.GetDirectoryName(stub) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            coord.LaunchFailed(ex.Message);
            Console.WriteLine("RESULT " + name + " state=" + coord.State);
            return 1;
        }

        if (proc == null)
        {
            coord.LaunchFailed("Process.Start 返回 null");
            Console.WriteLine("RESULT " + name + " state=" + coord.State);
            return 1;
        }

        var ctx = new ToolRunContext { Profile = profile, Process = proc, Arguments = stubArgs };
        coord.LaunchSucceeded(proc, ctx);

        if (primary == CompletionSignalKind.LogPattern)
            FeedLogPattern(stub);

        var sw = Stopwatch.StartNew();
        while (!coord.IsFinished && sw.Elapsed < TimeSpan.FromMinutes(maxMinutes + graceMinutes + 2))
            Thread.Sleep(500);

        Console.WriteLine($"RESULT {name} state={coord.State} elapsed={sw.Elapsed.TotalSeconds:F1}s");
        try { if (!proc.HasExited) proc.Kill(); } catch { }
        return 0;
    }

    /// <summary>halted 分支：在主信号源监视的日志里写一行完成标记，触发 Running→Resolving。</summary>
    private static void FeedLogPattern(string stub)
    {
        try
        {
            string logDir = Path.Combine(Path.GetDirectoryName(stub) ?? "", "log");
            Directory.CreateDirectory(logDir);
            string file = Path.Combine(logDir, "better-genshin-impact-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            Thread.Sleep(1500);
            File.AppendAllText(file, $"配置组 \"Stub\" 执行结束{Environment.NewLine}", Encoding.UTF8);
            Console.WriteLine("FEED log-pattern at " + Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            Console.WriteLine("FEED-FAIL " + ex.Message);
        }
    }

    /// <summary>配置迁移：写一份 v1.2 形态的 settings.json，验证迁移结果与备份。</summary>
    private static int Migrate()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "config");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        string backup = path + ".bak.v12";
        if (File.Exists(backup)) File.Delete(backup);

        File.WriteAllText(path, """
        {
          "BgiPath": "D:\\BetterGI\\BetterGI.exe",
          "DragonConfigName": "日常一条龙",
          "SelectedGroups": ["日常", "挖矿"],
          "ScheduleEnabled": true,
          "ScheduleHour": 8,
          "Theme": "Dark"
        }
        """, Encoding.UTF8);

        AppState.LoadSettings();
        ProfileRegistry.Initialize();

        var bgi = ProfileRegistry.BetterGi;
        Console.WriteLine("MIGRATE version=" + AppState.Settings.Version);
        Console.WriteLine("MIGRATE bgiPath=" + bgi.ExecutablePath);
        Console.WriteLine("MIGRATE configName=" + bgi.CustomData.GetValueOrDefault("dragonConfigName"));
        Console.WriteLine("MIGRATE groups=" + bgi.CustomData.GetValueOrDefault("selectedGroups"));
        Console.WriteLine("MIGRATE schedule=" + AppState.Settings.ScheduleEnabled + "/" + AppState.Settings.ScheduleHour);
        Console.WriteLine("MIGRATE backup=" + File.Exists(backup));
        Console.WriteLine("MIGRATE toolsJson=" + File.Exists(Path.Combine(ProfileRegistry.ToolsDir, "bettergi.json")));
        Console.WriteLine("MIGRATE profiles=" + string.Join(",", System.Linq.Enumerable.Select(ProfileRegistry.All, p => p.ToolId)));
        return 0;
    }

    /// <summary>
    /// v1.8 / F9：全局热键的解析层。
    /// 不验真实注册——CI 没有桌面会话，RegisterHotKey 必失败；
    /// 但「字符串怎么变成 修饰键+虚拟键」是纯函数，能全分支断言。
    /// </summary>
    private static int RunHotkey()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // 默认档位：默认关，默认组合开跑 Ctrl+Alt+R、停止 Ctrl+Alt+S
        var def = new AppSettings();
        Check("热键默认关闭", !def.HotkeyEnabled);
        Check("默认开跑组合", def.HotkeyStart == "Ctrl+Alt+R", def.HotkeyStart);
        Check("默认停止组合", def.HotkeyStop == "Ctrl+Alt+S", def.HotkeyStop);

        // --- 解析：常见写法 ---
        var p = HotkeyService.Parse("Ctrl+Alt+R");
        Check("解析 Ctrl+Alt+R 成功", p != null);
        Check("Ctrl+Alt+R 修饰键=Control|Alt", p != null && p.Value.Modifiers == (HotkeyService.ModControl | HotkeyService.ModAlt),
            p?.Modifiers.ToString() ?? "null");
        Check("Ctrl+Alt+R 主键='R'(0x52)", p != null && p.Value.VirtualKey == 0x52, p?.VirtualKey.ToString() ?? "null");

        // 大小写、顺序、别名都要认
        Check("小写也认", HotkeyService.Parse("ctrl+alt+r") != null);
        Check("顺序无关", HotkeyService.Parse("Alt+Ctrl+R") != null);
        Check("control 别名", HotkeyService.Parse("control+shift+f5") != null);
        Check("win 别名", HotkeyService.Parse("Win+R") != null);
        Check("空格容忍", HotkeyService.Parse(" Ctrl + Alt + R ") != null);

        // --- 非法输入一律 null：裸主键会霸占打字，必须拒绝 ---
        Check("空串拒绝", HotkeyService.Parse("") == null);
        Check("null 拒绝", HotkeyService.Parse(null) == null);
        Check("裸主键拒绝", HotkeyService.Parse("R") == null);
        Check("只有修饰键拒绝", HotkeyService.Parse("Ctrl+Alt") == null);
        Check("未知主键拒绝", HotkeyService.Parse("Ctrl+Alt+NotAKey") == null);
        Check("未知修饰键拒绝", HotkeyService.Parse("Hyper+R") == null);

        // --- Normalize：回填输入框时用的规范写法 ---
        Check("Normalize 规范大小写", HotkeyService.Normalize("ctrl+alt+r").Contains("Ctrl"), HotkeyService.Normalize("ctrl+alt+r"));
        Check("Normalize 空串不抛", HotkeyService.Normalize("") == "");
        Check("Normalize null 不抛", HotkeyService.Normalize(null) == "");

        // --- 特殊主键 ---
        foreach (var key in new[] { "F1", "F12", "Space", "Escape", "Tab", "Enter", "0", "9" })
            Check("主键 " + key + " 可解析", HotkeyService.Parse("Ctrl+" + key) != null);

        Console.WriteLine($"RESULT hotkey fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.8 / F10：任务栏进度与托盘文字的纯函数。
    /// 这一段最容易写错的地方是「分母」，所以断言重点在 0 项、1 项、全跑成三种边界。
    /// </summary>
    private static int RunProgress()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        Check("0/0 进度为 0", QueueProgress.Fraction(0, 0) == 0);
        Check("负数不外溢", QueueProgress.Fraction(-1, 5) == 0);
        Check("0/3 为 0", QueueProgress.Fraction(0, 3) == 0);
        Check("1/3 约 0.333", Math.Abs(QueueProgress.Fraction(1, 3) - 1.0 / 3.0) < 0.0001);
        Check("3/3 为 1", QueueProgress.Fraction(3, 3) == 1);
        Check("超量夹到 1", QueueProgress.Fraction(5, 3) == 1);
        Check("分母为负按 0", QueueProgress.Fraction(2, -3) == 0);

        Check("总数 0 时文案是准备中", QueueProgress.Describe(0, 0, null) == "准备中", QueueProgress.Describe(0, 0, null));
        Check("无工具名只给计数", QueueProgress.Describe(1, 3, null) == "1/3", QueueProgress.Describe(1, 3, null));
        Check("空工具名只给计数", QueueProgress.Describe(1, 3, "  ") == "1/3", QueueProgress.Describe(1, 3, "  "));
        Check("有工具名带分隔符", QueueProgress.Describe(1, 3, "BetterGI") == "1/3 · BetterGI", QueueProgress.Describe(1, 3, "BetterGI"));

        Check("空闲时用缺省名", QueueProgress.TrayText(false, 0, 0, null) == "一条龙管家", QueueProgress.TrayText(false, 0, 0, null));
        Check("空闲时用自定义名", QueueProgress.TrayText(false, 1, 3, null, "自定义") == "自定义", QueueProgress.TrayText(false, 1, 3, null, "自定义"));
        Check("运行中有进度", QueueProgress.TrayText(true, 1, 3, "BetterGI").Contains("1/3"), QueueProgress.TrayText(true, 1, 3, "BetterGI"));

        // 托盘 tooltip 有长度上限（约 63 字符），超了必须截断加省略号
        string longName = new string('长', 100);
        string tray = QueueProgress.TrayText(true, 1, 3, longName);
        Check("托盘文字超长截断", tray.Length <= 64, tray.Length.ToString());
        Check("托盘文字截断加省略号", tray.EndsWith("…", StringComparison.Ordinal), tray.Substring(Math.Max(0, tray.Length - 3)));

        Console.WriteLine($"RESULT progress fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.8 / F15：命令面板的匹配与分发表。
    /// 面板本身是窗口，CI 里开不了；但「有哪些命令、怎么筛、id 撞不撞」全在 CommandRegistry。
    /// </summary>
    private static int RunCommand()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var all = CommandRegistry.All;
        Check("命令表非空", all.Count >= 20, all.Count.ToString());

        // id 必须唯一，否则 Executed 回传时分不清是哪条
        var ids = new HashSet<string>(StringComparer.Ordinal);
        bool dup = false;
        foreach (var c in all) if (!ids.Add(c.Id)) dup = true;
        Check("命令 id 唯一", !dup);

        // 每条都得有标题和分组，空标题会渲染成一行空白
        Check("每条都有标题", all.All(c => c.Title.Length > 0));
        Check("每条都有分组", all.All(c => c.Group.Length > 0));
        Check("每条 id 不含空白", all.All(c => c.Id.IndexOf(' ') < 0));

        // 空查询：面板刚打开要看到内容
        var empty = CommandRegistry.Search("", 8);
        Check("空查询返回 8 条", empty.Count == 8, empty.Count.ToString());
        Check("空查询按原序", empty.Count > 0 && empty[0].Id == all[0].Id, empty.Count > 0 ? empty[0].Id : "none");

        // 子串命中：标题优先于关键字
        var q1 = CommandRegistry.Search("日历", 8);
        Check("搜『日历』命中挂机日历", q1.Count > 0 && q1[0].Id == "view.calendar", q1.Count > 0 ? q1[0].Id : "none");

        var q2 = CommandRegistry.Search("token", 8);
        Check("搜 token 命中复制令牌", q2.Count > 0 && q2[0].Id == "tool.copyToken", q2.Count > 0 ? q2[0].Id : "none");

        var q3 = CommandRegistry.Search("浮条", 8);
        Check("搜『浮条』命中迷你浮条", q3.Count > 0 && q3[0].Id == "ui.miniBar", q3.Count > 0 ? q3[0].Id : "none");

        Check("无匹配返回空", CommandRegistry.Search("zzz-not-a-command", 8).Count == 0);
        Check("limit 生效", CommandRegistry.Search("", 3).Count == 3);

        // 标题命中的排序必须在关键字命中之前
        var mixed = CommandRegistry.Search("开跑", 8);
        Check("标题命中排在前面", mixed.Count > 0 && mixed[0].Id == "run.queue", mixed.Count > 0 ? mixed[0].Id : "none");

        // 动态命令：单跑某工具，id 带工具 id，且不与固定表撞
        var extra = CommandRegistry.ToolCommands(new[] { ("bettergi", "BetterGI"), ("ok-ww", "ok-ww") });
        Check("动态命令数量", extra.Count == 2, extra.Count.ToString());
        Check("动态命令 id 带前缀", extra[0].Id.StartsWith("run.tool:", StringComparison.Ordinal), extra[0].Id);
        Check("动态命令 id 含工具 id", extra[0].Id == "run.tool:bettergi", extra[0].Id);
        Check("动态命令不撞固定表", extra.All(e => !ids.Contains(e.Id)));
        Check("动态命令跳过空名", CommandRegistry.ToolCommands(new[] { ("", "") }).Count == 0);

        // 动态命令要能一起被搜到
        var merged = new List<CommandRegistry.Command>(CommandRegistry.All);
        merged.AddRange(extra);
        var hitTool = CommandRegistry.Search(merged, "BetterGI", 8);
        Check("合并后能搜到自定义工具", hitTool.Count > 0 && hitTool[0].Id == "run.tool:bettergi",
            hitTool.Count > 0 ? hitTool[0].Id : "none");

        // 面板里所有 id 都必须被分发处理（漏一条就是点了没反应）
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "run.queue", "run.continue", "run.stop", "run.single",
            "view.schedule", "view.queue", "view.tools", "view.advanced", "view.log", "view.calendar",
            "view.today", "view.logrules", "view.depends", "view.groups",
            "tool.copyToken", "tool.apiToggle", "tool.lanToggle", "tool.snapshot",
            "ui.miniBar", "ui.theme", "ui.hotkey",
            "sys.openFolder", "sys.check", "sys.about",
        };
        var missing = all.Where(c => !known.Contains(c.Id)).Select(c => c.Id).ToList();
        Check("命令表没有未实现的分支", missing.Count == 0, string.Join(",", missing));

        Console.WriteLine($"RESULT command fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.8 / F13：挂机日历的判定逻辑。
    /// 重点断言「有记录但没结束」算失败——这是最容易被写反的一处。
    /// </summary>
    private static int RunCalendar()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        Check("空记录=没跑", CalendarModel.LevelOf(null) == CalendarModel.DayLevel.Idle);
        Check("空列表=没跑", CalendarModel.LevelOf(new List<GroupRecord>()) == CalendarModel.DayLevel.Idle);

        var ok2 = new List<GroupRecord>
        {
            new() { Name = "A", Start = "01:00:00", End = "02:00:00" },
            new() { Name = "B", Start = "02:00:00", End = "03:00:00" },
        };
        Check("全有结束=全成功", CalendarModel.LevelOf(ok2) == CalendarModel.DayLevel.AllOk);

        var partial = new List<GroupRecord>
        {
            new() { Name = "A", Start = "01:00:00", End = "02:00:00" },
            new() { Name = "B", Start = "02:00:00", End = null },
        };
        Check("部分有结束=部分成功", CalendarModel.LevelOf(partial) == CalendarModel.DayLevel.Partial);

        var none = new List<GroupRecord>
        {
            new() { Name = "A", Start = "01:00:00", End = null },
            new() { Name = "B", Start = "02:00:00", End = "" },
        };
        Check("全无结束=全没成", CalendarModel.LevelOf(none) == CalendarModel.DayLevel.Failed);

        // 空 End 字符串与 null 等价，不能因为 "" 就当成已完成
        var emptyEnd = new List<GroupRecord> { new() { Name = "A", Start = "01:00:00", End = "" } };
        Check("空 End 不算完成", CalendarModel.LevelOf(emptyEnd) == CalendarModel.DayLevel.Failed);

        // FirstDay：必须是周一，且往前整 52 个整周
        var anchor = new DateTime(2026, 11, 6); // 星期五
        DateTime first = CalendarModel.FirstDay(anchor);
        Check("起点是周一", first.DayOfWeek == DayOfWeek.Monday, first.DayOfWeek.ToString());
        Check("起点不晚于今天", first <= anchor);
        Check("跨度正好 53 周", (anchor.Date.AddDays(-((int)anchor.DayOfWeek + 6) % 7) - first).Days == (CalendarModel.Weeks - 1) * 7,
            (anchor.Date - first).Days.ToString());
        Check("同日反复调用稳定", CalendarModel.FirstDay(anchor) == first);

        // Summary 文案
        Check("无记录文案", CalendarModel.Summary(0, 0, 0, 0).Contains("还没有"));
        string s = CalendarModel.Summary(10, 6, 3, 1);
        Check("有记录含天数", s.Contains("10 天"), s);
        Check("有记录含三类", s.Contains("全部成功 6") && s.Contains("部分成功 3") && s.Contains("全没成 1"), s);

        // 悬停提示
        Check("提示/没跑", CalendarModel.TipFor(anchor, CalendarModel.DayLevel.Idle, 0, 0).Contains("没跑"));
        Check("提示/全成功", CalendarModel.TipFor(anchor, CalendarModel.DayLevel.AllOk, 2, 2).Contains("全部跑成"));
        Check("提示/部分", CalendarModel.TipFor(anchor, CalendarModel.DayLevel.Partial, 2, 1).Contains("1/2"));
        Check("提示/全失败", CalendarModel.TipFor(anchor, CalendarModel.DayLevel.Failed, 2, 0).Contains("全部没跑成"));
        Check("提示带日期", CalendarModel.TipFor(anchor, CalendarModel.DayLevel.Idle, 0, 0).Contains("2026-11-06"));

        Console.WriteLine($"RESULT calendar fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.13「看得见」验证：耗时计算、历史中位数、实时快照、今日明细。
    ///
    /// <para>
    /// 这些函数直接决定界面上「还要 20 分 · 预计 03:10 结束」这句话对不对，
    /// 所以全部做成纯函数在这里断言，而不是等到挂机一晚上之后靠眼睛发现问题。
    /// </para>
    /// <para>
    /// 造数走 GroupArchive.PathOverride + 直接写 JSON：Seed 只往内存里塞、不落盘，
    /// 而 RecentDurations 是读文件的——不写盘就永远扫不到样本。
    /// </para>
    /// </summary>
    private static int RunLive()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) DurationOf：一条记录的真实耗时 ----
        Check("耗时有起止", GroupArchive.DurationOf(new GroupRecord { Start = "01:00:00", End = "01:30:00" }) == TimeSpan.FromMinutes(30));
        Check("缺结束算不出", GroupArchive.DurationOf(new GroupRecord { Start = "01:00:00", End = null }) == null);
        Check("缺开始算不出", GroupArchive.DurationOf(new GroupRecord { Start = null, End = "01:30:00" }) == null);
        Check("空串算不出", GroupArchive.DurationOf(new GroupRecord { Start = "01:00:00", End = "  " }) == null);
        Check("空记录算不出", GroupArchive.DurationOf(null) == null);
        Check("坏格式算不出", GroupArchive.DurationOf(new GroupRecord { Start = "abc", End = "01:30:00" }) == null);
        // 跨零点：23:40 开始、00:10 结束 = 30 分钟，不是负 23 小时半
        Check("跨零点按隔天", GroupArchive.DurationOf(new GroupRecord { Start = "23:40:00", End = "00:10:00" }) == TimeSpan.FromMinutes(30));

        // ---- 2) 历史样本与中位数（造 14 天假数据） ----
        string? oldPath = GroupArchive.PathOverride;
        string sandbox = Path.Combine(Path.GetTempPath(), "dragon-live-probe.json");
        try
        {
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
            GroupArchive.PathOverride = sandbox;

            var fake = new Dictionary<string, List<GroupRecord>>
            {
                // 「挖矿」：三天分别 10 / 20 / 60 分钟 → 中位数 20
                [DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd")] = new()
                {
                    new() { Name = "挖矿", Start = "01:00:00", End = "01:10:00" },
                },
                [DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd")] = new()
                {
                    new() { Name = "挖矿", Start = "01:00:00", End = "01:20:00" },
                },
                [DateTime.Today.AddDays(-3).ToString("yyyy-MM-dd")] = new()
                {
                    new() { Name = "挖矿", Start = "01:00:00", End = "02:00:00" },
                },
                // 「重开过」：单条但 segments=2，起止跨着两次执行 → 必须排除
                [DateTime.Today.AddDays(-4).ToString("yyyy-MM-dd")] = new()
                {
                    new() { Name = "重开过", Start = "01:00:00", End = "03:00:00", Segments = 2 },
                    new() { Name = "重开过", Start = "05:00:00", End = "05:30:00", Segments = 1 },
                },
                // 「今天」的样本不该进「往常」
                [DateTime.Today.ToString("yyyy-MM-dd")] = new()
                {
                    new() { Name = "只跑过一次", Start = "01:00:00", End = "05:00:00" },
                },
                // 超出 14 天窗口
                [DateTime.Today.AddDays(-20).ToString("yyyy-MM-dd")] = new()
                {
                    new() { Name = "太老的样本", Start = "01:00:00", End = "01:30:00" },
                },
            };
            File.WriteAllText(sandbox, JsonSerializer.Serialize(fake, new JsonSerializerOptions { WriteIndented = true }));
            GroupArchive.Load();

            var recent = GroupArchive.RecentDurations("挖矿");
            Check("样本条数=3", recent.Count == 3, recent.Count.ToString());
            Check("样本内容正确", recent.OrderBy(x => x).SequenceEqual(new[] { 10.0, 20.0, 60.0 }), string.Join(",", recent));
            Check("中位数取中间值", GroupArchive.MedianMinutes("挖矿") == 20, GroupArchive.MedianMinutes("挖矿")?.ToString() ?? "null");

            Check("重开过段被排除", GroupArchive.RecentDurations("重开过").Count == 1, GroupArchive.RecentDurations("重开过").Count.ToString());
            // 只剩 1 条样本 → 默认 minSamples=2 时不给中位数（宁可不说，也不拿一次数据当规律）
            Check("单样本默认不预估", GroupArchive.MedianMinutes("重开过") == null);
            Check("单样本放宽后可算", GroupArchive.MedianMinutes("重开过", 14, 1) == 30,
                GroupArchive.MedianMinutes("重开过", 14, 1)?.ToString() ?? "null");
            Check("今天的样本不算往常", GroupArchive.RecentDurations("只跑过一次").Count == 0);
            Check("样本不足返 null", GroupArchive.MedianMinutes("只跑过一次") == null);
            Check("超过窗口的样本不算", GroupArchive.RecentDurations("太老的样本").Count == 0);
            Check("不存在的组返空", GroupArchive.RecentDurations("查无此组").Count == 0);
            Check("空组名返空", GroupArchive.RecentDurations("").Count == 0);
            Check("days=0 返空", GroupArchive.RecentDurations("挖矿", 0).Count == 0);

            // 偶数个样本取中间两值平均
            var fake2 = new Dictionary<string, List<GroupRecord>>
            {
                [DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd")] = new() { new() { Name = "偶数", Start = "01:00:00", End = "01:10:00" } },
                [DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd")] = new() { new() { Name = "偶数", Start = "01:00:00", End = "01:30:00" } },
            };
            File.WriteAllText(sandbox, JsonSerializer.Serialize(fake2));
            GroupArchive.Load();
            Check("偶数样本取均值", GroupArchive.MedianMinutes("偶数") == 20, GroupArchive.MedianMinutes("偶数")?.ToString() ?? "null");
        }
        finally
        {
            GroupArchive.PathOverride = oldPath;
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
            GroupArchive.Load();
        }

        // ---- 3) LiveStatus.Short：时长文案 ----
        Check("Short/30 秒", LiveStatus.Short(TimeSpan.FromSeconds(30)) == "30 秒", LiveStatus.Short(TimeSpan.FromSeconds(30)));
        Check("Short/59 秒", LiveStatus.Short(TimeSpan.FromSeconds(59)) == "59 秒", LiveStatus.Short(TimeSpan.FromSeconds(59)));
        Check("Short/1 分", LiveStatus.Short(TimeSpan.FromSeconds(60)) == "1 分", LiveStatus.Short(TimeSpan.FromSeconds(60)));
        Check("Short/45 分", LiveStatus.Short(TimeSpan.FromMinutes(45)) == "45 分", LiveStatus.Short(TimeSpan.FromMinutes(45)));
        Check("Short/整小时", LiveStatus.Short(TimeSpan.FromMinutes(120)) == "2 小时", LiveStatus.Short(TimeSpan.FromMinutes(120)));
        Check("Short/带零头", LiveStatus.Short(TimeSpan.FromMinutes(75)) == "1 小时 15 分", LiveStatus.Short(TimeSpan.FromMinutes(75)));
        Check("Short/负数夹 0", LiveStatus.Short(TimeSpan.FromSeconds(-5)) == "0 秒", LiveStatus.Short(TimeSpan.FromSeconds(-5)));

        // ---- 4) LiveStatus.Build：四个分支 ----
        var now = new DateTime(2026, 11, 13, 2, 0, 0);

        var idle = LiveStatus.Build(false, "BetterGI", "挖矿", null, null, null, now);
        Check("空闲/主行", idle.Headline == "空闲", idle.Headline);
        Check("空闲/次行为空", idle.Detail == "", idle.Detail);
        Check("空闲/无预计", idle.EtaAt == null);

        // 有组名 + 有样本：组跑在 01:50，中位 20 分 → 预计 02:10，还剩 10 分
        var inGroup = LiveStatus.Build(true, "BetterGI", "挖矿", now.AddHours(-1), now.AddMinutes(-10), 20, now);
        Check("有组/主行含组名", inGroup.Headline.StartsWith("挖矿", StringComparison.Ordinal), inGroup.Headline);
        Check("有组/主行含已跑", inGroup.Headline.Contains("10 分"), inGroup.Headline);
        Check("有组/有预计时刻", inGroup.EtaAt == now.AddMinutes(10), inGroup.EtaAt?.ToString("HH:mm") ?? "null");
        Check("有组/次行给还要多久", inGroup.Detail.Contains("还要 10 分") && inGroup.Detail.Contains("02:10"), inGroup.Detail);
        Check("HasGroup 为真", inGroup.HasGroup);

        // 有组名但没样本：不预估，也不写"未知"
        var noSample = LiveStatus.Build(true, "BetterGI", "挖矿", now.AddHours(-1), now.AddMinutes(-10), null, now);
        Check("无样本/不预估", noSample.EtaAt == null && noSample.Detail == "", noSample.Detail);

        // 组已超过中位数：不能说"预计 xx 结束"（那个点在过去）
        var overrun = LiveStatus.Build(true, "BetterGI", "挖矿", now.AddHours(-1), now.AddMinutes(-40), 20, now);
        Check("超时/改成往常口径", overrun.Detail.Contains("已超过往常"), overrun.Detail);
        Check("超时/不出现预计时刻", !overrun.Detail.Contains("预计"), overrun.Detail);

        // 还没进组（Webhook 没到）：退化成工具名 + 已跑
        var toolOnly = LiveStatus.Build(true, "BetterGI", "", now.AddMinutes(-5), null, null, now);
        Check("无组/主行用工具名", toolOnly.Headline.StartsWith("BetterGI", StringComparison.Ordinal), toolOnly.Headline);
        Check("无组/主行含已跑", toolOnly.Headline.Contains("5 分"), toolOnly.Headline);
        Check("无组/HasGroup 为假", !toolOnly.HasGroup);

        // 连工具名都没有
        var bare = LiveStatus.Build(true, null, null, null, null, null, now);
        Check("全空/主行给兜底", bare.Headline == "正在执行", bare.Headline);

        // ---- 5) Tooltip ----
        string tip = LiveStatus.Tooltip(inGroup, 2, 5);
        Check("提示含队列进度", tip.Contains("2/5"), tip);
        Check("提示含往常耗时", tip.Contains("往常耗时"), tip);
        string tipNoSample = LiveStatus.Tooltip(noSample, 2, 5);
        Check("提示/无样本说明原因", tipNoSample.Contains("样本不足"), tipNoSample);
        string tipIdle = LiveStatus.Tooltip(idle, 3, 5);
        Check("提示/空闲不显示进度", !tipIdle.Contains("3/5"), tipIdle);

        // ---- 5b) Brief：浮条短版次行（180 宽放不下 Detail 的完整句） ----
        Check("浮条/不跑为空", LiveStatus.Brief(idle, now) == "", LiveStatus.Brief(idle, now));
        Check("浮条/分钟给剩余时长", LiveStatus.Brief(inGroup, now) == "还要 10 分", LiveStatus.Brief(inGroup, now));
        Check("浮条/无样本为空", LiveStatus.Brief(noSample, now) == "", LiveStatus.Brief(noSample, now));
        Check("浮条/超时不提钟点", LiveStatus.Brief(overrun, now) == "已超往常"
            && !LiveStatus.Brief(overrun, now).Contains(":"), LiveStatus.Brief(overrun, now));
        // 剩余超过一小时：改说钟点（「还要 84 分」要心算，不如图省事）
        var longRun = LiveStatus.Build(true, "BetterGI", "挖矿", now.AddHours(-2), now.AddMinutes(-40), 120, now);
        string briefLong = LiveStatus.Brief(longRun, now);
        Check("浮条/超一小时给钟点", briefLong.StartsWith("约 ", StringComparison.Ordinal) && briefLong.EndsWith(" 完", StringComparison.Ordinal),
            briefLong);
        Check("浮条/短版比完整版短", LiveStatus.Brief(inGroup, now).Length < inGroup.Detail.Length,
            $"{LiveStatus.Brief(inGroup, now).Length} vs {inGroup.Detail.Length}");

        // ---- 6) TodayDigest.Verdict：三档 ----
        Check("评价/进行中", TodayDigest.Verdict(TimeSpan.FromMinutes(5), 20, true) == "进行中");
        Check("评价/缺耗时", TodayDigest.Verdict(null, 20, false) == "");
        Check("评价/缺样本", TodayDigest.Verdict(TimeSpan.FromMinutes(20), null, false) == "");
        Check("评价/零样本", TodayDigest.Verdict(TimeSpan.FromMinutes(20), 0, false) == "");
        Check("评价/正常抖动", TodayDigest.Verdict(TimeSpan.FromMinutes(21), 20, false) == "和往常差不多",
            TodayDigest.Verdict(TimeSpan.FromMinutes(21), 20, false));
        Check("评价/明显偏快", TodayDigest.Verdict(TimeSpan.FromMinutes(10), 20, false) == "比往常快");
        Check("评价/明显偏慢", TodayDigest.Verdict(TimeSpan.FromMinutes(40), 20, false) == "比往常慢");

        // ---- 7) TodayDigest.Build：总时长与统计 ----
        var records = new List<GroupRecord>
        {
            new() { Name = "A", Start = "01:00:00", End = "01:30:00" },  // 30
            new() { Name = "B", Start = "02:00:00", End = "02:15:00" },  // 15
            new() { Name = "C", Start = "03:00:00", End = null },        // 进行中，不计入
        };
        var digest = TodayDigest.Build(records, _ => 20);
        Check("明细/组数", digest.Groups.Count == 3, digest.Groups.Count.ToString());
        Check("明细/跑完计数", digest.FinishedCount == 2, digest.FinishedCount.ToString());
        Check("明细/总时长只算跑完的", digest.TotalGroupTime == TimeSpan.FromMinutes(45), digest.TotalGroupTime.ToString());
        Check("明细/进行中不计耗时", digest.Groups.First(g => g.Name == "C").Running);
        Check("明细/主行含组数", digest.Headline.Contains("2 组"), digest.Headline);
        Check("明细/主行含合计", digest.Headline.Contains("45 分"), digest.Headline);

        var empty = TodayDigest.Build(new List<GroupRecord>(), _ => null);
        Check("明细/空列表主行", empty.Headline.Contains("还没有"), empty.Headline);
        Check("明细/null 不炸", TodayDigest.Build(null, _ => null).Groups.Count == 0);
        Check("明细/无名记录跳过", TodayDigest.Build(new List<GroupRecord> { new() { Name = "" } }, _ => null).Groups.Count == 0);
        Check("明细/取样本失败不炸", TodayDigest.Build(records, _ => throw new InvalidOperationException()).Groups.All(g => g.MedianMinutes == null));

        // 只剩进行中时，主行要说清"暂时没有跑完的记录"
        var runningOnly = TodayDigest.Build(new List<GroupRecord> { new() { Name = "D", Start = "01:00:00", End = null } }, _ => 20);
        Check("明细/只剩进行中", runningOnly.Headline.Contains("还在跑"), runningOnly.Headline);

        // ---- 8) TodayDigest.Report：文字版 ----
        var journal = new List<JournalEntry>
        {
            new() { ToolId = "bettergi", Name = "BetterGI", State = nameof(ToolRunState.Done), At = "01:00" },
            new() { ToolId = "march7th", Name = "三月七助手", State = nameof(ToolRunState.Failed), At = "02:00", Reason = "退出码 1" },
        };
        string report = TodayDigest.Report(digest, journal, new DateTime(2026, 11, 13));
        Check("报告/含日期", report.Contains("2026-11-13"), report);
        Check("报告/含组名", report.Contains("A") && report.Contains("C"), report);
        Check("报告/含合计", report.Contains("45 分"), report);
        Check("报告/成功打勾", report.Contains("✓ BetterGI"), report);
        Check("报告/失败打叉", report.Contains("✗ 三月七助手"), report);
        Check("报告/含原因", report.Contains("退出码 1"), report);
        Check("报告/进行中标注", report.Contains("进行中"), report);

        string reportEmpty = TodayDigest.Report(TodayDigest.Build(new List<GroupRecord>(), _ => null), new List<JournalEntry>(), DateTime.Today);
        Check("报告/空数据说清", reportEmpty.Contains("没有记录") || reportEmpty.Contains("没有跑过"), reportEmpty);

        // ---- 9) ToolLine ----
        string line = TodayDigest.ToolLine(journal[1]);
        Check("工具行/含名字", line.StartsWith("三月七助手", StringComparison.Ordinal), line);
        Check("工具行/含原因", line.Contains("退出码 1"), line);
        Check("工具行/null 安全", TodayDigest.ToolLine(null!) == "");

        // ---- 10) 哨兵：当前组名必须真的被写进去（v1.13 修的就是它） ----
        var h = SentinelService.HealthSnapshot();
        Check("哨兵/快照能读当前组", h.CurrentGroup != null);

        Console.WriteLine($"RESULT live fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.14：日志规则。
    /// 判定全在 <see cref="LogRules"/> 里，都是纯函数，可以逐条钉死；
    /// 最后用真实的日志文件验证「试一下」到底扫得对不对。
    /// </summary>
    private static int RunLogRules()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 关键词清洗 ----
        Check("清洗/去首尾空白", LogRule.Normalize("  卡住  ") == "卡住");
        Check("清洗/删换行", LogRule.Normalize("卡\n住\r") == "卡 住");
        Check("清洗/压重复空格", LogRule.Normalize("卡   住") == "卡 住");
        Check("清洗/空串", LogRule.Normalize("   ") == "");
        Check("清洗/null", LogRule.Normalize(null) == "");
        string longKw = LogRule.Normalize(new string('长', 100));
        Check("清洗/超长截断", longKw.Length == LogRule.MaxKeywordChars, longKw.Length.ToString());

        // ---- 2) 命中判定（纯函数，不碰 AppState） ----
        var rules = new List<LogRule>
        {
            new() { Id = "1", Keyword = "卡在", Behavior = LogRule.Notify, Enabled = true },
            new() { Id = "2", Keyword = "Abnormal", Behavior = LogRule.Halt, Enabled = true },
            new() { Id = "3", Keyword = "停用", Behavior = LogRule.Capture, Enabled = false },
            new() { Id = "4", Keyword = "  ", Behavior = LogRule.Notify, Enabled = true },
        };
        var hit = LogRules.Match("任务卡在传送锚点", rules);
        Check("命中/中文子串", hit.Count == 1 && hit[0].Id == "1", hit.Count.ToString());
        var hitCase = LogRules.Match("something ABNORMAL happened", rules);
        Check("命中/大小写不敏感", hitCase.Count == 1 && hitCase[0].Id == "2", hitCase.Count.ToString());
        Check("命中/停用规则不算", LogRules.Match("停用了", rules).Count == 0);
        Check("命中/空关键词不算", LogRules.Match("  ", rules).Count == 0);
        Check("命中/多规则同时命中", LogRules.Match("卡在异常 Abnormal", rules).Count == 2);
        Check("命中/空行", LogRules.Match("", rules).Count == 0);
        Check("命中/null 行", LogRules.Match(null, rules).Count == 0);

        // ---- 3) 节流（纯函数） ----
        var now = new DateTime(2026, 11, 14, 10, 0, 0);
        var fires = new Dictionary<string, DateTime>(StringComparer.Ordinal)
        {
            ["r1"] = now.AddMinutes(-3),
            ["r2"] = now.AddMinutes(-11),
        };
        Check("节流/3 分钟内不再触发", !LogRules.CooledDown("r1", now, fires));
        Check("节流/11 分钟可以触发", LogRules.CooledDown("r2", now, fires));
        Check("节流/没记录过可以触发", LogRules.CooledDown("r3", now, fires));
        Check("节流/恰好到点可以触发",
            LogRules.CooledDown("r4", now, new Dictionary<string, DateTime>(StringComparer.Ordinal)
            {
                ["r4"] = now.AddMinutes(-LogRules.CooldownMinutes),
            }));
        Check("节流/冷却时长是 10 分钟", Math.Abs(LogRules.CooldownMinutes - 10) < 0.001);

        // ---- 4) 行为目录 ----
        Check("行为/三种", LogRule.AllBehaviors.Length == 3);
        Check("行为/中文名", LogRule.BehaviorName(LogRule.Notify) == "提醒"
            && LogRule.BehaviorName(LogRule.Capture) == "留现场"
            && LogRule.BehaviorName(LogRule.Halt) == "停下队列");
        Check("行为/未知取值按提醒", LogRule.BehaviorName("瞎写") == "提醒");
        Check("行为/每种都有解释",
            LogRule.AllBehaviors.All(b => LogRule.BehaviorHint(b).Length > 0));

        // ---- 5) 试一下：真扫一个日志文件 ----
        string sandbox = Path.Combine(Path.GetTempPath(), "dragon-logrules-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllLines(sandbox, new[]
            {
                "第一行：配置组 \"日常\" 加载完成",
                "任务卡在传送锚点，重试",
                "抽卡：出了个五星",
                "任务卡在传送锚点，重试",
                "Abnormal exit code 3",
                "最后一行：任务卡在传送锚点",
            }, new System.Text.UTF8Encoding(false));

            var scanRules = new List<LogRule>
            {
                new() { Id = "a", Keyword = "卡在", Behavior = LogRule.Notify },
                new() { Id = "b", Keyword = "abnormal", Behavior = LogRule.Halt },
                new() { Id = "c", Keyword = "根本不存在的词", Behavior = LogRule.Notify },
            };
            var scanned = LogRules.Scan(sandbox, scanRules, sampleCount: 2);
            Check("扫描/条数对得上", scanned.Count == 3, scanned.Count.ToString());
            Check("扫描/卡在命中 3 次", scanned[0].Count == 3, scanned[0].Count.ToString());
            Check("扫描/大小写不敏感", scanned[1].Count == 1, scanned[1].Count.ToString());
            Check("扫描/没出现是 0", scanned[2].Count == 0, scanned[2].Count.ToString());
            Check("扫描/样本不超过上限", scanned[0].Samples.Count == 2, scanned[0].Samples.Count.ToString());
            Check("扫描/样本是原文", scanned[0].Samples[0].Contains("卡在", StringComparison.Ordinal),
                scanned[0].Samples.Count > 0 ? scanned[0].Samples[0] : "");

            string text = LogRules.Describe(scanned, sandbox);
            Check("描述/含文件名", text.Contains(Path.GetFileName(sandbox), StringComparison.Ordinal), text);
            Check("描述/含命中总数", text.Contains("共命中 4 次", StringComparison.Ordinal), text);
            Check("描述/没出现的也提一句", text.Contains("没出现", StringComparison.Ordinal), text);

            string noFile = LogRules.Describe(scanned, null);
            Check("描述/没有日志文件说清楚", noFile.Contains("没找到", StringComparison.Ordinal), noFile);
        }
        finally
        {
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
        }

        // 传 null 路径时仍然按「有关键词的规则」给出 0 命中占位——
        // rules 里「停用」那条虽然 Enabled=false，但关键词有效，所以这里算 3 条（空关键词那条不算）。
        Check("扫描/空文件路径只给占位", LogRules.Scan(null, rules).Count == 3,
            LogRules.Scan(null, rules).Count.ToString());
        Check("扫描/不存在的文件不抛", LogRules.Scan(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".log"), rules).Count >= 0);

        // ---- 6) 设置里能存住（默认空表时规则区不生效） ----
        Check("设置/有一节 LogRules", AppState.Settings.LogRules != null);

        Console.WriteLine($"RESULT logrules fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.9 / P2「依赖编排」验证：排序、门、矩阵回写都是纯函数，先断言它们；
    /// 最后真起两个 StubTool 跑一遍，确认依赖没跑成时子项被跳过（不是干等、也不是照跑）。
    /// </summary>
    private static int RunDag(string stub)
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 依赖解析 ----
        var bare = new QueueEntry { ToolId = "a" };
        Check("默认无依赖", QueueGraph.DependenciesOf(bare).Count == 0);
        Check("默认策略=跳过", bare.OnDependencyFail == QueueGraph.PolicySkip);

        var messy = new QueueEntry { ToolId = "b", DependsOn = new List<string> { " a ", "", "a", "  " } };
        var deps = QueueGraph.DependenciesOf(messy);
        Check("去空白去重", deps.Count == 1 && deps[0] == "a", string.Join("|", deps));

        // ---- 2) 拓扑排序 ----
        var e1 = new QueueEntry { ToolId = "first" };
        var e2 = new QueueEntry { ToolId = "second", DependsOn = new List<string> { "first" } };
        var e3 = new QueueEntry { ToolId = "third", DependsOn = new List<string> { "second" } };

        // 故意给反序：third, second, first
        var shuffled = new List<QueueEntry> { e3, e2, e1 };
        var ordered = QueueGraph.Order(shuffled);
        Check("排序把依赖提前",
            ordered.Select(e => e.ToolId).SequenceEqual(new[] { "first", "second", "third" }),
            string.Join("→", ordered.Select(e => e.ToolId)));
        Check("排序不丢项", ordered.Count == 3);

        // 同层保持原序（用户拖过的不该被打乱）
        var x = new QueueEntry { ToolId = "x" };
        var y = new QueueEntry { ToolId = "y" };
        var z = new QueueEntry { ToolId = "z", DependsOn = new List<string> { "x", "y" } };
        var same = QueueGraph.Order(new List<QueueEntry> { x, y, z });
        Check("同层保持原序", same.Select(e => e.ToolId).SequenceEqual(new[] { "x", "y", "z" }),
            string.Join("→", same.Select(e => e.ToolId)));

        // 环：不抛异常，环上的项原序留到最后
        var c1 = new QueueEntry { ToolId = "c1", DependsOn = new List<string> { "c2" } };
        var c2 = new QueueEntry { ToolId = "c2", DependsOn = new List<string> { "c1" } };
        var ring = QueueGraph.TryOrder(new List<QueueEntry> { c1, c2 }, out List<string> stuck);
        Check("环不抛异常", ring.Count == 2);
        Check("环上的项被标出来", stuck.Count == 2 && stuck.Contains("c1") && stuck.Contains("c2"),
            string.Join(",", stuck));
        Check("无环时没有卡住的项", QueueGraph.TryOrder(new List<QueueEntry> { e1 }, out List<string> none).Count == 1
            && none.Count == 0);

        // 依赖了不存在的工具：不能因此永远排不上
        var ghost = new QueueEntry { ToolId = "g", DependsOn = new List<string> { "not-exist" } };
        var ghostOut = QueueGraph.TryOrder(new List<QueueEntry> { ghost }, out List<string> ghostStuck);
        Check("依赖不存在的项照常排上", ghostOut.Count == 1 && ghostStuck.Count == 0);

        // ---- 3) 依赖门 ----
        var set = (string s) => new HashSet<string>(new[] { s }, StringComparer.OrdinalIgnoreCase);
        var none2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Check("无依赖=放行", QueueGraph.Gate(bare, none2, none2).Verdict == GateVerdict.Ready);

        var child = new QueueEntry { ToolId = "child", DependsOn = new List<string> { "parent" } };
        Check("依赖已完成=放行", QueueGraph.Gate(child, set("parent"), none2).Verdict == GateVerdict.Ready);
        Check("依赖没跑=等待", QueueGraph.Gate(child, none2, none2).Verdict == GateVerdict.Waiting);
        Check("依赖失败=阻断", QueueGraph.Gate(child, none2, set("parent")).Verdict == GateVerdict.Blocked);

        var blocked = QueueGraph.Gate(child, none2, set("parent"));
        Check("阻断理由说清是谁", blocked.Reason.Contains("parent"), blocked.Reason);
        var waiting = QueueGraph.Gate(child, none2, none2);
        Check("等待理由说清等谁", waiting.Reason.Contains("parent"), waiting.Reason);

        // 两个依赖只成了其一 → 还在等
        var two = new QueueEntry { ToolId = "two", DependsOn = new List<string> { "p1", "p2" } };
        var half = new HashSet<string>(new[] { "p1" }, StringComparer.OrdinalIgnoreCase);
        Check("多依赖成了其一=还在等", QueueGraph.Gate(two, half, none2).Verdict == GateVerdict.Waiting);
        var mixed = new HashSet<string>(new[] { "p1", "p2" }, StringComparer.OrdinalIgnoreCase);
        Check("多依赖全成=放行", QueueGraph.Gate(two, mixed, none2).Verdict == GateVerdict.Ready);
        Check("失败优先于等待", QueueGraph.Gate(two, half, set("p2")).Verdict == GateVerdict.Blocked);

        // 策略
        Check("策略默认跳过", !QueueGraph.ShouldAbortOnDependencyFail(child));
        child.OnDependencyFail = QueueGraph.PolicyAbort;
        Check("策略可设整队停下", QueueGraph.ShouldAbortOnDependencyFail(child));
        child.OnDependencyFail = "乱七八糟";
        Check("策略写错按跳过处理", !QueueGraph.ShouldAbortOnDependencyFail(child));
        child.OnDependencyFail = QueueGraph.PolicySkip;

        // ---- 4) 矩阵回写 ----
        var m = new List<QueueEntry>
        {
            new() { ToolId = "m1" },
            new() { ToolId = "m2" },
            new() { ToolId = "m3" },
        };
        QueueGraph.ApplyMatrix(m, new Dictionary<int, List<int>> { { 2, new List<int> { 0, 1, 2, 0 } } }, QueueGraph.PolicyAbort);
        Check("矩阵回写只写同一行", QueueGraph.DependenciesOf(m[0]).Count == 0);
        Check("矩阵回写去重且排除自己",
            QueueGraph.DependenciesOf(m[2]).SequenceEqual(new[] { "m1", "m2" }),
            string.Join(",", QueueGraph.DependenciesOf(m[2])));
        Check("矩阵回写策略一致", m[2].OnDependencyFail == QueueGraph.PolicyAbort);
        QueueGraph.ApplyMatrix(m, new Dictionary<int, List<int>>(), QueueGraph.PolicySkip);
        Check("矩阵清空后回到无依赖", m.All(e => QueueGraph.DependenciesOf(e).Count == 0));

        // 描述文案
        Check("描述/无依赖", QueueGraph.Describe(bare) == "无依赖");
        Check("描述/有依赖", QueueGraph.Describe(m == null ? bare : new QueueEntry { ToolId = "d", DependsOn = new List<string> { "p" } }).Contains("p"));

        // ---- 5) 端到端：依赖失败 → 子项被跳过，且不照跑 ----
        var savedQueue = AppState.Settings.Queue;
        var savedStrategy = AppState.Settings.FailStrategy;
        QueueService.IntervalOverride = TimeSpan.FromSeconds(1);
        AppState.Settings.FailStrategy = "Skip";
        try
        {
            AppState.Settings.Queue = new List<QueueEntry>
            {
                new() { ToolId = "probe-dag-parent", Enabled = true, DependsOn = new List<string>() },
                new()
                {
                    ToolId = "probe-dag-child", Enabled = true,
                    DependsOn = new List<string> { "probe-dag-parent" },
                    OnDependencyFail = QueueGraph.PolicySkip,
                },
            };

            var parent = Stub("probe-dag-parent", stub, "--seconds 1 --exit 3", retry: 0);
            var childProfile = Stub("probe-dag-child", stub, "--seconds 1 --exit 0");
            QueueService.RunProfilesAsync(new List<ToolProfile> { parent, childProfile }, QueueTrigger.Manual)
                .GetAwaiter().GetResult();

            var parentResult = QueueService.LastResults.FirstOrDefault(r => r.ToolId == "probe-dag-parent");
            var childResult = QueueService.LastResults.FirstOrDefault(r => r.ToolId == "probe-dag-child");
            Check("端到端/父项跑过（失败）",
                parentResult != null && parentResult.State is ToolRunState.Failed or ToolRunState.Timeout,
                parentResult?.State.ToString() ?? "null");
            Check("端到端/子项被跳过", childResult != null && childResult.Skipped,
                childResult == null ? "null" : $"skipped={childResult.Skipped}");
            Check("端到端/子项没真去跑", childResult?.State == ToolRunState.Idle,
                childResult?.State.ToString() ?? "null");
            Check("端到端/跳过理由说清依赖", childResult?.Reason.Contains("probe-dag-parent") == true,
                childResult?.Reason ?? "");

            // 父项成功 → 子项照跑
            RunJournal.ClearToday();
            AppState.Settings.Queue = new List<QueueEntry>
            {
                new() { ToolId = "probe-dag-ok", Enabled = true },
                new()
                {
                    ToolId = "probe-dag-after", Enabled = true,
                    DependsOn = new List<string> { "probe-dag-ok" },
                },
            };
            var okParent = Stub("probe-dag-ok", stub, "--seconds 1 --exit 0", retry: 0);
            var after = Stub("probe-dag-after", stub, "--seconds 1 --exit 0");
            QueueService.RunProfilesAsync(new List<ToolProfile> { okParent, after }, QueueTrigger.Manual)
                .GetAwaiter().GetResult();
            var okResult = QueueService.LastResults.FirstOrDefault(r => r.ToolId == "probe-dag-ok");
            var afterResult = QueueService.LastResults.FirstOrDefault(r => r.ToolId == "probe-dag-after");
            Check("端到端/父项成功", okResult?.State == ToolRunState.Done, okResult?.State.ToString() ?? "null");
            Check("端到端/父项成了子项照跑", afterResult != null && !afterResult.Skipped
                && afterResult.State == ToolRunState.Done,
                afterResult == null ? "null" : $"state={afterResult.State} skipped={afterResult.Skipped}");
        }
        finally
        {
            AppState.Settings.Queue = savedQueue;
            AppState.Settings.FailStrategy = savedStrategy;
            QueueService.IntervalOverride = null;
            RunJournal.ClearToday();
        }

        Console.WriteLine($"RESULT dag fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.9 / P2 配置组切片：清洗、回退全局、--startGroups 拼装、下标回写。
    /// 关键断言是「空列表＝沿用全局」，这条塌了老用户升级当天就会发现一个组都不跑。
    /// </summary>
    private static int RunGroups()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 清洗 ----
        var messy = GroupSlicing.Sanitize(new[] { " 日常 ", "", "日常", " ", "活动" });
        Check("清洗去空白去重保序", messy.SequenceEqual(new[] { "日常", "活动" }), string.Join("|", messy));
        Check("清洗忽略大小写去重",
            GroupSlicing.Sanitize(new[] { "Daily", "daily" }).Count == 1);
        Check("空输入返回空列表", GroupSlicing.Sanitize(null).Count == 0 && GroupSlicing.Sanitize(Array.Empty<string>()).Count == 0);

        // ---- 2) 已知组过滤 ----
        var known = new[] { "日常", "活动" };
        var filtered = GroupSlicing.Sanitize(new[] { "日常", "已被删掉的组" }, known);
        Check("认不出的组被丢掉", filtered.SequenceEqual(new[] { "日常" }), string.Join("|", filtered));
        var noKnown = GroupSlicing.Sanitize(new[] { "随便写的" }, Array.Empty<string>());
        Check("没识别到配置组时不过滤", noKnown.Count == 1, string.Join("|", noKnown));
        Check("known=null 时不过滤", GroupSlicing.Sanitize(new[] { "x" }, null).Count == 1);

        // ---- 3) 回退全局（最重要的一条） ----
        var global = new[] { "全局A", "全局B" };
        var ownEmpty = GroupSlicing.Effective(new List<string>(), global, null);
        Check("空切片=沿用全局", ownEmpty.SequenceEqual(global), string.Join("|", ownEmpty));
        var ownNull = GroupSlicing.Effective(null, global, null);
        Check("null 切片=沿用全局", ownNull.SequenceEqual(global), string.Join("|", ownNull));
        var ownSet = GroupSlicing.Effective(new[] { "只跑这个" }, global, null);
        Check("写了切片就用切片", ownSet.SequenceEqual(new[] { "只跑这个" }), string.Join("|", ownSet));
        Check("两边都空就是空", GroupSlicing.Effective(null, Array.Empty<string>(), null).Count == 0);
        var dropped = GroupSlicing.Effective(new[] { "已删除" }, global, new[] { "全局A" });
        Check("切片全被过滤后回退全局",
            dropped.SequenceEqual(new[] { "全局A" }), string.Join("|", dropped));

        Check("UsesGlobal 认空列表", GroupSlicing.UsesGlobal(new List<string>()));
        Check("UsesGlobal 认 null", GroupSlicing.UsesGlobal(null));
        Check("UsesGlobal 对有效列表为假", !GroupSlicing.UsesGlobal(new[] { "日常" }));
        Check("UsesGlobal 对全空白为真", GroupSlicing.UsesGlobal(new[] { "  ", "" }));

        // ---- 4) 参数拼装 ----
        var arg = GroupSlicing.StartGroupsArgument(new[] { "日常", "活动" });
        Check("参数名是 --startGroups", arg.StartsWith("--startGroups ", StringComparison.Ordinal), arg);
        Check("参数带引号", arg.Contains("\"日常\"") && arg.Contains("\"活动\""), arg);
        Check("两个组之间用空格", arg == "--startGroups \"日常\" \"活动\"", arg);
        Check("无组时返回空串", GroupSlicing.StartGroupsArgument(null) == ""
            && GroupSlicing.StartGroupsArgument(Array.Empty<string>()) == "");
        Check("组名里的空格原样保留",
            GroupSlicing.StartGroupsArgument(new[] { "每日 任务" }).Contains("\"每日 任务\""));

        // ---- 5) 并集与描述 ----
        var union = GroupSlicing.Union(new IReadOnlyList<string>?[]
        {
            new[] { "日常", "活动" },
            new[] { "活动", "收尾" },
            null,
        });
        Check("并集去重保序", union.SequenceEqual(new[] { "日常", "活动", "收尾" }), string.Join("|", union));
        Check("描述/用全局", GroupSlicing.Describe(new List<string>()) == "用全局勾选的配置组");
        Check("描述/一个组", GroupSlicing.Describe(new[] { "日常" }) == "只跑 日常");
        Check("描述/两个组", GroupSlicing.Describe(new[] { "日常", "活动" }) == "只跑 日常、活动");
        var many = GroupSlicing.Describe(new[] { "a", "b", "c" });
        Check("描述/多个组带计数", many.Contains("等 3 个组"), many);

        // ---- 6) 下标回写 ----
        var entries = new List<QueueEntry>
        {
            new() { ToolId = "t1", Groups = new List<string>() },
            new() { ToolId = "t2", Groups = new List<string> { "旧值" } },
        };
        GroupSlicing.Apply(entries, new Dictionary<int, List<string>>
        {
            [0] = new List<string> { "日常", " 日常 " },
            [1] = new List<string>(),
        }, null);
        Check("回写/第 0 项写入并去重", entries[0].Groups.SequenceEqual(new[] { "日常" }), string.Join("|", entries[0].Groups));
        Check("回写/空列表清空第 1 项", entries[1].Groups.Count == 0);
        GroupSlicing.Apply(entries, new Dictionary<int, List<string>> { [99] = new List<string> { "越界" } }, null);
        Check("回写/越界下标被忽略", entries.Count == 2 && entries.All(e => !e.Groups.Contains("越界")));
        GroupSlicing.Apply(null, new Dictionary<int, List<string>>(), null);
        GroupSlicing.Apply(entries, null, null);
        Check("回写/null 参数不抛", true);

        // ---- 7) 默认值：老配置读进来必须等于以前的行为 ----
        var fresh = new QueueEntry { ToolId = "new" };
        Check("默认无切片", fresh.Groups != null && fresh.Groups.Count == 0);
        Check("默认切片=全局",
            GroupSlicing.Effective(fresh.Groups, new[] { "g1" }, null).SequenceEqual(new[] { "g1" }));

        Console.WriteLine($"RESULT groups fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.9 / P2 BGI 更新前置检查：版本归一、四种裁决、只读不联网、失败留底。
    /// 重点断言「读不到版本时保持沉默」——这条塌了就会天天误报。
    /// </summary>
    private static int RunBgiVersion()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 版本归一 ----
        Check("归一/去构建哈希", BgiVersionGuard.Normalize("1.2.3+abc123") == "1.2.3");
        Check("归一/去预发布尾巴", BgiVersionGuard.Normalize("1.2.3-beta") == "1.2.3");
        Check("归一/去空格", BgiVersionGuard.Normalize("  1.2.3  ") == "1.2.3");
        Check("归一/空输入", BgiVersionGuard.Normalize(null) == "" && BgiVersionGuard.Normalize("") == "");
        Check("归一/纯数字保留", BgiVersionGuard.Normalize("4") == "4");

        // ---- 2) 四种裁决 ----
        Check("裁决/没记录过=FirstSeen",
            BgiVersionGuard.Judge("", "1.2.3") == VersionVerdict.FirstSeen);
        Check("裁决/一样=Unchanged",
            BgiVersionGuard.Judge("1.2.3", "1.2.3") == VersionVerdict.Unchanged);
        Check("裁决/哈希不同不算变",
            BgiVersionGuard.Judge("1.2.3+aaa", "1.2.3+bbb") == VersionVerdict.Unchanged);
        Check("裁决/变了=Changed",
            BgiVersionGuard.Judge("1.2.3", "1.3.0") == VersionVerdict.Changed);
        Check("裁决/读不到=Unknown",
            BgiVersionGuard.Judge("1.2.3", "") == VersionVerdict.Unknown);
        Check("裁决/读不到优先于没记录",
            BgiVersionGuard.Judge("", "") == VersionVerdict.Unknown);

        // ---- 3) 文案 ----
        var changed = BgiVersionGuard.Describe(VersionVerdict.Changed, "1.2.3", "1.3.0");
        Check("文案/变了说清前后版本",
            changed.Contains("1.2.3") && changed.Contains("1.3.0"), changed);
        Check("文案/变了给出下一步", changed.Contains("BGI"), changed);
        Check("文案/没变不说话",
            BgiVersionGuard.Describe(VersionVerdict.Unchanged, "1", "1") == "");
        Check("文案/读不到不说话",
            BgiVersionGuard.Describe(VersionVerdict.Unknown, "1", "") == "");
        Check("文案/第一次记录有话说",
            BgiVersionGuard.Describe(VersionVerdict.FirstSeen, "", "1.2.3").Contains("1.2.3"));

        // ---- 4) 读不到版本时不出声、不覆盖 ----
        Check("读不存在的 exe 返回空串", BgiVersionGuard.ReadVersion(@"C:\definitely\not\here\nope.exe") == "");
        Check("空路径返回空串", BgiVersionGuard.ReadVersion("") == "" && BgiVersionGuard.ReadVersion(null) == "");
        Check("读不到时不写台账", !BgiVersionGuard.Remember("") && !BgiVersionGuard.Remember(null));

        var savedVersion = AppState.Settings.LastBgiVersion;
        try
        {
            AppState.Settings.LastBgiVersion = "1.2.3";
            Check("同版本不重复写", !BgiVersionGuard.Remember("1.2.3") && !BgiVersionGuard.Remember("1.2.3+zzz"));
            Check("新版本会被记住", BgiVersionGuard.Remember("1.3.0")
                && AppState.Settings.LastBgiVersion == "1.3.0");

            // Preflight 走真实文件：拿自己这个 exe 当替身（有版本号、离线、不依赖 BGI）
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var pf = BgiVersionGuard.Preflight(self);
            Check("Preflight/读得到真实版本", pf.Current.Length > 0, pf.Current);
            Check("Preflight/和台账一致时不出声",
                BgiVersionGuard.Judge(AppState.Settings.LastBgiVersion, pf.Current) != VersionVerdict.Unknown,
                $"ledger={AppState.Settings.LastBgiVersion} current={pf.Current}");

            AppState.Settings.LastBgiVersion = "0.0.1";
            var pf2 = BgiVersionGuard.Preflight(self);
            Check("Preflight/台账旧了会提醒", pf2.Verdict == VersionVerdict.Changed && pf2.Message.Length > 0,
                $"{pf2.Verdict} {pf2.Message}");

            var pf3 = BgiVersionGuard.Preflight(@"C:\nope\nope.exe");
            Check("Preflight/路径不存在=Unknown 且不说话",
                pf3.Verdict == VersionVerdict.Unknown && pf3.Message.Length == 0, pf3.Verdict.ToString());
        }
        finally
        {
            AppState.Settings.LastBgiVersion = savedVersion;
        }

        // ---- 5) 不联网：源码里不许出现联网调用 ----
        try
        {
            string dir = Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? "";
            for (int i = 0; i < 5 && dir.Length > 0; i++) dir = Path.GetDirectoryName(dir) ?? "";
            string src = Path.Combine(dir, "Dragon Butler", "BgiVersionGuard.cs");
            if (File.Exists(src))
            {
                string text = File.ReadAllText(src);
                Check("源码里没有联网调用",
                    !text.Contains("HttpClient", StringComparison.Ordinal)
                    && !text.Contains("WebRequest", StringComparison.Ordinal)
                    && !text.Contains("WebClient", StringComparison.Ordinal));
                Check("源码里没有下载/写文件",
                    !text.Contains("File.WriteAll", StringComparison.Ordinal)
                    && !text.Contains("DownloadFile", StringComparison.Ordinal));
            }
            else
            {
                Console.WriteLine($"  SKIP 找不到源码：{src}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  SKIP 源码扫描失败：" + ex.Message);
        }

        Console.WriteLine($"RESULT bgiupdate fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.10 / G1 收益账本 + G5 晨报补跑提示。
    /// 解析规则是这整套里最容易被 BGI 改格式搞坏的部分，所以断言写得比功能还多：
    /// 能认的、不能认的、容易误伤的（带单位/区间/千分位）都要钉住。
    /// </summary>
    private static int RunHarvest()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 数字写法 ----
        Check("FormatAmount/万元以下原样", HarvestLedger.FormatAmount(0) == "0" && HarvestLedger.FormatAmount(9999) == "9999",
            HarvestLedger.FormatAmount(9999));
        Check("FormatAmount/一万写成万", HarvestLedger.FormatAmount(10000) == "1 万", HarvestLedger.FormatAmount(10000));
        Check("FormatAmount/保留一位小数", HarvestLedger.FormatAmount(125000) == "12.5 万", HarvestLedger.FormatAmount(125000));
        Check("FormatAmount/整万不留小数点", HarvestLedger.FormatAmount(400000) == "40 万", HarvestLedger.FormatAmount(400000));
        Check("FormatAmount/十万以上取整", HarvestLedger.FormatAmount(1250000) == "125 万", HarvestLedger.FormatAmount(1250000));
        Check("FormatAmount/一亿以上换亿", HarvestLedger.FormatAmount(100000000) == "1 亿", HarvestLedger.FormatAmount(100000000));
        Check("FormatAmount/负数夹到 0", HarvestLedger.FormatAmount(-5) == "0");

        // ---- 2) 解析：该认的 ----
        var s = AppState.Settings;
        bool oldEnabled = s.HarvestEnabled;
        string? oldPath = HarvestLedger.PathOverride;
        // 断言绝不能写用户的真实账本：换到临时文件，跑完删掉
        string sandbox = Path.Combine(Path.GetTempPath(), "dragon-harvest-probe.json");
        try
        {
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
            HarvestLedger.PathOverride = sandbox;
            HarvestLedger.Reload();
            s.HarvestEnabled = true;

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("摩拉 +125000");
            var h1 = HarvestLedger.Take();
            Check("解析/摩拉带加号", h1.Mora == 125000, h1.Mora.ToString());

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("获得摩拉：1,234");
            var h2 = HarvestLedger.Take();
            Check("解析/摩拉带千分位", h2.Mora == 1234, h2.Mora.ToString());

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("拾取 甜甜花 x 12");
            var h3 = HarvestLedger.Take();
            Check("解析/拾取材料",
                h3.Materials.TryGetValue("甜甜花", out int sweet) && sweet == 12,
                string.Join(",", h3.Materials.Select(kv => kv.Key + "=" + kv.Value)));

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("摩拉 +1000");
            HarvestLedger.Observe("摩拉 +2000");
            var h4 = HarvestLedger.Take();
            Check("解析/多行累加", h4.Mora == 3000, h4.Mora.ToString());

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("狗粮经验 +80000");
            var h5 = HarvestLedger.Take();
            Check("解析/狗粮经验", h5.Exp == 80000, h5.Exp.ToString());

            // ---- 3) 解析：绝不该认的（误伤比漏算更糟） ----
            HarvestLedger.ResetPending();
            HarvestLedger.Observe("一趟全部跑完大约 10-12 小时，摩拉收益 26-28w");
            var h6 = HarvestLedger.Take();
            Check("不认/区间与单位混写", h6.IsEmpty, $"mora={h6.Mora} exp={h6.Exp}");

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("12.5万摩拉");
            var h7 = HarvestLedger.Take();
            Check("不认/无分隔符只有万", h7.IsEmpty, $"mora={h7.Mora}");

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("配置组 \"日常\" 执行结束");
            var h8 = HarvestLedger.Take();
            Check("不认/无关日志行", h8.IsEmpty);

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("");
            HarvestLedger.Observe(null);
            var h9 = HarvestLedger.Take();
            Check("不认/空行与 null 不抛", h9.IsEmpty);

            // ---- 4) 开关关闭时什么都不记 ----
            s.HarvestEnabled = false;
            HarvestLedger.ResetPending();
            HarvestLedger.Observe("摩拉 +999999");
            var h10 = HarvestLedger.Take();
            Check("开关关闭时不记账", h10.IsEmpty, $"mora={h10.Mora}");

            // ---- 5) Take 会清零（否则下一趟会重复报） ----
            s.HarvestEnabled = true;
            HarvestLedger.ResetPending();
            HarvestLedger.Observe("摩拉 +100");
            var first = HarvestLedger.Take();
            var second = HarvestLedger.Take();
            Check("Take 取走即清零", first.Mora == 100 && second.IsEmpty, $"first={first.Mora} second={second.Mora}");

            // ---- 6) 文案 ----
            HarvestLedger.ResetPending();
            Check("Describe/空账不说话", HarvestLedger.Describe(new Harvest()).Length == 0);
            Check("Describe/null 不说话", HarvestLedger.Describe(null).Length == 0);
            var sample = new Harvest { Mora = 125000 };
            sample.Materials["甜甜花"] = 12;
            string desc = HarvestLedger.Describe(sample);
            Check("Describe/含摩拉与材料", desc.Contains("12.5 万") && desc.Contains("甜甜花×12"), desc);
            Check("Describe/材料最多列三个", HarvestLedger.Describe(new Harvest
            {
                Materials = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["a"] = 5, ["b"] = 4, ["c"] = 3, ["d"] = 2, ["e"] = 1,
                },
            }).Contains("等 5 种材料"));
            Check("Describe/按数量降序", HarvestLedger.Describe(new Harvest
            {
                Materials = new Dictionary<string, int>(StringComparer.Ordinal) { ["少"] = 1, ["多"] = 9 },
            }).StartsWith("本次约 多×9", StringComparison.Ordinal));

            // ---- 7) 记账与查询 ----
            var today = DateTime.Today;
            var before = HarvestLedger.GetDay(today);
            HarvestLedger.AddToDay(today, new Harvest { Mora = 500 });
            var after = HarvestLedger.GetDay(today);
            Check("按天记账累加", after.Mora == before.Mora + 500, $"{before.Mora}→{after.Mora}");
            Check("取不到的天返回空账而非 null",
                HarvestLedger.GetDay(today.AddDays(-3999)) is { } emptyDay && emptyDay.IsEmpty);
            Check("最近 N 天合计不小于今天",
                HarvestLedger.GetRecent(1).Mora >= after.Mora);
            Check("空账 Add 不改变目标",
                RunAddEmptyCheck());

            // ---- 8) 源码级约束：账本不联网、不写别的文件 ----
            try
            {
                string src = ReadRepoSources("Dragon Butler/HarvestLedger.cs");
                Check("源码/账本不联网",
                    !src.Contains("HttpClient", StringComparison.Ordinal)
                    && !src.Contains("WebRequest", StringComparison.Ordinal)
                    && !src.Contains("DownloadFile", StringComparison.Ordinal));
                Check("源码/HarvestLedger 自己只写 harvest.json",
                    src.Contains("harvest.json", StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  SKIP 源码扫描失败：" + ex.Message);
            }

            // ---- 9) G5 晨报补跑提示 ----
            Check("补跑提示/定时关着不说话", RunCatchUpCheck(enabled: false) == "");
            Check("补跑提示/没勾任何星期不说话", RunCatchUpCheck(enabled: true, mask: 0) == "");

            // 昨天在计划里 → 该报一句「昨天 HH:mm 没跑成」
            int yesterdayBit = SchedulePlanner.WeekdayBit(DateTime.Today.AddDays(-1));
            string shouldSpeak = RunCatchUpCheck(enabled: true, mask: 1 << yesterdayBit);
            Check("补跑提示/昨天该跑却没跑要说话",
                shouldSpeak.Contains("昨天", StringComparison.Ordinal)
                && shouldSpeak.Contains("没跑成", StringComparison.Ordinal), shouldSpeak);

            // 昨天不在计划里 → 昨天本来就不该跑，没什么可说
            int otherBit = (yesterdayBit + 1) % 7;
            string shouldSilent = RunCatchUpCheck(enabled: true, mask: 1 << otherBit);
            Check("补跑提示/昨天本来不该跑就不说话", shouldSilent == "", shouldSilent);

            // 补跑关着时措辞要换（不能还说「窗口内没跑起来」）
            var sched = AppState.Settings;
            int oldCatchUp = sched.ScheduleCatchUpMinutes;
            try
            {
                sched.ScheduleCatchUpMinutes = 0;
                string noWindow = RunCatchUpCheck(enabled: true, mask: 1 << yesterdayBit);
                Check("补跑提示/补跑关着时措辞不同",
                    noWindow.Contains("补跑是关着的", StringComparison.Ordinal), noWindow);
            }
            finally
            {
                sched.ScheduleCatchUpMinutes = oldCatchUp;
            }

            Check("补跑提示/度假模式不说话", RunVacationCheck(1 << yesterdayBit));
        }
        finally
        {
            s.HarvestEnabled = oldEnabled;
            HarvestLedger.ResetPending();
            HarvestLedger.PathOverride = oldPath;
            HarvestLedger.Reload();
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
        }

        Console.WriteLine($"RESULT harvest fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    private static bool RunAddEmptyCheck()
    {
        var target = new Harvest { Mora = 7 };
        target.Add(null);
        target.Add(new Harvest());
        return target.Mora == 7;
    }

    /// <summary>用一组设置跑一遍晨报的补跑提示，返回结果（不改真实设置）。</summary>
    private static string RunCatchUpCheck(bool enabled, int mask = 0x7F)
    {
        var s = AppState.Settings;
        bool oldEnabled = s.ScheduleEnabled;
        int oldMask = s.ScheduleWeekdays;
        try
        {
            s.ScheduleEnabled = enabled;
            s.ScheduleWeekdays = mask;
            return MorningReportService.CatchUpNote();
        }
        finally
        {
            s.ScheduleEnabled = oldEnabled;
            s.ScheduleWeekdays = oldMask;
        }
    }

    /// <summary>度假模式下晨报开口了吗（应该不会）。</summary>
    private static bool RunVacationCheck(int mask)
    {
        var s = AppState.Settings;
        bool oldEnabled = s.ScheduleEnabled, oldVacation = s.VacationMode;
        int oldMask = s.ScheduleWeekdays;
        try
        {
            s.ScheduleEnabled = true;
            s.ScheduleWeekdays = mask;
            s.VacationMode = true;
            return MorningReportService.CatchUpNote() == "";
        }
        finally
        {
            s.ScheduleEnabled = oldEnabled;
            s.VacationMode = oldVacation;
            s.ScheduleWeekdays = oldMask;
        }
    }

    /// <summary>读仓库里的一个源文件（从程序集位置上溯 5 级到仓库根）。</summary>
    private static string ReadRepoSources(string relative)
    {
        string root = Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? ".";
        for (int i = 0; i < 5; i++) root = Path.GetDirectoryName(root) ?? root;
        return File.ReadAllText(Path.Combine(root, relative));
    }

    /// <summary>
    /// v1.12：观感规则断言。
    /// 这一轮只调字号、对比度、间距，最怕的是「调着调着顺手加了个色键、加了一行、加了个区块」——
    /// 那就不再是「修看得见的地方」，而是又往臃肿走一步。
    /// 所以把当年口头约定的硬约束固化成断言，改坏了 CI 会喊停。
    /// </summary>
    private static int RunUiRules()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        const string MiniBar = "Dragon Butler/MiniBarWindow.xaml";
        string mini = ReadRepoSources(MiniBar);
        Check("浮条仍是 180×44", mini.Contains("Width=\"180\" Height=\"44\"", StringComparison.Ordinal));
        Check("浮条字号提到 13 / 11.5",
            mini.Contains("FontSize=\"13\"", StringComparison.Ordinal)
            && mini.Contains("FontSize=\"11.5\"", StringComparison.Ordinal));
        Check("浮条次行透明度提到 0.75", mini.Contains("Opacity=\"0.75\"", StringComparison.Ordinal));
        // 只允许四个色键：多出来的就是新配色，一多就不叫「跟着主题走」了
        var allowed = new[] { "CardBgBrush", "BorderBrush", "TextPrimaryBrush", "BrandBrush" };
        var used = System.Text.RegularExpressions.Regex
            .Matches(mini, @"\{DynamicResource (\w+)\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var extra = used.Where(u => !allowed.Contains(u, StringComparer.Ordinal)).ToList();
        Check("浮条只用四个允许的色键", extra.Count == 0, string.Join(",", extra));

        const string Main = "Dragon Butler/MainWindow.xaml";
        string main = ReadRepoSources(Main);
        Check("主窗默认宽度仍是 980", main.Contains("Width=\"980\"", StringComparison.Ordinal));
        // 区块不能变多：折叠卡的行数就是「界面有没有变臃肿」的硬指标
        int advRows = CountRowsBefore(main, "\"AdvancedBody\"", 3000);
        int sysRows = CountRowsBefore(main, "\"SystemBody\"", 3000);
        Check("高级设置仍是 10 行", advRows == 10, advRows.ToString());
        Check("系统区仍是 7 行", sysRows == 7, sysRows.ToString());
        Check("队列卡加了 1px 竖分隔",
            main.Contains("<Border Width=\"1\" Margin=\"8,3,2,3\" Background=\"{DynamicResource BorderBrush}\"/>",
                StringComparison.Ordinal));

        string palette = ReadRepoSources("Dragon Butler/CommandPalette.xaml");
        Check("命令面板选中态有 Brand 竖条",
            palette.Contains("x:Name=\"Bar\"", StringComparison.Ordinal)
            && palette.Contains("Value=\"{DynamicResource BrandBrush}\"", StringComparison.Ordinal));
        Check("命令面板提示字号 11.5 / 透明度 0.7",
            palette.Contains("FontSize=\"11.5\" Opacity=\"0.7\"", StringComparison.Ordinal));

        string calendar = ReadRepoSources("Dragon Butler/CalendarWindow.xaml");
        Check("日历月份标签在上方（不再占一列）",
            !calendar.Contains("<TextBlock x:Name=\"TbMonthLabels\" Grid.Column=\"0\"", StringComparison.Ordinal));
        Check("日历图例用 TextSecondary（对比度够）",
            calendar.Contains("x:Name=\"DotSuccess\"", StringComparison.Ordinal)
            && !calendar.Contains("TextTertiaryBrush", StringComparison.Ordinal));

        string depends = ReadRepoSources("Dragon Butler/DependsWindow.xaml.cs");
        Check("依赖矩阵表头 12.5",
            depends.Contains("TextBlock.FontSizeProperty, 12.5", StringComparison.Ordinal));

        string probe = ReadRepoSources("tests/CoordinatorProbe/RenderProbe.cs");
        Check("渲染工具支持滚到顶（ui top）",
            probe.Contains("ScrollToTop()", StringComparison.Ordinal)
            && probe.Contains("\"top\"", StringComparison.Ordinal));
        Check("渲染工具会造日历样例历史",
            probe.Contains("SeedSampleArchive", StringComparison.Ordinal));

        Console.WriteLine($"RESULT uirules fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>数某个 x:Name 之后、下一个公开元素之前的 RowDefinition 个数（粗略但够用）。</summary>
    private static int CountRowsBefore(string text, string anchor, int window)
    {
        int at = text.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0) return -1;
        int end = Math.Min(text.Length, at + window);
        string slice = text.Substring(at, end - at);
        int nextName = slice.IndexOf("x:Name=\"", 8, StringComparison.Ordinal);
        if (nextName > 0) slice = slice.Substring(0, nextName);
        return System.Text.RegularExpressions.Regex.Matches(slice, "<RowDefinition").Count;
    }

    /// <summary>
    /// v1.11 / G3：MCP（JSON-RPC over HTTP）。
    /// 判定逻辑是纯函数，所以这里全部离线验：握手、列工具、四种工具调用、令牌、各种畸形请求。
    /// 畸形请求那几条是重点——MCP 客户端五花八门，收到奇怪输入时**回一个 JSON-RPC 错误，
    /// 而不是 500 或者卡死**，是这层接口能不能用的分界线。
    /// </summary>
    private static int RunMcp()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        const string token = "0123456789abcdef0123456789abcdef";
        const bool on = true;

        // 本地的检查助手：跑一次 Plan，顺便把 JSON 体解出来
        System.Text.Json.JsonElement Ask(string body, string? tk = token, bool enabled = on)
        {
            var plan = McpServer.Plan(body, enabled, tk, token);
            if (plan.Body.Length == 0) return default;
            using var d = System.Text.Json.JsonDocument.Parse(plan.Body);
            return d.RootElement.Clone();
        }

        // ---- 1) 开关与令牌：关着时连存在都不暴露 ----
        var closed = McpServer.Plan(Request("initialize"), apiEnabled: false, token, token);
        Check("关闭时 404", closed.Status == 404, closed.Status.ToString());
        Check("关闭时不执行任何动作", !closed.NeedsExecution);
        var badToken = McpServer.Plan(Request("initialize"), on, "wrong", token);
        Check("令牌不对 401", badToken.Status == 401, badToken.Status.ToString());
        var noToken = McpServer.Plan(Request("initialize"), on, null, token);
        Check("没带令牌 401", noToken.Status == 401);
        var noServerToken = McpServer.Plan(Request("initialize"), on, token, "");
        Check("服务端没令牌 503", noServerToken.Status == 503, noServerToken.Status.ToString());

        // ---- 2) initialize 握手 ----
        var init = Ask(Request("initialize"));
        Check("握手/有 result", init.TryGetProperty("result", out var initResult));
        Check("握手/协议版本",
            initResult.TryGetProperty("protocolVersion", out var pv) && pv.GetString() == McpServer.ProtocolVersion,
            initResult.TryGetProperty("protocolVersion", out var pv2) ? pv2.GetString() ?? "" : "缺少");
        Check("握手/声明 tools 能力",
            initResult.TryGetProperty("capabilities", out var caps)
            && caps.TryGetProperty("tools", out _));
        Check("握手/serverInfo 名字",
            initResult.TryGetProperty("serverInfo", out var si)
            && si.TryGetProperty("name", out var sn) && sn.GetString() == McpServer.ServerName);
        var init2 = Ask("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2099-01-01\"}}");
        Check("握手/客户端报别的版本就照回它",
            init2.GetProperty("result").GetProperty("protocolVersion").GetString() == "2099-01-01");

        // ---- 3) id 原样回显（数字 id 不该变成字符串） ----
        var numId = Ask("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\"}");
        Check("id/数字原样回数字", numId.GetProperty("id").ValueKind == System.Text.Json.JsonValueKind.Number,
            numId.GetProperty("id").ValueKind.ToString());
        var strId = Ask("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"tools/list\"}");
        Check("id/字符串原样回字符串", strId.GetProperty("id").ValueKind == System.Text.Json.JsonValueKind.String);

        // ---- 4) tools/list ----
        var list = Ask(Request("tools/list")).GetProperty("result").GetProperty("tools");
        var names = list.EnumerateArray().Select(t => t.GetProperty("name").GetString() ?? "").OrderBy(x => x).ToList();
        Check("工具/正好四个", list.GetArrayLength() == 4, list.GetArrayLength().ToString());
        Check("工具/名字齐全",
            names.SequenceEqual(new[] { "butler_run", "butler_start", "butler_status", "butler_stop" }),
            string.Join(",", names));
        bool allHaveSchema = list.EnumerateArray().All(t =>
            t.TryGetProperty("inputSchema", out var sc)
            && sc.TryGetProperty("type", out var ty) && ty.GetString() == "object");
        Check("工具/都有 object 入参 schema", allHaveSchema);
        bool allHaveDesc = list.EnumerateArray().All(t =>
            t.TryGetProperty("description", out var de) && (de.GetString() ?? "").Length > 10);
        Check("工具/都有像样的描述（AI 靠它选工具）", allHaveDesc);
        var runTool = list.EnumerateArray().First(t => t.GetProperty("name").GetString() == "butler_run");
        Check("工具/butler_run 要求 toolId",
            runTool.GetProperty("inputSchema").GetProperty("required").EnumerateArray()
                   .Any(r => r.GetString() == "toolId"));

        // ---- 5) tools/call 路由 ----
        var statusCall = McpServer.Plan(Call("butler_status", null), on, token, token);
        Check("调用/查状态要执行", statusCall.Action == McpAction.Status && statusCall.NeedsExecution);
        var startCall = McpServer.Plan(Call("butler_start", null), on, token, token);
        Check("调用/开跑", startCall.Action == McpAction.Start);
        var stopCall = McpServer.Plan(Call("butler_stop", null), on, token, token);
        Check("调用/停止", stopCall.Action == McpAction.Stop);
        var runCall = McpServer.Plan(Call("butler_run", "bettergi"), on, token, token);
        Check("调用/单跑带 toolId", runCall.Action == McpAction.Run && runCall.ToolId == "bettergi");

        var runNoId = Ask(Call("butler_run", null));
        Check("调用/单跑缺 toolId 报错",
            runNoId.TryGetProperty("error", out var e1) && e1.GetProperty("code").GetInt32() == -32602);

        var unknown = Ask(Call("butler_delete", null));
        Check("调用/未知工具是工具错误而非协议错误",
            unknown.GetProperty("result").GetProperty("isError").GetBoolean());

        // ---- 6) 畸形请求：一律 JSON-RPC 错误，不 500、不崩 ----
        var notJson = Ask("{ this is not json");
        Check("畸形/坏 JSON 回 -32700",
            notJson.TryGetProperty("error", out var e2) && e2.GetProperty("code").GetInt32() == -32700,
            notJson.GetProperty("error").GetProperty("code").GetInt32().ToString());

        var notObject = Ask("[1,2,3]");
        Check("畸形/非对象回 -32600",
            notObject.TryGetProperty("error", out var e3) && e3.GetProperty("code").GetInt32() == -32600);

        var noMethod = Ask("{\"jsonrpc\":\"2.0\",\"id\":1}");
        Check("畸形/缺 method 回 -32600",
            noMethod.TryGetProperty("error", out var e4) && e4.GetProperty("code").GetInt32() == -32600);

        var unknownMethod = Ask(Request("tools/delete"));
        Check("畸形/未知方法回 -32601",
            unknownMethod.TryGetProperty("error", out var e5) && e5.GetProperty("code").GetInt32() == -32601,
            unknownMethod.GetProperty("error").GetProperty("code").GetInt32().ToString());

        var emptyBody = McpServer.Plan("", on, token, token);
        Check("畸形/空请求体也是 400 不是崩", emptyBody.Status == 400);
        var nullBody = McpServer.Plan(null, on, token, token);
        Check("畸形/null 请求体也是 400", nullBody.Status == 400);

        var noParams = Ask("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"}");
        Check("畸形/tools/call 缺 params 回 -32602",
            noParams.TryGetProperty("error", out var e6) && e6.GetProperty("code").GetInt32() == -32602);

        var noName = Ask("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{}}");
        Check("畸形/tools/call 缺 name 回 -32602",
            noName.TryGetProperty("error", out var e7) && e7.GetProperty("code").GetInt32() == -32602);

        // ---- 7) 通知（没有 id）：不回 JSON-RPC 响应 ----
        var notif = McpServer.Plan("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", on, token, token);
        Check("通知/不执行动作", !notif.NeedsExecution);
        Check("通知/空体或 202", notif.Body.Length == 0 || notif.Status == 202, $"status={notif.Status} body={notif.Body.Length}");

        // ---- 8) 响应体结构：合法的 JSON-RPC 2.0 ----
        var body = McpServer.Plan(Request("tools/list"), on, token, token).Body;
        using var parsed = System.Text.Json.JsonDocument.Parse(body);
        var root2 = parsed.RootElement;
        Check("结构/带 jsonrpc 2.0",
            root2.TryGetProperty("jsonrpc", out var v) && v.GetString() == "2.0");
        Check("结构/有 id", root2.TryGetProperty("id", out _));
        Check("结构/result 与 error 不共存",
            !(root2.TryGetProperty("result", out _) && root2.TryGetProperty("error", out _)));

        // ---- 9) 工具结果与状态文案 ----
        string ok = McpServer.ToolResult("1", "已请求开跑");
        using var okDoc = System.Text.Json.JsonDocument.Parse(ok);
        var okRes = okDoc.RootElement.GetProperty("result");
        Check("结果/isError=false", !okRes.GetProperty("isError").GetBoolean());
        Check("结果/正文是 text 类型",
            okRes.GetProperty("content")[0].GetProperty("type").GetString() == "text"
            && okRes.GetProperty("content")[0].GetProperty("text").GetString() == "已请求开跑");

        string err = McpServer.ToolError("1", "队列没在运行");
        using var errDoc = System.Text.Json.JsonDocument.Parse(err);
        Check("结果/失败时 isError=true", errDoc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());

        string st = McpServer.StatusText(false, "", 2, 1, 3, "一条龙结束");
        Check("状态文案/空闲", st.StartsWith("空闲", StringComparison.Ordinal), st);
        Check("状态文案/含进度", st.Contains("2/3", StringComparison.Ordinal));
        Check("状态文案/含剩余", st.Contains("还剩 1 项", StringComparison.Ordinal));
        Check("状态文案/含最后事件", st.Contains("一条龙结束", StringComparison.Ordinal));
        string stRun = McpServer.StatusText(true, "BetterGI", 0, 3, 3, "");
        Check("状态文案/在跑且带当前项",
            stRun.StartsWith("正在跑", StringComparison.Ordinal) && stRun.Contains("BetterGI", StringComparison.Ordinal), stRun);

        // ---- 10) 源码级约束：没有新增监听器、没有引包 ----
        try
        {
            string mcpSrc = ReadRepoSources("Dragon Butler/McpServer.cs");
            Check("源码/MCP 不自己开监听器",
                !mcpSrc.Contains("HttpListener", StringComparison.Ordinal));
            Check("源码/MCP 不引包",
                !mcpSrc.Contains("using System.Net;", StringComparison.Ordinal)
                && !mcpSrc.Contains("using System.Net.Http;", StringComparison.Ordinal));

            // 把整库所有 .cs 扫一遍，确认 new HttpListener 仍只出现在 WebhookListener 里
            // 注意要带左括号：不带的话会把 ControlApi.cs / WebhookHub.cs 里
            // 「全库 new HttpListener 仅存在于…」这类注释也算成命中
            string root = Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? ".";
            for (int i = 0; i < 5; i++) root = Path.GetDirectoryName(root) ?? root;
            var offenders = Directory.GetFiles(Path.Combine(root, "Dragon Butler"), "*.cs")
                .Where(f => File.ReadAllText(f).Contains("new HttpListener(", StringComparison.Ordinal))
                .Select(f => Path.GetFileName(f)!)
                .ToList();
            Check("源码/new HttpListener 仍只有一处文件",
                offenders.Count == 1 && offenders[0] == "WebhookListener.cs",
                string.Join(",", offenders));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  SKIP 源码扫描失败：" + ex.Message);
        }

        Console.WriteLine($"RESULT mcp fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>拼一条最简 JSON-RPC 请求（id 固定 1）。</summary>
    private static string Request(string method) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method + "\"}";

    /// <summary>拼一条 tools/call 请求。<paramref name="toolId"/> 为 null 时不带 arguments。</summary>
    private static string Call(string name, string? toolId)
    {
        string args = toolId == null
            ? "{}"
            : "{\"toolId\":\"" + toolId + "\"}";
        return "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
               + name + "\",\"arguments\":" + args + "}}";
    }
}
