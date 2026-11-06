using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

        if (!File.Exists(stub) && mode is not ("help" or "clipdialog"))
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
}
