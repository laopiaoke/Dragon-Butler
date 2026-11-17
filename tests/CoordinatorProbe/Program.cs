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
            "template" => RunTemplate(),
            "bgiwrite" => RunBgiWrite(),
            "completion" => RunCompletion(),
        "presets" => RunPresets(),
        "drop" => RunDrop(),
        "update" => RunUpdate(),
            "backup" => RunBackup(),
            "preflight" => RunPreflight(),
            "recap" => RunRecap(),
            "decision" => RunDecision(),
            "baseline" => RunBaseline(),
            "timebank" => RunTimeBank(),
            "away" => RunAway(),
            "handover" => RunHandover(),
            "troubleshoot" => RunTroubleshoot(),
            "tidy" => RunTidy(),
            "suggest" => RunSuggest(),
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
            "favorites" => RunFavorites(),
            "dag" => RunDag(stub),
            "groups" => RunGroups(),
            "bgiupdate" => RunBgiVersion(),
            "harvest" => RunHarvest(),
            "mcp" => RunMcp(),
            "uirules" => RunUiRules(),
            "ui" => RenderProbe.RunUi(args.Length > 1 ? args[1] : "all"),
            "mem" => MemoryProbe.Run(),
            "trim" => MemoryProbe.RunTrim(),
            "toolgrid" => RunToolGrid(),
            "toolcatalog" => RunToolCatalog(),
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

    /// <summary>
    /// v1.15：配置组合（多套「一条龙配置名 + 组名」反复用）。
    /// 借的是 AUTO-MAS 的多配置思路，但只保留「记住几套组合、开跑时挑一套」——
    /// 不做注入、不做自动切号，所以断言的重点全在「不会把用户的东西弄丢/混错」。
    /// </summary>
    /// <summary>
    /// v1.16 / 拖放识别。核心断言只有一句：**认不出就不猜**。
    /// 把 BetterGI 的路径写给别人，用户下次点开会得到一个起不来的工具，
    /// 而他并不知道是自己拖错了——这比「什么都没发生」糟得多。
    /// </summary>
    private static int RunDrop()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // 假磁盘：路径 → 是否存在；目录 → 里面有哪些文件
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\G\BetterGI\BetterGI.exe",
            @"C:\M\March7thAssistant.exe",
            @"C:\W\ok-ww.exe",
            @"C:\X\random.exe",
            @"C:\G\BetterGI",
            @"C:\W",
            @"C:\Empty",
        };
        var dirs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\G\BetterGI"] = new[] { @"C:\G\BetterGI\BetterGI.exe", @"C:\G\BetterGI\readme.txt" },
            [@"C:\W"] = new[] { @"C:\W\ok-ww.exe" },
            [@"C:\Empty"] = Array.Empty<string>(),
        };

        bool Exists(string p) => files.Contains(p);
        bool IsDir(string p) => dirs.ContainsKey(p);
        IEnumerable<string> ListDir(string p) => dirs.TryGetValue(p, out var v) ? v : Array.Empty<string>();

        (string ToolId, string Path)? Run(string? p) => DropAssign.Resolve(p, Exists, IsDir(p ?? ""), ListDir);

        // 1) 文件名 → 工具
        Check("文件名/BetterGI", DropAssign.ToolOf("BetterGI.exe") == "bettergi");
        Check("文件名/大小写不敏感", DropAssign.ToolOf("bettergi.EXE") == "bettergi");
        Check("文件名/三月七", DropAssign.ToolOf(@"C:\M\March7thAssistant.exe") == "march7th");
        Check("文件名/ok-ww", DropAssign.ToolOf("ok-ww.exe") == "ok-ww");
        Check("文件名/不认识返回 null", DropAssign.ToolOf("random.exe") == null);
        Check("文件名/空安全", DropAssign.ToolOf("") == null && DropAssign.ToolOf(null) == null);

        // 2) exe 直接拖进来
        var a = Run(@"C:\G\BetterGI\BetterGI.exe");
        Check("exe/认得出来", a?.ToolId == "bettergi");
        Check("exe/路径原样带回", a?.Path == @"C:\G\BetterGI\BetterGI.exe");
        Check("exe/带引号也能处理", Run("\"C:\\G\\BetterGI\\BetterGI.exe\"")?.ToolId == "bettergi");
        Check("exe/认不出就是 null", Run(@"C:\X\random.exe") == null);
        Check("exe/不存在就 null", Run(@"C:\Nope\BetterGI.exe") == null);
        Check("exe/空路径 null", Run("") == null && Run(null) == null);

        // 3) 目录拖进来：按目录名猜，再按里面的 exe 猜
        var d1 = Run(@"C:\G\BetterGI");
        Check("目录/按目录名认出", d1?.ToolId == "bettergi");
        Check("目录/路径补成里面的 exe", d1?.Path == @"C:\G\BetterGI\BetterGI.exe");

        var d2 = Run(@"C:\W");
        Check("目录/目录名不像工具名，靠里面的 exe", d2?.ToolId == "ok-ww");
        Check("目录/靠 exe 时路径也正确", d2?.Path == @"C:\W\ok-ww.exe");

        Check("目录/空的返回 null", Run(@"C:\Empty") == null);
        Check("目录/不存在的返回 null", Run(@"C:\NoSuchDir") == null);

        // 4) 拒绝语分三种，因为处理方式完全不同
        Check("拒绝/快捷方式", DropAssign.Refusal(@"C:\x\BetterGI.lnk").Contains("快捷方式"));
        Check("拒绝/认不出的 exe", DropAssign.Refusal(@"C:\x\foo.exe").Contains("不认识"));
        Check("拒绝/空目录", DropAssign.Refusal(@"C:\x\somedir").Contains("文件夹"));
        Check("拒绝/非 exe 文件", DropAssign.Refusal(@"C:\x\a.txt").Contains("只认 exe"));
        Check("拒绝/null 不抛", DropAssign.Refusal(null).Length > 0);

        // 5) 显示名
        Check("显示名/BetterGI", DropAssign.DisplayName("bettergi") == "BetterGI");
        Check("显示名/未知原样", DropAssign.DisplayName("foo") == "foo");

        // 6) 界面接线：AllowDrop 必须真的挂在窗口上，否则引导语是假的
        string xaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("界面/窗口开了 AllowDrop", xaml.Contains("AllowDrop=\"True\""));
        Check("界面/接了 DragOver", xaml.Contains("DragOver=\"OnWindowDragOver\""));
        Check("界面/接了 Drop", xaml.Contains("Drop=\"OnWindowDrop\""));

        string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("界面/认不出时给拒绝语", code.Contains("DropAssign.Refusal"));
        Check("界面/走统一的 ApplyToolPath", code.Contains("ApplyToolPath(toolId, target)"));

        // 7) 引导语说的事必须存在（v1.15 写过「拖进窗口」但没有实现）
        Check("引导语/三步里提到拖", code.Contains("拖进窗口"));

        Console.WriteLine($"RESULT drop fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.16：检查更新。这里一条网络都不发——只验「给的输入能不能得到该有的话」。
    /// 真联网的那条路留给手动点，探针不该依赖 GitHub 在线。
    /// </summary>
    private static int RunUpdate()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // 1) Parse：几种写法都要认
        Check("解析/v1.15", UpdateCheck.Parse("v1.15") == (1, 15, 0, false));
        Check("解析/1.15.0", UpdateCheck.Parse("1.15.0") == (1, 15, 0, false));
        Check("解析/大写 V", UpdateCheck.Parse("V1.15.0") == (1, 15, 0, false));
        Check("解析/预发布", UpdateCheck.Parse("v1.16.0-beta") == (1, 16, 0, true));
        Check("解析/只有主版本", UpdateCheck.Parse("2") == (2, 0, 0, false));
        Check("解析/多余后缀忽略", UpdateCheck.Parse("v1.15.0+build7") == (1, 15, 0, false));
        Check("解析/空返回 null", UpdateCheck.Parse("") == null && UpdateCheck.Parse(null) == null);
        Check("解析/乱写返回 null", UpdateCheck.Parse("abc") == null);

        // 2) Compare：字符串比较会把 1.9 判成比 1.10 新，这里必须按段比
        Check("比较/1.16 > 1.15", UpdateCheck.Compare("v1.16", "1.15.0") == 1);
        Check("比较/1.9 < 1.10", UpdateCheck.Compare("v1.9", "1.10.0") == -1);
        Check("比较/一样返回 0", UpdateCheck.Compare("v1.15", "1.15.0") == 0);
        Check("比较/本地更新返回 -1", UpdateCheck.Compare("v1.14", "1.15.0") == -1);
        Check("比较/预发布小于正式", UpdateCheck.Compare("v1.15.0-beta", "1.15.0") == -1);
        Check("比较/正式大于预发布", UpdateCheck.Compare("v1.15.0", "1.15.0-beta") == 1);
        Check("比较/patch 参与", UpdateCheck.Compare("v1.15.1", "1.15.0") == 1);
        Check("比较/认不出返回 null", UpdateCheck.Compare("abc", "1.15.0") == null);

        // 3) Describe：每种情况都要落到「所以我现在干什么」
        var noNet = UpdateCheck.Describe(null, "1.15.0", false, "连不上 GitHub");
        Check("话术/网络失败不报错", !noNet.Ok && !noNet.HasNewer);
        Check("话术/网络失败说清不影响用", noNet.Message.Contains("不影响使用"));

        var bad = UpdateCheck.Describe("abc", "1.15.0", true, null);
        Check("话术/版本号认不出就说认不出", bad.Message.Contains("不认得") && !bad.HasNewer);

        var newer = UpdateCheck.Describe("v1.16", "1.15.0", true, null);
        Check("话术/有新版标 HasNewer", newer.HasNewer);
        Check("话术/有新版带上两个版本号", newer.Message.Contains("v1.16") && newer.Message.Contains("1.15.0"));
        Check("话术/有新版给链接", newer.Url == UpdateCheck.ReleasesUrl);

        var same = UpdateCheck.Describe("v1.15", "1.15.0", true, null);
        Check("话术/最新版不骚扰", !same.HasNewer && same.Message.Contains("最新的"));

        var ahead = UpdateCheck.Describe("v1.14", "1.15.0", true, null);
        Check("话术/开发版要说明白", !ahead.HasNewer && ahead.Message.Contains("开发版"));

        // 4) 边界：这个功能不该替用户做任何事
        string src = ReadRepoSources("Dragon Butler/UpdateCheck.cs");
        Check("边界/不下载", !src.Contains("DownloadFile") && !src.Contains("WebClient"));
        Check("边界/不替换自身", !src.Contains("File.Replace") && !src.Contains("File.Copy"));
        Check("边界/不重启", !src.Contains("Restart") && !src.Contains("Start("));
        Check("边界/不发本机信息", src.Contains("Dragon-Butler-UpdateCheck"));
        Check("边界/URL 写死仓库", src.Contains("laopiaoke/Dragon-Butler"));

        // 5) 接线：命令面板那句「会自动提示」必须真的有代码在提示
        string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("接线/启动时查一次", code.Contains("StartBackgroundUpdateCheck()"));
        Check("接线/命令面板走真检查", code.Contains("CheckUpdateManualAsync()"));
        Check("接线/有新版本才落日志", code.Contains("发现新版本"));
        Check("接线/有新版挂在首屏", code.Contains("TbUpdateHint"));

        string xaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("界面/提示默认隐藏", xaml.Contains("x:Name=\"TbUpdateHint\"") && xaml.Contains("Visibility=\"Collapsed\""));
        Check("界面/点了开浏览器", xaml.Contains("MouseLeftButtonUp=\"TbUpdateHint_MouseLeftButtonUp\""));

        Console.WriteLine($"RESULT update fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.17：备份包的打包与解包。
    /// 这个功能真正危险的地方不在打包成功，而在解包——zip 里的路径是外部数据。
    /// 所以断言的重心是「不该落盘的一条都没落」。
    /// </summary>
    private static int RunBackup()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 白名单 ----
        Check("白名单/认得 settings", SettingsBackup.IsWhitelisted("settings.json"));
        Check("白名单/大小写不敏感", SettingsBackup.IsWhitelisted("Settings.JSON"));
        Check("白名单/认得 harvest", SettingsBackup.IsWhitelisted("harvest.json"));
        Check("白名单/拒日志", !SettingsBackup.IsWhitelisted("2026-11-16_运行.log"));
        Check("白名单/拒带目录", !SettingsBackup.IsWhitelisted("config/settings.json"));
        Check("白名单/拒反斜杠路径", !SettingsBackup.IsWhitelisted(@"config\settings.json"));
        Check("白名单/拒空与 null", !SettingsBackup.IsWhitelisted("") && !SettingsBackup.IsWhitelisted(null));
        Check("白名单/只有四个", SettingsBackup.Whitelist.Length == 4);

        // ---- 2) 解包路径校验（zip 里的名字一律当不可信输入） ----
        Check("路径/正常", SettingsBackup.SafeEntryPath("dragon-butler/settings.json") == "dragon-butler/settings.json");
        Check("路径/反斜杠归一", SettingsBackup.SafeEntryPath(@"dragon-butler\settings.json") == "dragon-butler/settings.json");
        Check("路径/拒 ..", SettingsBackup.SafeEntryPath("dragon-butler/../../../Windows/System32/a.dll") == null);
        Check("路径/拒裸 ..", SettingsBackup.SafeEntryPath("../evil.txt") == null);
        Check("路径/拒绝对路径", SettingsBackup.SafeEntryPath("/etc/passwd") == null);
        Check("路径/拒盘符", SettingsBackup.SafeEntryPath(@"C:\Windows\a.dll") == null);
        Check("路径/拒正斜杠盘符", SettingsBackup.SafeEntryPath("C:/Windows/a.dll") == null);
        Check("路径/拒双斜杠", SettingsBackup.SafeEntryPath("dragon-butler//settings.json") == null);
        Check("路径/拒 .", SettingsBackup.SafeEntryPath("dragon-butler/./settings.json") == null);
        Check("路径/拒空段", SettingsBackup.SafeEntryPath("") == null && SettingsBackup.SafeEntryPath(null) == null);

        // ---- 3) 落地位置二次核对 ----
        string root = Path.Combine(Path.GetTempPath(), "dbtest-root");
        Check("位置/里面", SettingsBackup.IsInside(root, Path.Combine(root, "settings.json")));
        Check("位置/子目录", SettingsBackup.IsInside(root, Path.Combine(root, "sub", "a.json")));
        Check("位置/外面", !SettingsBackup.IsInside(root, Path.Combine(Path.GetTempPath(), "elsewhere.json")));
        Check("位置/前缀但不同目录", !SettingsBackup.IsInside(root, root + "Other" + Path.DirectorySeparatorChar + "a.json"));
        Check("位置/空参数", !SettingsBackup.IsInside("", "a") && !SettingsBackup.IsInside(root, ""));

        // ---- 4) 收文件：只收真实存在的白名单文件 ----
        string work = Path.Combine(Path.GetTempPath(), "dbtest-backup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string cfg = Path.Combine(work, "config");
        string oneDragon = Path.Combine(work, "bgi", "User", "OneDragon");
        try
        {
            Directory.CreateDirectory(cfg);
            Directory.CreateDirectory(oneDragon);
            File.WriteAllText(Path.Combine(cfg, "settings.json"), "{\"a\":1}");
            File.WriteAllText(Path.Combine(cfg, "run_journal.json"), "{}");
            File.WriteAllText(Path.Combine(cfg, "nope.log"), "x");
            var collected = SettingsBackup.CollectFiles(cfg, File.Exists);
            Check("收文件/只收存在的", collected.Count == 2, string.Join(",", collected));
            Check("收文件/不收日志", !collected.Any(n => n.EndsWith(".log")));
            Check("收文件/顺序按白名单", collected[0] == "settings.json");
            Check("收文件/空目录返回空", SettingsBackup.CollectFiles(Path.Combine(work, "empty"), File.Exists).Count == 0);
            Check("收文件/null 安全", SettingsBackup.CollectFiles(null, File.Exists).Count == 0);

            // 一条龙配置：只收合法名
            File.WriteAllText(Path.Combine(oneDragon, "日常.json"), "{}");
            File.WriteAllText(Path.Combine(oneDragon, "坏:名字.json"), "{}");
            File.WriteAllText(Path.Combine(oneDragon, "readme.txt"), "x");
            var bgi = SettingsBackup.CollectBgiFiles(oneDragon, File.Exists,
                dir => Directory.GetFiles(dir));
            Check("收一条龙/只收合法 json", bgi.Count == 1, string.Join(",", bgi));
            Check("收一条龙/拒绝非法名", !bgi.Any(n => n.Contains(":")));

            // ---- 5) 打包 ----
            string zip = Path.Combine(work, "pack.zip");
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("settings.json", Path.Combine(cfg, "settings.json")),
                new("run_journal.json", Path.Combine(cfg, "run_journal.json")),
                new("settings.json.bak", Path.Combine(cfg, "nope.log")),   // 不存在的会被跳过
            };
            var write = SettingsBackup.WritePackage(zip, pairs, new DateTime(2026, 11, 16, 8, 30, 0));
            Check("打包/成功", write.Ok, write.Message);
            Check("打包/文件在", File.Exists(zip));
            Check("打包/没有残留临时文件", !File.Exists(zip + ".dbtmp"));

            var manifest = SettingsBackup.ReadManifest(zip, out string reason);
            Check("清单/读得出来", manifest != null, reason);
            Check("清单/版本是 1", manifest != null && manifest.Version == 1);
            Check("清单/记了时间", manifest != null && manifest.CreatedAt.StartsWith("2026-11-16"));
            Check("清单/记了两个文件", manifest != null && manifest.Files.Count == 2, manifest == null ? "" : string.Join(",", manifest.Files));
            Check("清单/不存在的文件没进包", manifest != null && !manifest.Files.Contains("settings.json.bak"));

            // ---- 6) 空包要如实说 ----
            string emptyZip = Path.Combine(work, "empty.zip");
            var emptyWrite = SettingsBackup.WritePackage(emptyZip,
                new List<KeyValuePair<string, string>>(), DateTime.Now);
            Check("打包/空包算失败", !emptyWrite.Ok && emptyWrite.Message.Contains("没有可备份"));
            Check("打包/空包不留文件", !File.Exists(emptyZip));

            // ---- 7) 解包：正常恢复 ----
            string dest = Path.Combine(work, "restore", "config");
            var ex = SettingsBackup.Extract(zip, dest, null, false);
            Check("解包/恢复了 2 个", ex.Restored == 2, ex.Message);
            Check("解包/settings 到位", File.Exists(Path.Combine(dest, "settings.json")));
            Check("解包/内容一致", File.ReadAllText(Path.Combine(dest, "settings.json")).Contains("\"a\":1"));
            Check("解包/没多出东西", Directory.GetFiles(dest).Length == 2);

            // ---- 8) 解包：坏包（自己造一个带 .. 的 zip） ----
            string evilZip = Path.Combine(work, "evil.zip");
            using (var fs = new FileStream(evilZip, FileMode.Create))
            using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                void Add(string name, string body)
                {
                    var e = z.CreateEntry(name);
                    using var s = e.Open();
                    using var w = new StreamWriter(s);
                    w.Write(body);
                }
                Add("dragon-butler/backup.json", "{\"Version\":1,\"Files\":[]}");
                Add("dragon-butler/../escape.txt", "evil");
                Add("dragon-butler/settings.json", "{\"evil\":true}");
            }
            string dest2 = Path.Combine(work, "restore2", "config");
            var ex2 = SettingsBackup.Extract(evilZip, dest2, null, false);
            Check("坏包/只恢复白名单一条", ex2.Restored == 1, ex2.Message);
            Check("坏包/越界那条被跳过", ex2.Skipped.Count >= 1);
            Check("坏包/没有写到上级目录", !File.Exists(Path.Combine(work, "restore2", "escape.txt")));

            // ---- 9) 不是我们的包：给得出人话 ----
            string alienZip = Path.Combine(work, "alien.zip");
            using (var fs = new FileStream(alienZip, FileMode.Create))
            using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("random.txt");
                using var s = e.Open();
                using var w = new StreamWriter(s);
                w.Write("hello");
            }
            var alien = SettingsBackup.ReadManifest(alienZip, out string alienReason);
            Check("别家包/null", alien == null);
            Check("别家包/说人话", alienReason.Contains("不是本软件"));

            // ---- 10) 恢复前的措辞 ----
            string prompt = SettingsBackup.RestorePrompt(manifest, zip);
            Check("措辞/说会覆盖", prompt.Contains("覆盖"));
            Check("措辞/说会先备份", prompt.Contains("备份"));
            Check("措辞/null 清单也能说", SettingsBackup.RestorePrompt(null, zip).Contains("覆盖"));

            // ---- 11) 默认名 ----
            string name = SettingsBackup.FileName(new DateTime(2026, 11, 16, 8, 30, 0));
            Check("命名/带时间", name == "一条龙管家-20261116-0830.zip", name);

            // ---- 12) 接线：界面三个按钮后面必须真有代码 ----
            string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
            Check("接线/存一份", code.Contains("private void BtnBackupNow_Click"));
            Check("接线/从包恢复", code.Contains("private void BtnBackupRestore_Click"));
            Check("接线/打开文件夹", code.Contains("private void BtnBackupOpen_Click"));
            Check("接线/恢复前先自备份", code.Contains("恢复前-"));
            Check("接线/自备份失败就不恢复", code.Contains("为了不丢东西这次没有恢复"));
            Check("接线/恢复后提示重开", code.Contains("重开程序"));

            string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
            Check("界面/三个按钮都在",
                mainXaml.Contains("Click=\"BtnBackupNow_Click\"")
                && mainXaml.Contains("Click=\"BtnBackupRestore_Click\"")
                && mainXaml.Contains("Click=\"BtnBackupOpen_Click\""));
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine($"RESULT backup fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    private static int RunPresets()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        int seq = 0;
        string NewId() => "id-" + (++seq);

        // ---- 名字清洗 ----
        Check("名字/去首尾空白", ConfigPresets.NormalizeName("  日常  ") == "日常");
        Check("名字/换行转空格", ConfigPresets.NormalizeName("大号\n日常") == "大号 日常");
        Check("名字/压连续空格", ConfigPresets.NormalizeName("大号   日常") == "大号 日常");
        Check("名字/空返回空", ConfigPresets.NormalizeName("") == "");
        Check("名字/null 返回空", ConfigPresets.NormalizeName(null) == "");
        Check("名字/超长截断",
            ConfigPresets.NormalizeName(new string('a', ConfigPresets.MaxNameChars + 10)).Length == ConfigPresets.MaxNameChars);

        // ---- 抓一套 ----
        var p1 = ConfigPresets.Capture(null, " 日常 ", new[] { "日常", "", "活动" }, "大号", NewId);
        Check("抓取/Id 非空", p1.Id.Length > 0);
        Check("抓取/名字清洗过", p1.Name == "大号");
        Check("抓取/配置名去空白", ConfigPresets.Capture(null, " 日常 ", null, "x", NewId).DragonConfigName == "日常");
        Check("抓取/空组名被丢掉", p1.Groups.Length == 2, string.Join("|", p1.Groups));
        Check("抓取/没给名字就自动编号", ConfigPresets.Capture(null, "日常", null, "", NewId).Name == "配置 1");
        Check("抓取/自动编号跳过已占用",
            ConfigPresets.Capture(new[] { new ConfigPreset { Name = "配置 1" } }, "日常", null, null, NewId).Name == "配置 2");
        Check("抓取/自动编号跳过大小写变体",
            ConfigPresets.Capture(new[] { new ConfigPreset { Name = "配置 1" }, new ConfigPreset { Name = "配置 2" }, new ConfigPreset { Name = "配置 3" } },
                "日常", null, null, NewId).Name == "配置 4");

        // ---- 校验 ----
        var existing = new List<ConfigPreset> { p1 };
        Check("校验/合法返回 null",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "小号", DragonConfigName = "日常" }, existing) == null);
        Check("校验/null 拒绝", ConfigPresets.RefuseReason(null, existing) != null);
        Check("校验/空名字拒绝",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "  ", DragonConfigName = "日常" }, existing)?.Contains("名字") == true);
        Check("校验/重名拒绝",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "大号", DragonConfigName = "日常" }, existing)?.Contains("已经有一套") == true);
        Check("校验/重名不看大小写",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "DAILY", DragonConfigName = "日常" },
                new[] { new ConfigPreset { Id = "y", Name = "daily" } })?.Contains("已经有一套") == true);
        Check("校验/改自己不算重名",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = p1.Id, Name = "大号", DragonConfigName = "日常" }, existing) == null);
        Check("校验/什么都没选拒绝",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "空壳" }, existing)?.Contains("什么都没选") == true);
        Check("校验/只有组也行",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "只跑组", Groups = new[] { "日常" } }, existing) == null);
        Check("校验/只有配置名也行",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "只跑配置", DragonConfigName = "日常" }, existing) == null);

        var many = Enumerable.Range(0, ConfigPresets.MaxCount)
            .Select(i => new ConfigPreset { Id = "m" + i, Name = "批量 " + i, DragonConfigName = "日常" }).ToList();
        Check("校验/到达上限后拒绝",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "new", Name = "再来一套", DragonConfigName = "日常" }, many)
                ?.Contains("最多存") == true);

        // ---- 查找 ----
        Check("查找/按 Id 找到", ConfigPresets.FindById(existing, p1.Id)?.Name == "大号");
        Check("查找/大小写不敏感", ConfigPresets.FindById(existing, p1.Id.ToUpperInvariant()) != null);
        Check("查找/空 Id 返回 null", ConfigPresets.FindById(existing, "") == null);
        Check("查找/没有返回 null", ConfigPresets.FindById(existing, "不存在") == null);
        Check("查找/null 集合安全", ConfigPresets.FindById(null, "x") == null);

        // ---- 排序：用过的在前，没用过的保持原序 ----
        var sortable = new List<ConfigPreset>
        {
            new() { Id = "a", Name = "A" },
            new() { Id = "b", Name = "B", LastUsedAt = "2026-11-15 09:00:00" },
            new() { Id = "c", Name = "C", LastUsedAt = "2026-11-15 11:00:00" },
            new() { Id = "d", Name = "D" },
        };
        var sorted = ConfigPresets.Sort(sortable);
        Check("排序/最近的在前", sorted[0].Id == "c" && sorted[1].Id == "b", string.Join(",", sorted.Select(x => x.Id)));
        Check("排序/没用过的保持原序且在后", sorted[2].Id == "a" && sorted[3].Id == "d");
        Check("排序/总数不变", sorted.Count == sortable.Count);
        Check("排序/空集合安全", ConfigPresets.Sort(null).Count == 0);
        Check("排序/坏时间当没用过处理",
            ConfigPresets.Sort(new[] { new ConfigPreset { Id = "z", Name = "Z", LastUsedAt = "不是时间" } }).Count == 1);

        // ---- 失效组名：只报告不修改 ----
        var stale = new ConfigPreset { Id = "s", Name = "S", Groups = new[] { "日常", "已删除" } };
        var missing = ConfigPresets.MissingGroups(stale, new[] { "日常", "活动" });
        Check("失效组/找得到", missing.Count == 1 && missing[0] == "已删除", string.Join("|", missing));
        Check("失效组/全都在就返回空", ConfigPresets.MissingGroups(stale, new[] { "日常", "已删除" }).Count == 0);
        Check("失效组/没识别到就不判定", ConfigPresets.MissingGroups(stale, Array.Empty<string>()).Count == 0);
        Check("失效组/null 组列表安全", ConfigPresets.MissingGroups(null, new[] { "日常" }).Count == 0);
        Check("失效组/不修改原对象", stale.Groups.Length == 2);
        Check("失效组/大小写不敏感", ConfigPresets.MissingGroups(
            new ConfigPreset { Id = "t", Name = "T", Groups = new[] { "Daily" } }, new[] { "daily" }).Count == 0);

        // ---- 展开 ----
        var (cfg, grps) = ConfigPresets.Expand(p1);
        Check("展开/配置名", cfg == "日常", cfg);
        Check("展开/组名", grps.Count == 2);
        Check("展开/null 预设返回空", ConfigPresets.Expand(null).DragonConfigName == "");
        Check("展开/null 预设组为空", ConfigPresets.Expand(null).Groups.Count == 0);

        // ---- 描述 ----
        Check("描述/配置名加组数", ConfigPresets.Describe(p1) == "日常 · 2 组", ConfigPresets.Describe(p1));
        Check("描述/没有组说默认组",
            ConfigPresets.Describe(new ConfigPreset { Name = "x", DragonConfigName = "日常" }) == "日常 · 默认组");
        Check("描述/只有组没配置名",
            ConfigPresets.Describe(new ConfigPreset { Name = "x", Groups = new[] { "日常" } }) == "1 组");
        Check("描述/null 返回空", ConfigPresets.Describe(null) == "");

        // ---- 使用时间戳 ----
        var stamp = ConfigPresets.StampUsed(new DateTime(2026, 11, 15, 9, 30, 5));
        Check("时间戳/格式", stamp == "2026-11-15 09:30:05", stamp);
        Check("时间戳/能被解析回来", DateTime.TryParse(stamp, out _));

        // ---- Clone 不共享数组 ----
        var clone = p1.Clone();
        clone.Groups[0] = "改过";
        Check("克隆/组数组不共享", p1.Groups[0] == "日常", p1.Groups[0]);
        Check("克隆/Id 一致", clone.Id == p1.Id);

        Console.WriteLine($"RESULT presets fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.15 / M1：写 BetterGI 一条龙配置。
    /// 这是全软件唯一一处会改别人文件的代码，所以每一条拒绝理由都要单独试一遍：
    /// 光测「能写成功」是不够的——真正会出事的是「本不该写的时候写了」。
    /// </summary>
    private static int RunBgiWrite()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        string root = Path.Combine(Path.GetTempPath(), "dragon-bgiwrite");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        string oneDragon = Path.Combine(root, BgiConfigWriter.SubDir);
        Directory.CreateDirectory(oneDragon);
        string cfg = Path.Combine(oneDragon, "日常.json");
        File.WriteAllText(cfg, "{\n  \"name\": \"日常\",\n  \"tasks\": [],\n  \"settings\": { \"completionAction\": \"None\", \"partyName\": \"旅行者\" }\n}");

        // ---- 名字安全：挡的是「借名字把写入引到别处」 ----
        Check("名字/正常中文可以", BgiConfigWriter.IsSafeName("日常"));
        Check("名字/英文数字可以", BgiConfigWriter.IsSafeName("day-run_2"));
        Check("名字/空不行", !BgiConfigWriter.IsSafeName(""));
        Check("名字/空白不行", !BgiConfigWriter.IsSafeName("   "));
        Check("名字/null 不行", !BgiConfigWriter.IsSafeName(null));
        Check("名字/上级目录不行", !BgiConfigWriter.IsSafeName(".."));
        Check("名字/单点不行", !BgiConfigWriter.IsSafeName("."));
        Check("名字/带反斜杠不行", !BgiConfigWriter.IsSafeName(@"..\evil"));
        Check("名字/带正斜杠不行", !BgiConfigWriter.IsSafeName("a/b"));
        Check("名字/带冒号不行", !BgiConfigWriter.IsSafeName("C:日常"));
        Check("名字/首尾有空格不行", !BgiConfigWriter.IsSafeName(" 日常"));
        Check("名字/末尾有点不行", !BgiConfigWriter.IsSafeName("日常."));
        Check("名字/控制字符不行", !BgiConfigWriter.IsSafeName("日\n常"));
        Check("名字/超长不行", !BgiConfigWriter.IsSafeName(new string('a', BgiConfigWriter.MaxNameChars + 1)));
        Check("名字/刚好到上限可以", BgiConfigWriter.IsSafeName(new string('a', BgiConfigWriter.MaxNameChars)));

        // ---- 路径拼接 + 二次核对 ----
        string built = BgiConfigWriter.ConfigPath(root, "日常");
        Check("路径/拼接正确", built == cfg, built);
        Check("路径/名字不合法返回空", BgiConfigWriter.ConfigPath(root, "../x") == "");
        Check("路径/根为空返回空", BgiConfigWriter.ConfigPath("", "日常") == "");
        Check("路径/认得出在一条龙目录里", BgiConfigWriter.IsUnderOneDragon(built, root));
        Check("路径/别处的文件不认", !BgiConfigWriter.IsUnderOneDragon(Path.Combine(root, "其他.json"), root));
        Check("路径/子目录里也不认",
            !BgiConfigWriter.IsUnderOneDragon(Path.Combine(oneDragon, "sub", "x.json"), root));
        Check("路径/非 json 不认", !BgiConfigWriter.IsUnderOneDragon(Path.ChangeExtension(cfg, ".txt"), root));

        // ---- 拒绝写入的六种情况 ----
        var now = DateTime.Now;
        Check("拒绝/没填根目录",
            BgiConfigWriter.RefuseReason(cfg, "", false, true, null, null)?.Contains("路径") == true);
        Check("拒绝/名字拼接失败",
            BgiConfigWriter.RefuseReason("", root, false, true, null, null) != null);
        Check("拒绝/不在一条龙目录",
            BgiConfigWriter.RefuseReason(Path.Combine(root, "x.json"), root, false, true, null, null)
                ?.Contains("没敢写") == true);
        Check("拒绝/BGI 正在跑",
            BgiConfigWriter.RefuseReason(cfg, root, true, true, null, null)?.Contains("先关掉再改") == true);
        Check("拒绝/文件不见了",
            BgiConfigWriter.RefuseReason(cfg, root, false, false, null, null)?.Contains("已经不在了") == true);
        Check("拒绝/mtime 被动过",
            BgiConfigWriter.RefuseReason(cfg, root, false, true, now, now.AddMinutes(3))
                ?.Contains("被别的程序改过") == true);
        Check("拒绝/mtime 退回（也算被动过）",
            BgiConfigWriter.RefuseReason(cfg, root, false, true, now, now.AddMinutes(-3)) != null);
        Check("放行/一切正常", BgiConfigWriter.RefuseReason(cfg, root, false, true, now, now) == null);
        Check("放行/mtime 在 2 秒容差内",
            BgiConfigWriter.RefuseReason(cfg, root, false, true, now, now.AddSeconds(1)) == null);
        Check("放行/没给 mtime 就不比", BgiConfigWriter.RefuseReason(cfg, root, false, true, null, null) == null);

        // ---- 只改认得的那个键，别的一个都不碰 ----
        string json = File.ReadAllText(cfg);
        string? edited = BgiConfigWriter.SetString(json, BgiConfigWriter.KeyCompletionAction, "Shutdown");
        Check("改值/返回了新内容", edited != null);
        Check("改值/目标是新值", BgiConfigWriter.GetString(edited, BgiConfigWriter.KeyCompletionAction) == "Shutdown");
        Check("改值/同层别的字段没动", BgiConfigWriter.GetString(edited, "settings.partyName") == "旅行者");
        Check("改值/顶层字段没动", (edited ?? "").Contains("\"name\": \"日常\"", StringComparison.Ordinal));
        Check("改值/任务列表还在", (edited ?? "").Contains("\"tasks\": []", StringComparison.Ordinal));

        Check("改值/坏 JSON 返回 null", BgiConfigWriter.SetString("{ 不是 json", BgiConfigWriter.KeyCompletionAction, "x") == null);
        Check("改值/顶层不是对象返回 null", BgiConfigWriter.SetString("[1,2]", BgiConfigWriter.KeyCompletionAction, "x") == null);
        Check("改值/中间少一层返回 null", BgiConfigWriter.SetString("{\"name\":\"a\"}", BgiConfigWriter.KeyCompletionAction, "x") == null);
        Check("改值/空 JSON 返回 null", BgiConfigWriter.SetString("", BgiConfigWriter.KeyCompletionAction, "x") == null);
        Check("读值/读得到", BgiConfigWriter.GetString(json, BgiConfigWriter.KeyCompletionAction) == "None");
        Check("读值/键不存在返回 null", BgiConfigWriter.GetString(json, "settings.不存在") == null);
        Check("读值/类型不是字符串返回 null", BgiConfigWriter.GetString("{\"settings\":{\"x\":1}}", "settings.x") == null);

        // ---- 真正的写入：成功路径 ----
        string baseDir = Path.Combine(root, "app");
        Directory.CreateDirectory(baseDir);
        var (ok1, msg1) = BgiConfigWriter.Save(baseDir, cfg, edited!, new DateTime(2026, 11, 15, 9, 30, 0));
        Check("保存/成功", ok1, msg1);
        Check("保存/内容真的变了",
            BgiConfigWriter.GetString(File.ReadAllText(cfg), BgiConfigWriter.KeyCompletionAction) == "Shutdown");
        string bakDir = BgiConfigWriter.BackupDir(baseDir, new DateTime(2026, 11, 15, 9, 30, 0));
        Check("保存/备份目录建了", Directory.Exists(bakDir), bakDir);
        Check("保存/备份里有原件",
            Directory.Exists(bakDir) && Directory.GetFiles(bakDir, "*.json").Length == 1);
        if (Directory.Exists(bakDir))
        {
            string bak = Directory.GetFiles(bakDir, "*.json")[0];
            Check("保存/备份是改之前的内容",
                BgiConfigWriter.GetString(File.ReadAllText(bak), BgiConfigWriter.KeyCompletionAction) == "None");
        }
        Check("保存/没有留下临时文件", !File.Exists(cfg + ".dbtmp"));
        Check("保存/提示里有备份路径", msg1.Contains(bakDir, StringComparison.OrdinalIgnoreCase));

        // ---- 失败路径：文件不在 ----
        var (ok2, _) = BgiConfigWriter.Save(baseDir, Path.Combine(oneDragon, "不存在.json"), "{}", now);
        Check("保存/文件不在就失败", !ok2);

        // ---- 失败路径：备份目录建不出来 ----
        // 占位文件必须落在 config\ 下面才有用：代码拼的是 {baseDir}\config\bgi-backup，
        // 把文件放在 {baseDir}\bgi-backup 挡不住任何东西（我第一版就是这么写错的）。
        string blockedBase = Path.Combine(root, "blocked");
        Directory.CreateDirectory(Path.Combine(blockedBase, "config"));
        File.WriteAllText(Path.Combine(blockedBase, "config", "bgi-backup"), "占位");
        var (ok3, msg3) = BgiConfigWriter.Save(blockedBase, cfg, "{\"settings\":{\"completionAction\":\"None\"}}", now);
        Check("保存/备份失败就不写", !ok3, msg3);
        Check("保存/备份失败后原件没被改",
            BgiConfigWriter.GetString(File.ReadAllText(cfg), BgiConfigWriter.KeyCompletionAction) == "Shutdown");

        // ---- 备份文件名：同一秒里两次备份不会互相覆盖 ----
        string t1 = BgiConfigWriter.BackupFile(bakDir, "日常", new DateTime(2026, 11, 15, 9, 30, 0, 100));
        string t2 = BgiConfigWriter.BackupFile(bakDir, "日常", new DateTime(2026, 11, 15, 9, 30, 0, 700));
        Check("备份名/同一秒内不重名", t1 != t2);
        Check("备份名/保留原名", t1.Contains("日常", StringComparison.Ordinal));
        Check("备份名/名字不合法时兜底", BgiConfigWriter.BackupFile(bakDir, "../x", now).Contains("config", StringComparison.Ordinal));

        // ---- 清理 ----
        try { Directory.Delete(root, true); } catch { }

        Console.WriteLine($"RESULT bgiwrite fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.17 / M1-B：一条龙「跑完后动作」。
    /// 最要紧的三件事：候选只能来自用户自己的配置（不硬编码枚举）、
    /// `Sleep` 与 `sleep` 是两个值不能合并、写入前必须过那四道拒写闸。
    /// </summary>
    private static int RunCompletion()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 候选值清洗 ----
        Check("清洗/去首尾空白", DragonCompletion.CleanValue("  Shutdown  ") == "Shutdown");
        Check("清洗/换行转掉", DragonCompletion.CleanValue("Sleep\n") == "Sleep");
        Check("清洗/中间控制字符去掉", !DragonCompletion.CleanValue("Sle\tep").Contains('\t'));
        Check("清洗/空返回空", DragonCompletion.CleanValue("") == "");
        Check("清洗/全空白返回空", DragonCompletion.CleanValue("   ") == "");
        Check("清洗/null 返回空", DragonCompletion.CleanValue(null) == "");
        Check("清洗/超长截断", DragonCompletion.CleanValue(new string('a', 200)).Length == DragonCompletion.MaxValueChars);

        // ---- 目标文件：留空就不改 ----
        string dir = Path.Combine(Path.GetTempPath(), "dragon-completion");
        Check("目标/正常拼出", DragonCompletion.TargetJson(dir, "日常") == Path.Combine(dir, "日常.json"));
        Check("目标/名字留空返回 null", DragonCompletion.TargetJson(dir, "") == null);
        Check("目标/名字不安全返回 null", DragonCompletion.TargetJson(dir, "../x") == null);
        Check("目标/目录为空返回 null", DragonCompletion.TargetJson("", "日常") == null);
        Check("目标/名字 null 返回 null", DragonCompletion.TargetJson(dir, null) == null);

        // ---- 扫描：候选全部来自文件，且大小写不合并 ----
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(dir, "日常.json")] =
                "{\"name\":\"日常\",\"settings\":{\"completionAction\":\"Shutdown\"}}",
            [Path.Combine(dir, "深渊.json")] =
                "{\"name\":\"深渊\",\"settings\":{\"completionAction\":\"Shutdown\"}}",
            [Path.Combine(dir, "挖矿.json")] =
                "{\"name\":\"挖矿\",\"settings\":{\"completionAction\":\"Sleep\"}}",
            [Path.Combine(dir, "小写.json")] =
                "{\"name\":\"小写\",\"settings\":{\"completionAction\":\"sleep\"}}",
            [Path.Combine(dir, "没设.json")] =
                "{\"name\":\"没设\",\"settings\":{}}",
            [Path.Combine(dir, "坏了.json")] = "{ 这不是 json",
            [Path.Combine(dir, "说明.txt")] = "ignore",
        };
        string? ReadFile(string p) => files.TryGetValue(p, out var t) ? t : null;
        IEnumerable<string> ListAll(string _) => files.Keys;

        var options = DragonCompletion.Scan(dir, ReadFile, ListAll);
        var values = options.Select(o => o.Value).ToList();
        Check("扫描/只收出现过的值", values.Count == 3, string.Join(",", values));
        Check("扫描/含 Shutdown", values.Contains("Shutdown"));
        Check("扫描/含 Sleep", values.Contains("Sleep"));
        Check("扫描/大写和小写是两个值", values.Contains("sleep") && values.Count(v => v.Equals("sleep", StringComparison.OrdinalIgnoreCase)) == 2);
        Check("扫描/坏了的那份不参与", !options.Any(o => o.UsedBy.Contains("坏了")));
        Check("扫描/没设值的不参与", !options.Any(o => o.UsedBy.Contains("没设")));
        Check("扫描/不收非 json", !options.Any(o => o.UsedBy.Contains("说明")));
        var shutdown = options.First(o => o.Value == "Shutdown");
        Check("扫描/记录哪些配置在用", shutdown.UsedBy.Count == 2, string.Join(",", shutdown.UsedBy));
        Check("扫描/UsedBy 不重复", options.All(o => o.UsedBy.Distinct(StringComparer.OrdinalIgnoreCase).Count() == o.UsedBy.Count));
        Check("扫描/按值排序", values.SequenceEqual(options.Select(o => o.Value).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ThenBy(v => v, StringComparer.Ordinal)));
        Check("扫描/目录空返回空", DragonCompletion.Scan("", ReadFile, ListAll).Count == 0);
        Check("扫描/目录不存在不抛", DragonCompletion.Scan(Path.Combine(dir, "没有"), ReadFile, p => throw new IOException()).Count == 0);
        Check("扫描/读文件抛异常跳过", DragonCompletion.Scan(dir, _ => throw new IOException(), ListAll).Count == 0);

        // ---- 读当前值 ----
        Check("当前值/读得到", DragonCompletion.CurrentValue(files[Path.Combine(dir, "日常.json")]) == "Shutdown");
        Check("当前值/没设返回空", DragonCompletion.CurrentValue(files[Path.Combine(dir, "没设.json")]) == "");
        Check("当前值/坏 JSON 返回空", DragonCompletion.CurrentValue("{ 坏") == "");

        // ---- 比较：区分大小写 ----
        Check("比较/相同为真", DragonCompletion.IsSameValue("Sleep", "Sleep"));
        Check("比较/大小写不同为假", !DragonCompletion.IsSameValue("Sleep", "sleep"));
        Check("比较/两个空为真", DragonCompletion.IsSameValue("", ""));
        Check("比较/null 与空等价", DragonCompletion.IsSameValue(null, ""));

        // ---- 改值：只动那一个键 ----
        string src = files[Path.Combine(dir, "日常.json")];
        string? applied = DragonCompletion.Apply(src, "Sleep");
        Check("改值/返回新内容", applied != null);
        Check("改值/目标变了", DragonCompletion.CurrentValue(applied!) == "Sleep");
        // 注意：SetString 走 WriteIndented，写回去是 "name": "日常"（冒号后有空格），
        // 所以这里按读回来的值判断，不去匹配格式化后的字面量。
        Check("改值/别的字段没动", BgiConfigWriter.GetString(applied, "name") == "日常",
            BgiConfigWriter.GetString(applied, "name") ?? "null");
        Check("改值/中文没被转义", (applied ?? "").Contains("日常", StringComparison.Ordinal));
        Check("改值/空值返回 null", DragonCompletion.Apply(src, "") == null);
        Check("改值/结构不认识返回 null", DragonCompletion.Apply("{\"name\":\"x\"}", "Sleep") == null);
        Check("改值/坏 JSON 返回 null", DragonCompletion.Apply("{ 坏", "Sleep") == null);
        Check("改值/值会先清洗", DragonCompletion.CurrentValue(DragonCompletion.Apply(src, "  Sleep  ") ?? "") == "Sleep");

        // ---- 下拉文案：呈现事实，不编中文含义 ----
        Check("文案/带用例", DragonCompletion.OptionText(shutdown).Contains("Shutdown") && DragonCompletion.OptionText(shutdown).Contains("日常"));
        Check("文案/没用例就只给值", DragonCompletion.OptionText(new CompletionOption { Value = "X" }) == "X");
        Check("文案/null 返回空", DragonCompletion.OptionText(null) == "");

        // ---- 确认弹窗必须说清三件事 ----
        string confirm = DragonCompletion.ConfirmText("日常", "Shutdown", "Sleep");
        Check("确认/含文件名", confirm.Contains("日常.json", StringComparison.Ordinal));
        Check("确认/含从什么到什么", confirm.Contains("Shutdown", StringComparison.Ordinal) && confirm.Contains("Sleep", StringComparison.Ordinal));
        Check("确认/提到会备份", confirm.Contains("bgi-backup", StringComparison.Ordinal));
        Check("确认/说明含义由 BGI 决定", confirm.Contains("BetterGI", StringComparison.Ordinal));
        Check("确认/原值为空时说不空转", DragonCompletion.ConfirmText("日常", "", "Sleep").Contains("现在是空的", StringComparison.Ordinal));

        // ---- 接线：界面里真的接上了 ----
        string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        string mainCs = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("接线/下拉在界面上", mainXaml.Contains("\"CbCompletion\"", StringComparison.Ordinal));
        Check("接线/有提示行", mainXaml.Contains("\"TbCompletionHint\"", StringComparison.Ordinal));
        Check("接线/选了会处理", mainXaml.Contains("CbCompletion_SelectionChanged", StringComparison.Ordinal));
        Check("接线/写入前过拒写闸", mainCs.Contains("BgiConfigWriter.RefuseReason(target", StringComparison.Ordinal));
        Check("接线/写入走 Save（带备份）", mainCs.Contains("BgiConfigWriter.Save(AppContext.BaseDirectory, target", StringComparison.Ordinal));
        Check("接线/不忽略大小写选值", mainCs.Contains("SelectCompletionByValue", StringComparison.Ordinal));
        Check("接线/有节流", mainCs.Contains("_completionRefreshedAt", StringComparison.Ordinal));

        Console.WriteLine($"RESULT completion fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.19：开跑前体检。
    /// 最要紧的三条不肯让步：只有真挡路的才 Block（其余最多 Warn/Notice）、
    /// 重的必须排在前面（用户先看到「跑不了」而不是「没开定时」）、
    /// 同一份输入必须给同一份输出（纯函数，不看时钟不碰磁盘）。
    /// </summary>
    private static int RunPreflight()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // 一切正常的那份输入，后面各用例只改一处
        PreflightInput Healthy() => new(
            BgiConfigured: true,
            BgiExists: true,
            BgiRunning: false,
            EnabledTools: 2,
            ReadyTools: 2,
            GroupCount: 3,
            DragonConfigName: "日常",
            DragonConfigs: new[] { "日常", "深渊" },
            NotifyMode: "Toast",
            NotifyEndpoint: "",
            MissingPresetGroups: Array.Empty<string>(),
            PresetMissing: false,
            LastRunAt: "昨天 23:10",
            LastRunOk: true,
            LastRunReason: null,
            ScheduleEnabled: true,
            Writable: true);

        PreflightItem Of(PreflightReport r, string key) => r.Items.FirstOrDefault(i => i.Key == key)!;

        // ---- 1) 健康输入：能跑、没有 Block/Warn ----
        var ok = Preflight.Build(Healthy());
        Check("健康/能跑", ok.CanRun);
        Check("健康/没有 Block", ok.Worst != PreflightLevel.Block);
        Check("健康/没有 Warn", ok.Worst < PreflightLevel.Warn, ok.Worst.ToString());
        Check("健康/标题说可以开跑", ok.Headline.Contains("可以开跑"), ok.Headline);
        Check("健康/十项都在", ok.Items.Count == 10, ok.Items.Count.ToString());

        // ---- 2) 每条 Block 都能挡住 ----
        var noBgi = Preflight.Build(Healthy() with { BgiConfigured = false, BgiExists = false });
        Check("缺路径/挡路", !noBgi.CanRun);
        Check("缺路径/挂到 bgi.missing", noBgi.Items.Any(i => i.Key == "bgi.missing" && i.Level == PreflightLevel.Block));
        Check("缺路径/有修法", Of(noBgi, "bgi.missing").FixCommand == "pick.bettergi");
        Check("缺路径/标题说差几件", noBgi.Headline.Contains("件才能开跑"), noBgi.Headline);

        var gone = Preflight.Build(Healthy() with { BgiExists = false });
        Check("路径失效/挡路", !gone.CanRun);
        Check("路径失效/文案提到失效", Of(gone, "bgi.gone").Title.Contains("失效"), Of(gone, "bgi.gone").Title);

        var noTool = Preflight.Build(Healthy() with { EnabledTools = 0, ReadyTools = 0 });
        Check("没勾工具/挡路", !noTool.CanRun);
        Check("没勾工具/修法去工具卡", Of(noTool, "tools.none").FixCommand == "view.tools");

        var notReady = Preflight.Build(Healthy() with { EnabledTools = 2, ReadyTools = 0 });
        Check("工具全没路径/挡路", !notReady.CanRun);
        Check("工具全没路径/挂到 notready", notReady.Items.Any(i => i.Key == "tools.notready"));

        var ro = Preflight.Build(Healthy() with { Writable = false });
        Check("目录只读/挡路", !ro.CanRun);
        Check("目录只读/没有一键修法", Of(ro, "disk.readonly").FixText.Length == 0);

        // ---- 3) 该只提示的绝不能挡路（半夜弹窗拦人比不检查更烦） ----
        var noGroup = Preflight.Build(Healthy() with { GroupCount = 0 });
        Check("没配置组/不挡路", noGroup.CanRun);
        Check("没配置组/是 Warn", Of(noGroup, "groups.none").Level == PreflightLevel.Warn);
        Check("没配置组/没有修法", Of(noGroup, "groups.none").FixText.Length == 0);
        Check("没配置组/标题说可以跑", noGroup.Headline.Contains("可以跑"), noGroup.Headline);

        var noSchedule = Preflight.Build(Healthy() with { ScheduleEnabled = false });
        Check("没开定时/不挡路", noSchedule.CanRun);
        Check("没开定时/只算 Notice", Of(noSchedule, "schedule.off").Level == PreflightLevel.Notice);
        Check("没开定时/不抬高 Worst", noSchedule.Worst == PreflightLevel.Notice, noSchedule.Worst.ToString());

        var noNotify = Preflight.Build(Healthy() with { NotifyMode = "Feishu", NotifyEndpoint = "" });
        Check("远程没填地址/不挡路", noNotify.CanRun);
        Check("远程没填地址/是 Warn", Of(noNotify, "notify.none").Level == PreflightLevel.Warn);
        Check("远程没填地址/修法去通知", Of(noNotify, "notify.none").FixCommand == "view.notify");

        // ---- 4) 边界：留空的一条龙配置名是合法的，不该报错 ----
        var emptyDragon = Preflight.Build(Healthy() with { DragonConfigName = "" });
        Check("配置名留空/不报警", Of(emptyDragon, "dragon.empty").Level == PreflightLevel.Ok);
        Check("配置名留空/能跑", emptyDragon.CanRun);

        var missingDragon = Preflight.Build(Healthy() with { DragonConfigName = "不存在的" });
        Check("配置名找不到/是 Warn", Of(missingDragon, "dragon.missing").Level == PreflightLevel.Warn);
        Check("配置名找不到/能跑", missingDragon.CanRun);
        Check("配置名找不到/文案带名字", Of(missingDragon, "dragon.missing").Title.Contains("不存在的"));

        var caseDragon = Preflight.Build(Healthy() with { DragonConfigName = "日常" });
        Check("配置名/大小写不敏", Of(caseDragon, "dragon.ok").Level == PreflightLevel.Ok);

        // ---- 5) 组合 ----
        var presetGone = Preflight.Build(Healthy() with { PresetMissing = true });
        Check("组合没了/是 Warn", Of(presetGone, "preset.missing").Level == PreflightLevel.Warn);
        Check("组合没了/能跑", presetGone.CanRun);
        Check("组合没了/修法去组合", Of(presetGone, "preset.missing").FixCommand == "view.presets");

        var presetBadGroup = Preflight.Build(Healthy() with { MissingPresetGroups = new[] { "活动", "深渊" } });
        Check("组合有失效组/是 Warn", Of(presetBadGroup, "preset.groups").Level == PreflightLevel.Warn);
        Check("组合有失效组/列出来", Of(presetBadGroup, "preset.groups").Detail.Contains("活动"));
        var manyMissing = Preflight.Build(Healthy() with
        {
            MissingPresetGroups = new[] { "a", "b", "c", "d", "e", "f", "g" },
        });
        Check("组合失效组/最多列 5 个", !Of(manyMissing, "preset.groups").Detail.Contains("f、"));

        // ---- 6) 排序：重的在前面 ----
        var messy = Preflight.Build(Healthy() with
        {
            BgiConfigured = false,
            BgiExists = false,
            EnabledTools = 0,
            ReadyTools = 0,
            ScheduleEnabled = false,
            GroupCount = 0,
        });
        Check("排序/第一条是 Block", messy.Items[0].Level == PreflightLevel.Block, messy.Items[0].Key);
        Check("排序/最后一条不是 Block", messy.Items[^1].Level != PreflightLevel.Block, messy.Items[^1].Key);
        Check("排序/Rank 单调不增（重的在前）",
            messy.Items.Select((it, idx) => idx == 0 || Preflight.Rank(messy.Items[idx - 1].Level) >= Preflight.Rank(it.Level)).All(x => x));
        Check("排序/Block 都排在 Notice 前",
            messy.Items.Select(i => i.Level).ToList().IndexOf(PreflightLevel.Block)
            < messy.Items.Select(i => i.Level).ToList().LastIndexOf(PreflightLevel.Notice));

        Check("Rank/Block 最大", Preflight.Rank(PreflightLevel.Block) == 3);
        Check("Rank/Ok 最小", Preflight.Rank(PreflightLevel.Ok) == 0);

        // ---- 7) 通知渠道判定 ----
        Check("通知/本地渠道算配好", Preflight.IsLocalChannel("Toast") && Preflight.IsLocalChannel(""));
        Check("通知/远程渠道不算本地", !Preflight.IsLocalChannel("Feishu"));
        Check("通知/本地永远 ready", Preflight.NotifyReady("Toast", ""));
        Check("通知/远程空地址不 ready", !Preflight.NotifyReady("Feishu", ""));
        Check("通知/远程有地址 ready", Preflight.NotifyReady("Feishu", "https://x"));
        Check("通知/两段都要填", !Preflight.NotifyReady("Telegram", "token|") && !Preflight.NotifyReady("Telegram", "|chat"));
        Check("通知/两段都填了 ready", Preflight.NotifyReady("Telegram", "token|chat"));

        var s = new AppSettings
        {
            NotifyMode = "Telegram",
            NotifyTelegramToken = "tok",
            NotifyTelegramChat = "",
        };
        Check("通知/端点拼 token|chat", Preflight.NotifyEndpointOf("Telegram", s) == "tok|");
        Check("通知/邮件拼 host|to", Preflight.NotifyEndpointOf("Email", new AppSettings { NotifyEmailHost = "h", NotifyEmailTo = "t" }) == "h|t");
        Check("通知/null 设置返回空", Preflight.NotifyEndpointOf("Feishu", null) == "");
        Check("通知/未知渠道返回空", Preflight.NotifyEndpointOf("Whatever", s) == "");

        // ---- 8) 纯函数：同一输入两次结果一样 ----
        var a1 = Preflight.Build(Healthy());
        var a2 = Preflight.Build(Healthy());
        Check("纯函数/两次一致", a1.Headline == a2.Headline && a1.Items.Count == a2.Items.Count);

        // ---- 9) 接线：体检真的挂在开跑前 ----
        string mainCs = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("接线/开跑前调体检", mainCs.Contains("var report = SafePreflight();", StringComparison.Ordinal));
        Check("接线/只有 CanRun 为假才拦", mainCs.Contains("if (report != null && !report.CanRun)", StringComparison.Ordinal));
        Check("接线/拦截后开体检窗", mainCs.Contains("OpenPreflight()", StringComparison.Ordinal));
        Check("接线/有取事实的方法", mainCs.Contains("CollectPreflight()", StringComparison.Ordinal));
        Check("接线/出错不抛", mainCs.Contains("private static PreflightReport? SafePreflight()", StringComparison.Ordinal));
        Check("接线/系统卡有入口", mainXaml.Contains("x:Name=\"BtnPreflight\"", StringComparison.Ordinal));
        Check("接线/入口挂到点击", mainXaml.Contains("Click=\"BtnPreflight_Click\"", StringComparison.Ordinal));
        Check("接线/命令面板能搜到", mainCs.Contains("case \"view.preflight\": OpenPreflight(); break;", StringComparison.Ordinal));
        Check("接线/命令已注册", CommandRegistry.All.Any(c => c.Id == "view.preflight"));
        Check("接线/体检窗只读（不含 Save）",
            !ReadRepoSources("Dragon Butler/PreflightWindow.xaml.cs").Contains("SaveSettings", StringComparison.Ordinal));

        Console.WriteLine($"RESULT preflight fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #1：复盘。
    ///
    /// 这一段要钉住的不是「能拼出句子」，而是三件不肯让步的事：
    /// ① 不编——没有数据就不出那一条，绝不拿「一切正常」凑数；
    /// ② 顺序——Lead 永远第一，Gap 永远最后，中间最多一条 Detail；
    /// ③ 不报流水账——四行封顶。
    /// </summary>
    private static int RunRecap()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var start = new DateTime(2026, 11, 17, 23, 4, 0);

        // 一切都好、还比平时快：Lead 说全成，Detail 说快了多少，没有 Gap
        Recap.Input Good() => new(
            StartAt: start,
            EndAt: start.AddMinutes(41),
            DoneTools: 2,
            TotalTools: 2,
            Groups: new[]
            {
                new Recap.GroupRow("日常", TimeSpan.FromMinutes(18), false, 22),
            },
            Tools: new[]
            {
                new Recap.ToolRow("BetterGI", nameof(ToolRunState.Done), null),
                new Recap.ToolRow("三月七助手", nameof(ToolRunState.Done), null),
            },
            Mora: 0,
            Exp: 0,
            Materials: null,
            LastErrorLine: null,
            StoppedByUser: false,
            RanNothing: false);

        // ---- 1) 全成 ----
        var good = Recap.Build(Good());
        Check("全成/有话说", good.Count >= 2, good.Count.ToString());
        Check("全成/第一条是 Lead", good[0].Kind == Recap.LineKind.Lead);
        Check("全成/说了几项跑成", good[0].Text.Contains("项都跑成了"), good[0].Text);
        Check("全成/带开跑时刻", good[0].Text.Contains("23:04"), good[0].Text);
        Check("全成/没有 Gap", good.All(l => l.Kind != Recap.LineKind.Gap), string.Join(" | ", good.Select(l => l.Kind)));
        Check("全成/提到比往常快", good.Any(l => l.Text.Contains("比往常快")), string.Join(" | ", good.Select(l => l.Text)));

        // ---- 2) 有失败：Gap 要最后一条，且带原因 ----
        var bad = Recap.Build(Good() with
        {
            DoneTools = 1,
            Tools = new[]
            {
                new Recap.ToolRow("BetterGI", nameof(ToolRunState.Done), null),
                new Recap.ToolRow("三月七助手", nameof(ToolRunState.Failed), "退出码 1"),
            },
        });
        Check("失败/说了几成几败", bad[0].Text.Contains("1 项跑成") && bad[0].Text.Contains("1 项没成"), bad[0].Text);
        Check("失败/最后一条是 Gap", bad[^1].Kind == Recap.LineKind.Gap, bad[^1].Kind.ToString());
        Check("失败/Gap 带名字", bad[^1].Text.Contains("三月七助手"), bad[^1].Text);
        Check("失败/Gap 带原因", bad[^1].Text.Contains("退出码 1"), bad[^1].Text);

        // ---- 3) 一项都没跑：只说一句，不硬凑 ----
        var none = Recap.Build(Good() with { RanNothing = true, DoneTools = 0, TotalTools = 0, Groups = null, Tools = null });
        Check("空/只有一句", none.Count == 1, none.Count.ToString());
        Check("空/说的是没跑", none[0].Text.Contains("没跑"), none[0].Text);

        // ---- 4) 用户主动停 ----
        var stopped = Recap.Build(Good() with { StoppedByUser = true });
        Check("停/第一条说停了", stopped[0].Text.Contains("你让它停了"), stopped[0].Text);
        Check("停/不出 Gap（是他自己停的，不算没做到）", stopped.All(l => l.Kind != Recap.LineKind.Gap),
            string.Join(" | ", stopped.Select(l => l.Kind)));

        // ---- 5) 比往常慢 ----
        var slow = Recap.Build(Good() with
        {
            Groups = new[] { new Recap.GroupRow("日常", TimeSpan.FromMinutes(40), false, 20) },
        });
        Check("慢/说慢了", slow.Any(l => l.Text.Contains("比往常慢")), string.Join(" | ", slow.Select(l => l.Text)));
        Check("慢/带百分比", slow.Any(l => l.Text.Contains("%")), string.Join(" | ", slow.Select(l => l.Text)));

        // ---- 6) 偏离不大就不说（±15% 以内是噪声） ----
        var same = Recap.Build(Good() with
        {
            Groups = new[] { new Recap.GroupRow("日常", TimeSpan.FromMinutes(21), false, 20) },
        });
        Check("相近/不说快慢", same.All(l => !l.Text.Contains("比往常")), string.Join(" | ", same.Select(l => l.Text)));

        // ---- 7) 样本不足：宁可不说，也不给个错数 ----
        var noMedian = Recap.Build(Good() with
        {
            Groups = new[] { new Recap.GroupRow("日常", TimeSpan.FromMinutes(18), false, null) },
        });
        Check("无样本/不说比往常", noMedian.All(l => !l.Text.Contains("比往常")), string.Join(" | ", noMedian.Select(l => l.Text)));

        // ---- 8) 没时间可评就说收益 ----
        var income = Recap.Build(Good() with
        {
            Groups = null,
            Mora = 12000,
            Exp = 30,
            Materials = new Dictionary<string, int>(StringComparer.Ordinal) { ["刀镡"] = 12 },
        });
        Check("收益/提到摩拉", income.Any(l => l.Text.Contains("摩拉")), string.Join(" | ", income.Select(l => l.Text)));

        // ---- 9) 配置组没结束 ----
        var unfinished = Recap.Build(Good() with
        {
            DoneTools = 0,
            TotalTools = 0,
            Tools = null,
            Groups = new[]
            {
                new Recap.GroupRow("日常", TimeSpan.FromMinutes(18), false, 20),
                new Recap.GroupRow("挖矿", null, true, null),
            },
        });
        Check("未结束/说了一组没结束", unfinished[0].Text.Contains("没结束"), unfinished[0].Text);
        Check("未结束/Gap 点名", unfinished[^1].Text.Contains("挖矿"), unfinished[^1].Text);

        // ---- 10) 顺序与封顶：任何输入都不许超过 4 行，Lead 第一 Gap 最后 ----
        var cases = new List<IReadOnlyList<Recap.Line>> { good, bad, none, stopped, slow, same, noMedian, income, unfinished };
        Check("封顶/最多 4 行", cases.All(c => c.Count <= 4), string.Join(",", cases.Select(c => c.Count)));
        Check("封顶/至少 1 行", cases.All(c => c.Count >= 1), string.Join(",", cases.Select(c => c.Count)));
        Check("顺序/Lead 永远第一", cases.All(c => c[0].Kind == Recap.LineKind.Lead));
        Check("顺序/Gap 只在最后", cases.All(c => c.Count(l => l.Kind == Recap.LineKind.Gap) <= 1
                                                  && (c[^1].Kind == Recap.LineKind.Gap
                                                      || c.All(l => l.Kind != Recap.LineKind.Gap))));

        // ---- 11) 不说教、不出现产品腔 ----
        string all = string.Join(" ", cases.SelectMany(c => c.Select(l => l.Text)));
        Check("语气/没有「建议你」", !all.Contains("建议你"), all);
        Check("语气/没有「请注意」", !all.Contains("请注意"), all);
        Check("语气/没有「为了更好」", !all.Contains("为了更好"), all);
        Check("语气/没有感叹号", !all.Contains("！"), all);

        // ---- 12) Duration 人话 ----
        Check("时长/不到一分钟", Recap.Duration(TimeSpan.FromSeconds(20)) == "不到 1 分钟");
        Check("时长/分钟", Recap.Duration(TimeSpan.FromMinutes(18)) == "18 分钟");
        Check("时长/整小时", Recap.Duration(TimeSpan.FromMinutes(120)) == "2 小时");
        Check("时长/小时加分钟", Recap.Duration(TimeSpan.FromMinutes(95)) == "1 小时 35 分");
        Check("时长/负数当零", Recap.Duration(TimeSpan.FromMinutes(-5)) == "不到 1 分钟");

        // ---- 13) null 输入不抛 ----
        Check("空输入/不抛", Recap.Build(null!).Count == 0);

        // ---- 14) 接线：主卡里真的渲染了 ----
        string xaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("接线/主卡有复盘容器", xaml.Contains("HomeRecap"), "MainWindow.xaml");
        Check("接线/有刷新方法", code.Contains("private void RefreshHomeRecap()"), "MainWindow.xaml.cs");
        Check("接线/刷主卡时会一起刷", code.Contains("RefreshHomeRecap();"), "MainWindow.xaml.cs");
        Check("接线/取数走五张表", code.Contains("GroupArchive.GetDay") && code.Contains("RunJournal.GetDay")
                                && code.Contains("HarvestLedger.GetDay"), "MainWindow.xaml.cs");
        Check("接线/正在跑时不显示（免得以为跑完了）", code.Contains("!QueueService.IsRunning"), "MainWindow.xaml.cs");

        Console.WriteLine($"RESULT recap fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #4：自动决定台账。
    ///
    /// 这一段钉的是三条边界，不是「能记下来」：
    /// ① 只在内存（不落盘）——它是「刚才发生了什么」，不是审计日志；
    /// ② 有上限——给人扫一眼的清单，给两千条等于没给；
    /// ③ 只记「本来可以不这样」的——正常跑完不记，否则真正要解释的那三条被淹掉。
    /// </summary>
    private static int RunDecision()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        DecisionLog.Clear();
        Check("空/计数为零", DecisionLog.Count == 0);
        Check("空/Recent 为空", DecisionLog.Recent(5).Count == 0);
        Check("空/Summary 为空串（界面据此整块收起）", DecisionLog.Summary(DateTime.Now) == "");

        var now = new DateTime(2026, 11, 17, 18, 0, 0);
        DecisionLog.AddAt(now.AddMinutes(-30), DecisionSource.Queue, "跳过 ok-ww", "它依赖的 BetterGI 这次没跑成");
        DecisionLog.AddAt(now.AddMinutes(-20), DecisionSource.Sentinel, "重启 BetterGI", "进程 30 秒没出现");
        DecisionLog.AddAt(now.AddMinutes(-10), DecisionSource.Schedule, "先不开始", "你 42 秒前还在动键鼠");

        Check("记/三条都进了", DecisionLog.Count == 3, DecisionLog.Count.ToString());
        var recent = DecisionLog.Recent(5);
        Check("Recent/最新在前", recent[0].Title == "先不开始", recent[0].Title);
        Check("Recent/取几条就几条", DecisionLog.Recent(2).Count == 2);
        Check("Recent/0 条返回空", DecisionLog.Recent(0).Count == 0);
        Check("Recent/负数返回空", DecisionLog.Recent(-3).Count == 0);

        // ---- 来源名 ----
        Check("来源/队列", DecisionLog.SourceName(DecisionSource.Queue) == "队列");
        Check("来源/守护", DecisionLog.SourceName(DecisionSource.Sentinel) == "守护");
        Check("来源/定时", DecisionLog.SourceName(DecisionSource.Schedule) == "定时");
        Check("来源/规则", DecisionLog.SourceName(DecisionSource.Rule) == "规则");
        Check("来源/程序", DecisionLog.SourceName(DecisionSource.System) == "程序");

        // ---- 一句话 ----
        string line = DecisionLog.Describe(recent[0]);
        Check("话/带来源", line.Contains("定时"), line);
        Check("话/带标题", line.Contains("先不开始"), line);
        Check("话/带理由", line.Contains("动键鼠"), line);
        Check("话/没有空理由", !line.Contains("没留下理由"), line);
        Check("话/null 安全", DecisionLog.Describe(null) == "");

        var noWhy = new Decision(now, DecisionSource.Queue, "跳过某工具", "");
        Check("话/理由空时明说", DecisionLog.Describe(noWhy).Contains("没留下理由"), DecisionLog.Describe(noWhy));

        // ---- 空输入不记（免得清单被空条目撑满） ----
        int before = DecisionLog.Count;
        DecisionLog.Add(DecisionSource.Queue, "", "");
        DecisionLog.Add(DecisionSource.Queue, null!, null!);
        DecisionLog.Add(DecisionSource.Queue, "   ", "  ");
        Check("空/全空的条目不入账", DecisionLog.Count == before, (DecisionLog.Count - before).ToString());

        // ---- 上限 ----
        DecisionLog.Clear();
        for (int i = 0; i < DecisionLog.Capacity + 50; i++)
        {
            DecisionLog.AddAt(now.AddSeconds(i), DecisionSource.Queue, $"第 {i} 条", "压测");
        }
        Check("上限/不超过 Capacity", DecisionLog.Count == DecisionLog.Capacity, DecisionLog.Count.ToString());
        var top = DecisionLog.Recent(1);
        Check("上限/留下的是最新那条", top[0].Title == $"第 {DecisionLog.Capacity + 49} 条", top[0].Title);

        // ---- 按天取 ----
        DecisionLog.Clear();
        DecisionLog.AddAt(now.Date.AddDays(-1).AddHours(23), DecisionSource.Queue, "昨天的", "x");
        DecisionLog.AddAt(now, DecisionSource.Queue, "今天的", "y");
        Check("按天/只到今天", DecisionLog.Today(now).Count == 1);
        Check("按天/取到的是今天那条", DecisionLog.Today(now)[0].Title == "今天的");

        // ---- Summary ----
        DecisionLog.Clear();
        for (int i = 0; i < 6; i++)
            DecisionLog.AddAt(now.AddMinutes(-i), DecisionSource.Queue, $"第 {i} 条", "理由");
        string summary = DecisionLog.Summary(now, 3);
        Check("总结/最多三行", summary.Split('\n').Length == 3, summary.Split('\n').Length.ToString());
        Check("总结/含来源", summary.Contains("队列"), summary);

        // ---- 清空 ----
        DecisionLog.Clear();
        Check("清空/归零", DecisionLog.Count == 0);

        // ---- 接线：决定真的被写进去了（不是只有台账自己在跑） ----
        string queue = ReadRepoSources("Dragon Butler/QueueService.cs");
        string sentinel = ReadRepoSources("Dragon Butler/SentinelService.cs");
        Check("接线/跳过要留理由", queue.Contains("DecisionLog.Add(DecisionSource.Queue, $\"跳过"), "QueueService.cs");
        Check("接线/整队停下要留理由", queue.Contains("DecisionLog.Add(DecisionSource.Queue, \"整队停下\""), "QueueService.cs");
        Check("接线/防打扰要留理由", queue.Contains("DecisionLog.Add(DecisionSource.Schedule, \"先不开始\""), "QueueService.cs");
        Check("接线/重启要留理由", sentinel.Contains("DecisionLog.Add(DecisionSource.Sentinel"), "SentinelService.cs");
        Check("接线/冷却期不重启要说清", sentinel.Contains("一分钟内已经重启过一次"), "SentinelService.cs");

        // ---- 边界：不许落盘 ----
        Check("边界/不写文件", !queue.Contains("DecisionLog.Save") && !sentinel.Contains("DecisionLog.Save"));
        Check("边界/没有序列化特性（它不是存档）",
            !ReadRepoSources("Dragon Butler/DecisionLog.cs").Contains("JsonPropertyName"),
            "DecisionLog.cs");

        // ---- 界面 ----
        string todayXaml = ReadRepoSources("Dragon Butler/TodayWindow.xaml");
        string todayCode = ReadRepoSources("Dragon Butler/TodayWindow.xaml.cs");
        Check("界面/今日有决策区", todayXaml.Contains("DecisionSection"), "TodayWindow.xaml");
        Check("界面/默认收起（没有就不占地方）", todayXaml.Contains("Visibility=\"Collapsed\""), "TodayWindow.xaml");
        Check("界面/渲染时会算可见性", todayCode.Contains("DecisionSection.Visibility"), "TodayWindow.xaml.cs");

        Console.WriteLine($"RESULT decision fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #3：基线。
    ///
    /// 最要紧的一条不是「能算出中位数」，而是<b>样本不足时必须闭嘴</b>——
    /// 跑了两天就说「你平时怎样」是在编，而编出来的数字用户没法反驳。
    /// </summary>
    private static int RunBaseline()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 中位数 ----
        Check("中位/空返回 null", Baseline.Median(Array.Empty<double>()) == null);
        Check("中位/null 返回 null", Baseline.Median(null) == null);
        Check("中位/单个", Baseline.Median(new[] { 7.0 }) == 7.0);
        Check("中位/奇数取中间", Baseline.Median(new[] { 10.0, 20.0, 60.0 }) == 20.0);
        Check("中位/偶数取均值", Baseline.Median(new[] { 10.0, 20.0, 40.0, 80.0 }) == 30.0);
        // 0 是有效样本，负数才忽略。这一条改的是断言不是实现：
        // 「那天一项都没失败」必须能参与中位数，否则「平时从没出过问题」这种最常见的
        // 情况会永远算不出基线。
        Check("中位/保留 0、忽略负数", Baseline.Median(new[] { 0.0, -5.0, 20.0, 10.0 }) == 10.0,
            Baseline.Median(new[] { 0.0, -5.0, 20.0, 10.0 })?.ToString() ?? "null");
        Check("中位/忽略 NaN 与无穷", Baseline.Median(new[] { double.NaN, double.PositiveInfinity, 20.0 }) == 20.0);
        Check("中位/不被极端值拉走", Baseline.Median(new[] { 20.0, 20.0, 20.0, 20.0, 999.0 }) == 20.0);

        // ---- 样本不足必须闭嘴 ----
        var few = new[] { 20.0, 20.0, 20.0 };   // 3 天 < MinSamples
        var vFew = Baseline.Judge(few, 40, " 分钟", "日常");
        Check("闭嘴/样本不足不给话", vFew.Text.Length == 0, vFew.Text);
        Check("闭嘴/样本不足级别是 Ok", vFew.Level == Baseline.Level.Ok);
        Check("闭嘴/阈值就是 5 天", Baseline.MinSamples == 5);
        Check("闭嘴/刚好 5 天开始说话",
            Baseline.Judge(new[] { 20.0, 20.0, 20.0, 20.0, 20.0 }, 40, " 分钟", "日常").Text.Length > 0);

        // ---- 正常范围不说话 ----
        var history = new[] { 20.0, 20.0, 20.0, 20.0, 20.0 };
        Check("正常/±20% 内不说话", Baseline.Judge(history, 22, " 分钟").Text.Length == 0);
        Check("正常/轻微偏高不说话", Baseline.Judge(history, 23.9, " 分钟").Text.Length == 0);
        Check("正常/不偏低不说话", Baseline.Judge(history, 17, " 分钟").Text.Length == 0);

        // ---- 偏离要说，且分两档 ----
        var notice = Baseline.Judge(history, 26, " 分钟", "日常");
        Check("偏高/超过 20% 说话", notice.Text.Length > 0, notice.Text);
        Check("偏高/级别 Notice", notice.Level == Baseline.Level.Notice, notice.Level.ToString());
        Check("偏高/文案带「高」", notice.Text.Contains("高"), notice.Text);
        Check("偏高/文案带基线值", notice.Text.Contains("20"), notice.Text);
        Check("偏高/文案带实测值", notice.Text.Contains("26"), notice.Text);
        Check("偏高/文案带百分比", notice.Text.Contains("30%"), notice.Text);

        var warn = Baseline.Judge(history, 40, " 分钟", "日常");
        Check("偏高/超过 50% 升到 Warn", warn.Level == Baseline.Level.Warn, warn.Level.ToString());

        // 低于基线一律不说话：好消息不该用警告色，且「今天跑得少」未必值得夸。
        var low = Baseline.Judge(history, 10, " 分钟", "日常");
        Check("偏低/不说话", low.Text.Length == 0, low.Text);
        Check("偏低/级别是 Ok", low.Level == Baseline.Level.Ok);

        // ---- 今天 0 项失败：不该显示成「低 100%」 ----
        var zero = Baseline.Judge(new[] { 2.0, 2.0, 2.0, 2.0, 2.0 }, 0, " 项", "没跑成的项");
        Check("零失败/不说话（不是报表）", zero.Text.Length == 0, zero.Text);

        // ---- 边界输入 ----
        Check("边界/actual 为 null 不说话", Baseline.Judge(history, null).Text.Length == 0);
        Check("边界/actual 负数不说话", Baseline.Judge(history, -1).Text.Length == 0);
        // 平时一直是 0、今天不是 0 —— 最该报的一种（「以前从没出过问题」）。
        var fromZero = Baseline.Judge(new[] { 0.0, 0.0, 0.0, 0.0, 0.0 }, 2, " 项", "没跑成的项");
        Check("从零/今天不是零说话", fromZero.Text.Length > 0, fromZero.Text);
        Check("从零/升到 Warn", fromZero.Level == Baseline.Level.Warn, fromZero.Level.ToString());
        Check("从零/文案说「平时一次都没有」", fromZero.Text.Contains("一次都没有"), fromZero.Text);
        Check("边界/基线为 0 且今天也是 0 不说话",
            Baseline.Judge(new[] { 0.0, 0.0, 0.0, 0.0, 0.0 }, 0).Text.Length == 0);

        // ---- JudgeLast ----
        Check("JudgeLast/取最后一个当今天",
            Baseline.JudgeLast(history, new[] { 40.0 }, " 分钟").Level == Baseline.Level.Warn);
        Check("JudgeLast/空的 recent 不说话", Baseline.JudgeLast(history, Array.Empty<double>()).Text.Length == 0);

        // ---- Describe ----
        Check("描述/不足时明说", Baseline.Describe(few).Contains("不够"), Baseline.Describe(few));
        Check("描述/够了就给中位数", Baseline.Describe(history).Contains("20"), Baseline.Describe(history));

        // ---- 适配层：不数今天 ----
        string facts = ReadRepoSources("Dragon Butler/BaselineFacts.cs");
        Check("适配/不含今天", facts.Contains("for (int back = 1; back <= days; back++)"), "BaselineFacts.cs");
        Check("适配/没跑的日子不算 0", facts.Contains("if (entries.Count == 0) continue"), "BaselineFacts.cs");
        Check("适配/读的是已有台账", facts.Contains("RunJournal.GetDay"), "BaselineFacts.cs");
        Check("适配/读的是已有存档", facts.Contains("GroupArchive.GetDay"), "BaselineFacts.cs");

        // ---- 界面 ----
        string xaml = ReadRepoSources("Dragon Butler/TodayWindow.xaml");
        string code = ReadRepoSources("Dragon Butler/TodayWindow.xaml.cs");
        Check("界面/今日有基线行", xaml.Contains("TbBaseline"), "TodayWindow.xaml");
        Check("界面/默认收起", xaml.Contains("Visibility=\"Collapsed\""), "TodayWindow.xaml");
        Check("界面/渲染时按文案有无决定可见性", code.Contains("v.Text.Length > 0 ? Visibility.Visible"), "TodayWindow.xaml.cs");

        Console.WriteLine($"RESULT baseline fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #5：时间银行。
    ///
    /// 这个数字的全部价值在于「诚实」——不折算、不推测、没数据就不显示。
    /// 所以断言里一半在验「什么时候必须闭嘴」。
    /// </summary>
    private static int RunTimeBank()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var now = new DateTime(2026, 11, 17);
        var days = new List<TimeBank.Day>
        {
            new(new DateTime(2026, 11, 1), 30),   // 本月
            new(new DateTime(2026, 11, 2), 45),   // 本月
            new(new DateTime(2026, 10, 31), 60),  // 上月
            new(new DateTime(2026, 10, 30), 90),  // 上月
        };

        var t = TimeBank.Sum(days, now);
        Check("累计/本月合计", Math.Abs(t.ThisMonthMinutes - 75) < 0.01, t.ThisMonthMinutes.ToString());
        Check("累计/全部合计", Math.Abs(t.AllMinutes - 225) < 0.01, t.AllMinutes.ToString());
        Check("累计/本月天数", t.MonthActiveDays == 2, t.MonthActiveDays.ToString());
        Check("累计/总天数", t.ActiveDays == 4, t.ActiveDays.ToString());

        // 0 分钟的日子不算「跑过」
        var withZero = new List<TimeBank.Day>
        {
            new(new DateTime(2026, 11, 3), 0),
            new(new DateTime(2026, 11, 4), 0),
        };
        Check("累计/零分钟不算活跃天", TimeBank.Sum(withZero, now).ActiveDays == 0);
        Check("累计/null 安全", TimeBank.Sum(null, now).AllMinutes == 0);

        // 跨月才有「从装上到现在」，本月内不提累计
        string both = TimeBank.Describe(t, now);
        Check("描述/带本月小时", both.Contains("1.3"), both);
        Check("描述/带本月天数", both.Contains("2 天"), both);
        Check("描述/跨月提累计", both.Contains("从装上到现在"), both);
        Check("描述/以句号结尾", both.EndsWith("。"), both);

        var sameMonth = new List<TimeBank.Day>
        {
            new(new DateTime(2026, 11, 1), 30),
            new(new DateTime(2026, 11, 2), 45),
        };
        Check("描述/同月不提累计", !TimeBank.Describe(TimeBank.Sum(sameMonth, now), now).Contains("从装上"), "");

        // 没数据就不显示——不能出现「本月 0 小时」
        Check("闭嘴/没有数据返回空", TimeBank.Describe(TimeBank.Sum(new List<TimeBank.Day>(), now), now).Length == 0);
        Check("闭嘴/null 返回空", TimeBank.Describe(null, now).Length == 0);
        Check("闭嘴/只有上月数据时本月为空", TimeBank.Describe(
            TimeBank.Sum(new List<TimeBank.Day> { new(new DateTime(2026, 10, 5), 120) }, now), now).Length == 0);

        // 小时格式
        Check("格式/不到 1 小时说分钟", TimeBank.Hours(45) == "45 分钟", TimeBank.Hours(45));
        Check("格式/零说 0 分钟", TimeBank.Hours(0) == "0 分钟", TimeBank.Hours(0));
        Check("格式/负数说 0 分钟", TimeBank.Hours(-5) == "0 分钟", TimeBank.Hours(-5));
        Check("格式/整数小时", TimeBank.Hours(120) == "2 小时", TimeBank.Hours(120));
        Check("格式/带一位小数", TimeBank.Hours(75) == "1.3 小时", TimeBank.Hours(75));
        Check("格式/十小时以上取整", TimeBank.Hours(6600) == "110 小时", TimeBank.Hours(6600));

        // 适配层
        string arch = ReadRepoSources("Dragon Butler/GroupArchive.cs");
        Check("适配/有按天求分钟", arch.Contains("public static List<(DateTime Date, double Minutes)> DailyMinutes"), "GroupArchive.cs");
        Check("适配/没跑的日子跳过", arch.Contains("if (!_data.TryGetValue(key, out var recs) || recs.Count == 0) continue"), "GroupArchive.cs");

        // 界面
        string xaml = ReadRepoSources("Dragon Butler/TodayWindow.xaml");
        string code = ReadRepoSources("Dragon Butler/TodayWindow.xaml.cs");
        Check("界面/今日有时间银行一行", xaml.Contains("TbTimeBank"), "TodayWindow.xaml");
        Check("界面/默认收起", xaml.Contains("TbTimeBank"), "TodayWindow.xaml");
        Check("界面/按文案有无决定可见性", code.Contains("text.Length > 0 ? Visibility.Visible"), "TodayWindow.xaml.cs");
        Check("界面/渲染里调用了", code.Contains("RenderTimeBank()"), "TodayWindow.xaml.cs");

        Console.WriteLine($"RESULT timebank fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #2：你走开就跑。
    ///
    /// 这块的核心不是「能不能判断没人」，而是 <b>判错的代价不对称</b>：
    /// 早开一次会打断你，晚开一次只是少跑一晚。所以断言大半在验
    /// 「什么情况下必须不开」。
    /// </summary>
    private static int RunAway()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var th = TimeSpan.FromMinutes(20);

        // ---- 单次判定 ----
        Check("空闲/刚好 20 分钟算没人", AwaySense.LooksAway(th, false));
        Check("空闲/19 分钟不算", !AwaySense.LooksAway(TimeSpan.FromMinutes(19), false));
        Check("空闲/0 不算", !AwaySense.LooksAway(TimeSpan.Zero, false));
        Check("空闲/前台是游戏一律不算", !AwaySense.LooksAway(TimeSpan.FromHours(2), true));
        Check("空闲/阈值可调", AwaySense.LooksAway(TimeSpan.FromMinutes(5), false, TimeSpan.FromMinutes(5)));
        Check("空闲/阈值为 0 时不算", !AwaySense.LooksAway(TimeSpan.FromHours(5), false, TimeSpan.Zero));
        Check("空闲/负阈值时不算", !AwaySense.LooksAway(TimeSpan.FromHours(5), false, TimeSpan.FromMinutes(-1)));

        // ---- 连续判定：必须攒够 ConfirmTicks 次 ----
        AwaySense.Snapshot? s = null;
        for (int i = 0; i < AwaySense.ConfirmTicks - 1; i++)
            s = AwaySense.Step(s, true, th, false);
        Check("确认/差一次还不算走开", s!.State != AwaySense.Presence.Away, s.State.ToString());
        Check("确认/记录已攒的连续次数", s.Streak == AwaySense.ConfirmTicks - 1, s.Streak.ToString());
        Check("确认/提示里带进度", s.Reason.Contains("/"), s.Reason);

        s = AwaySense.Step(s, true, th, false);
        Check("确认/够次数才认账", s.State == AwaySense.Presence.Away, s.State.ToString());
        Check("确认/认账时说明白", s.Reason == "你好像走了", s.Reason);

        // 中途一动就清零——这是「判错代价不对称」的直接体现
        var back = AwaySense.Step(s, false, TimeSpan.FromSeconds(3), false);
        Check("清零/一动就回到 Present", back.State == AwaySense.Presence.Present, back.State.ToString());
        Check("清零/连续次数归零", back.Streak == 0, back.Streak.ToString());
        Check("清零/理由说清是键鼠", back.Reason.Contains("键鼠"), back.Reason);

        var gameBack = AwaySense.Step(null, false, TimeSpan.Zero, true);
        Check("清零/因为游戏而不算时理由不同", gameBack.Reason.Contains("游戏"), gameBack.Reason);

        // 已经在 Away 状态，又采到一次「不算」→ 立刻回 Present，不能保持 Away
        var stayed = AwaySense.Step(new AwaySense.Snapshot(AwaySense.Presence.Away, th, 99, ""), false, TimeSpan.Zero, false);
        Check("清零/从 Away 退回 Present", stayed.State == AwaySense.Presence.Present, stayed.State.ToString());

        // ---- ShouldStart：三重闸门 ----
        var away = new AwaySense.Snapshot(AwaySense.Presence.Away, th, AwaySense.ConfirmTicks, "你好像走了");
        Check("开跑/不在且没跑过→开", AwaySense.ShouldStart(away, false, false));
        Check("开跑/已经在跑→不开", !AwaySense.ShouldStart(away, true, false));
        Check("开跑/今天跑过→不开", !AwaySense.ShouldStart(away, false, true));
        Check("开跑/你还在→不开", !AwaySense.ShouldStart(
            new AwaySense.Snapshot(AwaySense.Presence.Present, th, 0, ""), false, false));
        Check("开跑/还没看出来→不开", !AwaySense.ShouldStart(
            new AwaySense.Snapshot(AwaySense.Presence.Unknown, th, 0, ""), false, false));
        Check("开跑/null→不开", !AwaySense.ShouldStart(null, false, false));

        // ---- 文案 ----
        Check("描述/null 安全", AwaySense.Describe(null) == "还没看过", AwaySense.Describe(null));
        Check("描述/你不在", AwaySense.Describe(away).Contains("你不在"), AwaySense.Describe(away));
        Check("描述/带闲置时长", AwaySense.Describe(away).Contains("20"), AwaySense.Describe(away));
        Check("描述/你在", AwaySense.Describe(
            new AwaySense.Snapshot(AwaySense.Presence.Present, TimeSpan.Zero, 0, "")).Contains("你在"), "");

        // ---- 默认值：必须默认关 ----
        Check("默认/开关默认关", !new AppSettings().AwayStartEnabled);

        // ---- 接线 ----
        string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        string xaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("接线/定时器里调了 CheckAway", code.Contains("CheckSchedule(); CheckAway();"), "MainWindow.xaml.cs");
        Check("接线/有 CheckAway 实现", code.Contains("private void CheckAway()"), "MainWindow.xaml.cs");
        Check("接线/定时开着时让位", code.Contains("if (s.ScheduleEnabled) return;"), "MainWindow.xaml.cs");
        Check("接线/开跑前写一条决定", code.Contains("你走开，那就现在跑"), "MainWindow.xaml.cs");
        Check("接线/开完清零", code.Contains("AwayWatcher.Reset();"), "MainWindow.xaml.cs");
        Check("接线/没有取消按钮的诱惑", !xaml.Contains("取消走开"), "MainWindow.xaml");
        Check("接线/首屏有第三条路", xaml.Contains("BtnQuickAway"), "MainWindow.xaml");
        Check("接线/切换时互斥", code.Contains("s.ScheduleEnabled = false;"), "MainWindow.xaml.cs");
        // 走开模式下仍停在简版：摊开时间与星期会让人以为那些也生效。
        Check("接线/走开模式不摊开时间页", code.Contains("if (s.AwayStartEnabled)"), "MainWindow.xaml.cs");
        Check("接线/走开模式主按钮是改回按时间", code.Contains("改回按时间跑"), "MainWindow.xaml.cs");
        Check("接线/走开模式副按钮是现在就跑", code.Contains("\"runnow\""), "MainWindow.xaml.cs");

        Console.WriteLine($"RESULT away fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #6：交接单。
    ///
    /// 这块要挡的不是「能不能算出来」，而是 <b>顺序与语气</b>：
    /// 兜不住的事必须排在会发生的事前面，否则就成了宣传单。
    /// </summary>
    private static int RunHandover()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        Handover.Input Healthy(int days = 3) => new(
            Days: days, ScheduleEnabled: true, AwayStartEnabled: false, NextRunText: "08:00",
            NotifyLocalOnly: false, NotifyReady: true, EnabledTools: 2, ReadyTools: 2,
            AutoRestartBgi: true, RestartsToday: 0, StopWhenFailed: false,
            AfterRunDestructive: false, BgiPathOk: true, GroupCount: 3, FreeDiskMb: 20000);

        // ---- 健康：没有风险项，结论必须是「能自己过」 ----
        var ok = Handover.Build(Healthy());
        Check("健康/没有风险项", !ok.HasRisk, ok.Headline);
        Check("健康/结论说能自己过", ok.Headline.Contains("能自己过"), ok.Headline);
        Check("健康/有会发生的事", ok.Items.Count(i => i.Level == Handover.Severity.Plan) >= 2);
        Check("健康/提到会推手机", ok.Items.Any(i => i.Key == "fine.notify"));

        // ---- 每条风险都能单独触发，且以 × 开头 ----
        var cases = new (string Name, Func<Handover.Input, Handover.Input> Mutate, string Key)[]
        {
            ("路径不通", i => i with { BgiPathOk = false }, "bgi"),
            ("没有启用工具", i => i with { EnabledTools = 0, ReadyTools = 0 }, "tools"),
            ("有工具没路径", i => i with { ReadyTools = 1 }, "tools.partial"),
            ("只走本机通知", i => i with { NotifyLocalOnly = true }, "notify"),
            ("远程通知没配齐", i => i with { NotifyReady = false }, "notify.broken"),
            ("没有触发方式", i => i with { ScheduleEnabled = false }, "trigger"),
            ("没有配置组", i => i with { GroupCount = 0 }, "groups"),
            ("磁盘紧张", i => i with { FreeDiskMb = 100 }, "disk"),
        };
        foreach (var (name, mutate, key) in cases)
        {
            var sheet = Handover.Build(mutate(Healthy()));
            Check($"风险/{name}被标出", sheet.HasRisk && sheet.Items.Any(i => i.Key == key && i.Level == Handover.Severity.Risk), key);
        }

        // ---- 顺序：风险必须全部排在计划前面 ----
        var mixed = Handover.Build(Healthy() with { BgiPathOk = false, GroupCount = 0, FreeDiskMb = 10 });
        int lastRisk = mixed.Items.Select((x, idx) => (x, idx)).Where(t => t.x.Level == Handover.Severity.Risk).Max(t => t.idx);
        int firstPlan = mixed.Items.Select((x, idx) => (x, idx)).Where(t => t.x.Level == Handover.Severity.Plan).Min(t => t.idx);
        Check("顺序/风险全在计划前", lastRisk < firstPlan, $"{lastRisk} < {firstPlan}");
        Check("顺序/结论报出件数", mixed.Headline.Contains("3 件"), mixed.Headline);

        // ---- 文案要点 ----
        var one = Handover.Build(Healthy() with { BgiPathOk = false });
        Check("文案/一件时说一件", one.Headline.Contains("有一件事"), one.Headline);
        Check("文案/风险句说清后果", one.Items.First(i => i.Key == "bgi").Text.Contains("一次都不会跑"),
            one.Items.First(i => i.Key == "bgi").Text);

        var notify = Handover.Build(Healthy() with { NotifyLocalOnly = true });
        Check("文案/本机通知的后果是人不在等于没有",
            notify.Items.First(i => i.Key == "notify").Text.Contains("等于没有"),
            notify.Items.First(i => i.Key == "notify").Text);

        var abort = Handover.Build(Healthy() with { StopWhenFailed = true });
        Check("文案/失败即停要说出来", abort.Items.Any(i => i.Key == "plan.tools" && i.Text.Contains("整队停")));

        var noRestart = Handover.Build(Healthy() with { AutoRestartBgi = false });
        Check("文案/不重启要说清停在那儿", noRestart.Items.Any(i => i.Key == "plan.norestart" && i.Text.Contains("停在那儿")));

        var restarted = Handover.Build(Healthy() with { RestartsToday = 4 });
        Check("文案/重启带次数", restarted.Items.Any(i => i.Key == "plan.restart" && i.Text.Contains("4 次")));

        var destructive = Handover.Build(Healthy() with { AfterRunDestructive = true });
        Check("文案/会自动关机要说出来", destructive.Items.Any(i => i.Key == "plan.afterrun" && i.Text.Contains("关机")));

        var away = Handover.Build(Healthy() with { ScheduleEnabled = false, AwayStartEnabled = true });
        Check("文案/走开模式有自己的计划句", away.Items.Any(i => i.Key == "plan.away"));
        Check("文案/走开模式不再说每天几点", !away.Items.Any(i => i.Key == "plan.schedule"));

        // ---- 天数会影响措辞 ----
        Check("天数/2 天", Handover.Build(Healthy(2)).Headline.Contains("2 天"));
        Check("天数/0 天兜底为 1", Handover.Build(Healthy(0)).Headline.Contains("1 天"));

        // ---- 纯文本版 ----
        string report = Handover.Report(mixed, Healthy() with { BgiPathOk = false, GroupCount = 0, FreeDiskMb = 10 });
        Check("报告/含天数", report.Contains("离开 3 天"), report.Split('\n')[0]);
        Check("报告/风险用叉", report.Contains("× "), "");
        Check("报告/计划用点", report.Contains("· "), "");
        Check("报告/以结论开头第二行", report.Split('\n')[1].Contains("白跑"), report.Split('\n')[1]);

        // ---- 接线 ----
        string xaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        string win = ReadRepoSources("Dragon Butler/HandoverWindow.xaml.cs");
        Check("接线/系统区有入口", xaml.Contains("BtnHandover"), "MainWindow.xaml");
        Check("接线/有打开方法", code.Contains("private void OpenHandover()"), "MainWindow.xaml.cs");
        Check("接线/命令面板能搜到", code.Contains("case \"view.handover\""), "MainWindow.xaml.cs");
        Check("接线/窗口只读（不改设置）", !win.Contains("SaveSettings"), "HandoverWindow.xaml.cs");
        Check("接线/窗口不写文件", !win.Contains("File.WriteAllText"), "HandoverWindow.xaml.cs");

        Console.WriteLine($"RESULT handover fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #9：先替你把话说清楚。
    ///
    /// 这块的底线是 <b>「不知道就说不知道」</b>：认不出的退出码必须给一条诚实的
    /// 「我们没收录」，而不是套一句放之四海皆准的话。猜错一次，用户下次不信这个提示。
    /// </summary>
    private static int RunTroubleshoot()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 退出码：负数补码要认得出来 ----
        var av = Troubleshoot.FromExitCode(unchecked((int)3221225477u));
        Check("退出码/访问冲突按补码认得出", av != null && av.Key == "exit.access-violation", av?.Key ?? "null");
        Check("退出码/说清和管家无关", av != null && av.Why.Contains("无关"), av?.Why ?? "");
        Check("退出码/给的是下一步不是结论", av != null && av.Todo.Contains("事件查看器"), av?.Todo ?? "");
        Check("退出码/带可点的命令", av?.Action == "view.log", av?.Action ?? "null");

        Check("退出码/缺运行库", Troubleshoot.FromExitCode(unchecked((int)3221225781u))?.Key == "exit.dll-missing");
        Check("退出码/CLR 异常", Troubleshoot.FromExitCode(unchecked((int)3762504530u))?.Key == "exit.clr-exception");
        Check("退出码/退出码 1", Troubleshoot.FromExitCode(1)?.Key == "exit.code-1");
        Check("退出码/0 不解释（成功了不该出现在这里）", Troubleshoot.FromExitCode(0) == null);
        Check("退出码/没收录返回 null", Troubleshoot.FromExitCode(12345) == null);
        Check("退出码/null 返回 null", Troubleshoot.FromExitCode(null) == null);

        // ---- 日志行 ----
        Check("日志/任务启动失败", Troubleshoot.FromLogLine("[ERR] 任务启动失败")?.Key == "log.start-failed");
        Check("日志/树脂耗尽不算故障", Troubleshoot.FromLogLine("树脂耗尽，任务结束")?.Key == "log.resin-out");
        Check("日志/大小写不敏感", Troubleshoot.FromLogLine("accessviolation exception")?.Key == "log.access-violation");
        Check("日志/没命中返回 null", Troubleshoot.FromLogLine("一切都好") == null);
        Check("日志/空与 null 返回 null",
            Troubleshoot.FromLogLine("") == null && Troubleshoot.FromLogLine(null) == null);
        Check("日志/树脂耗尽不给命令（没什么可做）", Troubleshoot.FromLogLine("树脂耗尽")?.Action == null);

        // ---- Explain：优先级与兜底 ----
        Check("综合/退出码优先于日志", Troubleshoot.Explain(1, "任务启动失败").Key == "exit.code-1");
        Check("综合/没退出码时用日志", Troubleshoot.Explain(null, "任务启动失败").Key == "log.start-failed");

        var unknownCode = Troubleshoot.Explain(9999, null);
        Check("综合/没收录的码说实话", unknownCode.Key == "exit.unknown");
        Check("综合/没收录也给出这个码本身", unknownCode.What.Contains("9999"), unknownCode.What);
        Check("综合/没收录时给行动", unknownCode.Todo.Length > 0 && unknownCode.Action == "view.log");

        var nothing = Troubleshoot.Explain(null, null);
        Check("综合/什么都没有时诚实", nothing.Key == "unknown" && nothing.Action == "view.today");
        Check("综合/兜底不编原因", !nothing.Why.Contains("可能"), nothing.Why);

        // ---- 负数在文案里要显示成无符号十进制 ----
        var negShow = Troubleshoot.Explain(unchecked((int)3221225477u) + 1, null);
        Check("文案/负数折成无符号十进制", negShow.What.Contains("3221225478"), negShow.What);
        Check("文案/不出现负号", !negShow.What.Contains("-"), negShow.What);

        // ---- ParseExitCode：从人话里捞 ----
        Check("解析/标准句式", Troubleshoot.ParseExitCode("异常退出，退出码 3221225477") == unchecked((int)3221225477u));
        Check("解析/小码", Troubleshoot.ParseExitCode("退出码 1") == 1);
        Check("解析/多个空格", Troubleshoot.ParseExitCode("退出码   42") == 42);
        Check("解析/没有退出码", Troubleshoot.ParseExitCode("超时") == null);
        Check("解析/空与 null", Troubleshoot.ParseExitCode("") == null && Troubleshoot.ParseExitCode(null) == null);
        Check("解析/超过 int 的折成补码", Troubleshoot.ParseExitCode("退出码 3762504530") == unchecked((int)3762504530u));

        // ---- Brief ----
        Check("简述/不超 60 字", Troubleshoot.Brief(av).Length <= 60, Troubleshoot.Brief(av).Length.ToString());
        Check("简述/null 返回空", Troubleshoot.Brief(null) == "");

        // ---- 表不能被清空 ----
        Check("收录/至少八条", Troubleshoot.KnownCount >= 8, Troubleshoot.KnownCount.ToString());

        // ---- 界面接线 ----
        string today = ReadRepoSources("Dragon Butler/TodayWindow.xaml.cs");
        string evidence = ReadRepoSources("Dragon Butler/RunEvidence.cs");
        Check("接线/今日窗口解释失败项", today.Contains("Troubleshoot.Explain("), "TodayWindow.xaml.cs");
        Check("接线/认不出时不加废话", today.Contains("\"unknown\" or \"exit.unknown\""), "TodayWindow.xaml.cs");
        Check("接线/台账原因里捞退出码", today.Contains("Troubleshoot.ParseExitCode"), "TodayWindow.xaml.cs");
        Check("接线/原因里退出码显示成无符号", evidence.Contains("unchecked((uint)code)"), "RunEvidence.cs");

        Console.WriteLine($"RESULT troubleshoot fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #7：跑完收拾一下。
    ///
    /// 这块真正要挡的是「默认全关」和「有失败就不关游戏」两件事：
    /// 前者是不可逆动作的分寸，后者是别把现场也一起关了。
    /// </summary>
    /// <summary>
    /// v1.21：软件目录（8 个已知工具 + 参数候选的可信度分级）。
    ///
    /// 这块要挡的不是「有没有列全」，而是「有没有编」——
    /// 每一条参数都必须是 Documented 或 Observed，绝不允许凭空写一个值还标成已证实。
    /// </summary>
    private static int RunToolCatalog()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var all = ToolCatalog.All;

        // ---- 1) 收录数量与必备项 ----
        Check("目录/至少 8 个", all.Count >= 8, all.Count.ToString());
        Check("目录/有 BetterGI", ToolCatalog.Find("bettergi") != null);
        Check("目录/有三月七", ToolCatalog.Find("march7th") != null);
        Check("目录/有 ok-ww", ToolCatalog.Find("ok-ww") != null);
        Check("目录/绝区零", ToolCatalog.Find("zzz-od") != null);
        Check("目录/无限暖暖", ToolCatalog.Find("ok-nte") != null);
        Check("目录/明日方舟", ToolCatalog.Find("maa") != null);
        Check("目录/找不到返回 null", ToolCatalog.Find("no-such-tool") == null);
        Check("目录/空 id 返回 null", ToolCatalog.Find("") == null);

        // ---- 2) 内置标记：删掉 BGI 用户就回不来了 ----
        Check("内置/三个是内置", ToolCatalog.IsBuiltIn("bettergi") && ToolCatalog.IsBuiltIn("march7th") && ToolCatalog.IsBuiltIn("ok-ww"));
        Check("内置/绝区零不是内置", !ToolCatalog.IsBuiltIn("zzz-od"));
        Check("内置/大小写不敏感", ToolCatalog.IsBuiltIn("BetterGI"));

        // ---- 3) 每个条目都得填齐（空字段会让界面出现空白格） ----
        var bad = all.Where(t => string.IsNullOrWhiteSpace(t.ToolId)
                              || string.IsNullOrWhiteSpace(t.DisplayName)
                              || string.IsNullOrWhiteSpace(t.GameName)
                              || t.ExeNames.Length == 0
                              || t.Args.Count == 0)
                     .Select(t => t.ToolId).ToList();
        Check("目录/字段不缺", bad.Count == 0, string.Join(",", bad));

        // ---- 4) id 不重复 ----
        var dup = all.GroupBy(t => t.ToolId, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Check("目录/id 不重复", dup.Count == 0, string.Join(",", dup));

        // ---- 5) 参数候选：不允许「标成已证实其实没查过」 ----
        // 只要 Confidence 是 Documented，就必须在 Hint 里说清它是官方写的；
        // 这条挡的是以后有人图省事，随手写个值再标 Documented。
        var liars = all.SelectMany(t => t.Args)
                       .Where(a => a.Confidence == ArgConfidence.Documented
                                   && string.IsNullOrWhiteSpace(a.Hint))
                       .ToList();
        Check("参数/写 Documented 的必须有说明", liars.Count == 0, liars.Count.ToString());

        // ---- 6) 每个工具的默认参数必须在候选表里（否则界面下拉选不中它） ----
        var missing = all.Where(t => !t.Args.Any(a => string.Equals(a.Value, t.DefaultArguments, StringComparison.Ordinal)))
                         .Select(t => t.ToolId).ToList();
        Check("参数/默认值在候选里", missing.Count == 0, string.Join(",", missing));

        // ---- 7) BetterGI 的关键参数必须被标成 Documented（这是唯一查过源码的） ----
        var bgi = ToolCatalog.Find("bettergi")!;
        var oneDragon = bgi.Args.FirstOrDefault(a => a.Value.Contains("startOneDragon", StringComparison.Ordinal));
        Check("BetterGI/有一条龙的参数", oneDragon != null);
        Check("BetterGI/一条龙标成已证实", oneDragon?.Confidence == ArgConfidence.Documented,
            oneDragon?.Confidence.ToString() ?? "null");
        Check("BetterGI/含组名占位", oneDragon != null && oneDragon.Value.Contains("{组名}"));
        Check("BetterGI/默认可空跑", bgi.Args.Any(a => a.Value.Length == 0));

        // ---- 8) 认不出的必须诚实留空，不能编一个 ----
        var zzz = ToolCatalog.Find("zzz-od")!;
        Check("绝区零/参数全标自填", zzz.Args.All(a => a.Confidence == ArgConfidence.Custom));
        Check("绝区零/默认留空", zzz.DefaultArguments.Length == 0);
        var nte = ToolCatalog.Find("ok-nte")!;
        Check("无限暖暖/参数全标自填", nte.Args.All(a => a.Confidence == ArgConfidence.Custom));

        // ---- 9) 按 exe 名反查 ----
        Check("反查/bettergi.exe", ToolCatalog.FromExeName("BetterGI.exe")?.ToolId == "bettergi");
        Check("反查/三月七 launcher", ToolCatalog.FromExeName("March7th Launcher.exe")?.ToolId == "march7th");
        Check("反查/ok-ww", ToolCatalog.FromExeName("ok-ww.exe")?.ToolId == "ok-ww");
        Check("反查/认不出返回 null", ToolCatalog.FromExeName("notepad.exe") == null);
        Check("反查/空返回 null", ToolCatalog.FromExeName("") == null);

        // ---- 10) 从目录里一堆 exe 猜 ----
        Check("猜目录/认出 BetterGI", ToolCatalog.GuessFromFiles(new[] { "a.exe", "BetterGI.exe" })?.ToolId == "bettergi");
        Check("猜目录/全认不出返回 null", ToolCatalog.GuessFromFiles(new[] { "a.exe", "b.exe" }) == null);
        Check("猜目录/null 返回 null", ToolCatalog.GuessFromFiles(null) == null);

        // ---- 11) 默认参数查询 ----
        Check("默认参数/BetterGI", ToolCatalog.DefaultArgsOf("bettergi").Contains("startOneDragon"));
        Check("默认参数/未知工具是空串", ToolCatalog.DefaultArgsOf("no-such-tool").Length == 0);

        // ---- 12) 可信度文案 ----
        Check("文案/已证实", KnownTool.ConfidenceText(ArgConfidence.Documented).Contains("文档"));
        Check("文案/见过", KnownTool.ConfidenceText(ArgConfidence.Observed).Contains("别人"));
        Check("文案/自填", KnownTool.ConfidenceText(ArgConfidence.Custom).Contains("自"));
        Check("文案/已证实带对勾", KnownTool.ArgText(new ArgChoice { Value = "x", Confidence = ArgConfidence.Documented }).StartsWith("✓"));
        Check("文案/自填不带勾", !KnownTool.ArgText(new ArgChoice { Value = "x", Confidence = ArgConfidence.Custom }).StartsWith("✓"));
        Check("文案/null 安全", KnownTool.ArgText(null).Length == 0);

        // ---- 13) ToProfile：转出来的档案能直接用 ----
        var profile = ToolCatalog.ToProfile(bgi);
        Check("转档案/id 一致", profile.ToolId == "bettergi", profile.ToolId);
        Check("转档案/有参数", profile.LaunchArguments == bgi.DefaultArguments, profile.LaunchArguments);
        Check("转档案/有进程名", !string.IsNullOrWhiteSpace(profile.ManagedProcessName));
        Check("转档案/BGI 用日志锚点", profile.PrimarySignal == CompletionSignalKind.LogPattern,
            profile.PrimarySignal.ToString());
        var zzzProfile = ToolCatalog.ToProfile(zzz);
        Check("转档案/其余用退出码", zzzProfile.PrimarySignal == CompletionSignalKind.ProcessExit0,
            zzzProfile.PrimarySignal.ToString());

        // ---- 14) 接线：启动窗用得上这些候选 ----
        string launchCs = ReadRepoSources("Dragon Butler/ToolLaunchWindow.xaml.cs");
        string launchXaml = ReadRepoSources("Dragon Butler/ToolLaunchWindow.xaml");
        Check("接线/启动窗引用参数候选", launchCs.Contains("ToolCatalog."));
        Check("接线/启动窗有参数下拉", launchXaml.Contains("CbArgs"));
        Check("接线/启动窗可自由改参数", launchXaml.Contains("TbArgs"));
        Check("接线/启动窗有试启动", launchXaml.Contains("BtnTest"));
        Check("接线/内置不可删", launchCs.Contains("IsBuiltIn"));

        Console.WriteLine($"RESULT toolcatalog fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.21：桌面网格（2 行 4 列，可自由排序）。
    ///
    /// 这块最容易写错的三处，正好就是断言的重点：
    /// ① 已删除的工具不能留空位；② 排到底不能循环；③ 新工具排最后不插队。
    /// </summary>
    private static int RunToolGrid()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var natural = new List<string> { "bettergi", "march7th", "ok-ww" };

        // ---- 1) 常量 ----
        Check("格子/4 列", ToolGrid.Columns == 4, ToolGrid.Columns.ToString());
        Check("格子/2 行", ToolGrid.Rows == 2, ToolGrid.Rows.ToString());
        Check("格子/一共 8 格", ToolGrid.Capacity == 8, ToolGrid.Capacity.ToString());

        // ---- 2) Order：没排过就按自然序 ----
        var o1 = ToolGrid.Order(natural, null);
        Check("排序/没排过按自然序", o1.Count == 3 && o1[0] == "bettergi" && o1[2] == "ok-ww",
            string.Join(",", o1));
        Check("排序/null 自然序", ToolGrid.Order(null, null).Count == 0);
        Check("排序/null saved", ToolGrid.Order(natural, null).Count == 3);

        // ---- 3) Order：用户排过的优先且保持原序 ----
        var o2 = ToolGrid.Order(natural, new[] { "ok-ww", "bettergi", "march7th" });
        Check("排序/按用户排的来", string.Join(",", o2) == "ok-ww,bettergi,march7th", string.Join(",", o2));

        // ---- 4) Order：已删除的 id 静默剔除，不留空位 ----
        var o3 = ToolGrid.Order(natural, new[] { "gone", "ok-ww", "bettergi" });
        Check("排序/删掉的工具不留空位", string.Join(",", o3) == "ok-ww,bettergi,march7th", string.Join(",", o3));
        Check("排序/总数不变", o3.Count == 3, o3.Count.ToString());

        // ---- 5) Order：排过的在前、没排过的按自然序补后 ----
        var o4 = ToolGrid.Order(natural, new[] { "march7th" });
        Check("排序/排过的在前", string.Join(",", o4) == "march7th,bettergi,ok-ww", string.Join(",", o4));

        // ---- 6) Order：大小写不敏感、重复不重复加 ----
        var o5 = ToolGrid.Order(natural, new[] { "BETTERGI", "bettergi" });
        Check("排序/大小写不敏感且不重复", string.Join(",", o5) == "BETTERGI,march7th,ok-ww", string.Join(",", o5));

        // ---- 7) Move：正常左右挪 ----
        var order = new List<string> { "bettergi", "march7th", "ok-ww" };
        var right = ToolGrid.Move(order, "bettergi", 1);
        Check("挪动/往右一格", right != null && string.Join(",", right) == "march7th,bettergi,ok-ww",
            right == null ? "null" : string.Join(",", right));
        var left = ToolGrid.Move(order, "ok-ww", -1);
        Check("挪动/往左一格", left != null && string.Join(",", left) == "bettergi,ok-ww,march7th",
            left == null ? "null" : string.Join(",", left));
        Check("挪动/原列表没被改", string.Join(",", order) == "bettergi,march7th,ok-ww", string.Join(",", order));

        // ---- 8) Move：越界不动（不循环） ----
        Check("挪动/第一个再往左不动", ToolGrid.Move(order, "bettergi", -1) == null);
        Check("挪动/最后一个再往右不动", ToolGrid.Move(order, "ok-ww", 1) == null);
        Check("挪动/delta 为 0 不动", ToolGrid.Move(order, "bettergi", 0) == null);
        Check("挪动/找不到的工具不动", ToolGrid.Move(order, "nope", 1) == null);
        Check("挪动/空 id 不动", ToolGrid.Move(order, "", 1) == null);
        Check("挪动/null 列表不动", ToolGrid.Move(null, "bettergi", 1) == null);
        Check("挪动/空列表不动", ToolGrid.Move(new List<string>(), "bettergi", 1) == null);

        // ---- 9) Remove / Append ----
        Check("删除/去掉一个", string.Join(",", ToolGrid.Remove(order, "march7th")) == "bettergi,ok-ww");
        Check("删除/大小写不敏感", ToolGrid.Remove(order, "MARCH7TH").Count == 2);
        Check("删除/不存在的安全", ToolGrid.Remove(order, "nope").Count == 3);
        Check("删除/null 返回空表", ToolGrid.Remove(null, "bettergi").Count == 0);
        Check("追加/排最后", string.Join(",", ToolGrid.Append(order, "zzz-od")) == "bettergi,march7th,ok-ww,zzz-od");
        Check("追加/已存在不重复", ToolGrid.Append(order, "bettergi").Count == 3);
        Check("追加/大小写不敏感不重复", ToolGrid.Append(order, "BETTERGI").Count == 3);
        Check("追加/空 id 不加", ToolGrid.Append(order, "").Count == 3);
        Check("追加/null 返回单元素", ToolGrid.Append(null, "bettergi").Count == 1);

        // ---- 10) Visible / OverflowCount ----
        var nine = Enumerable.Range(0, 9).Select(i => "t" + i).ToList();
        Check("上桌/只取前 8 个", ToolGrid.Visible(nine).Count == 8, ToolGrid.Visible(nine).Count.ToString());
        Check("上桌/顺序不变", ToolGrid.Visible(nine)[0] == "t0" && ToolGrid.Visible(nine)[7] == "t7");
        Check("溢出/9 个里 1 个排不进", ToolGrid.OverflowCount(nine) == 1, ToolGrid.OverflowCount(nine).ToString());
        Check("溢出/8 个正好不溢出", ToolGrid.OverflowCount(nine.Take(8).ToList()) == 0);
        Check("溢出/3 个不溢出", ToolGrid.OverflowCount(order) == 0);
        Check("溢出/null 是 0", ToolGrid.OverflowCount(null) == 0);
        Check("上桌/null 空表", ToolGrid.Visible(null).Count == 0);

        // ---- 11) 接线 ----
        string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        string mainCs = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("接线/有 ToolGridHost", mainXaml.Contains("\"ToolGridHost\""));
        Check("接线/ToolGridHost 是 4 列 Grid", mainXaml.Contains("<Grid x:Name=\"ToolGridHost\""));
        Check("接线/有添加按钮", mainXaml.Contains("BtnGridAdd"));
        Check("接线/有溢出提示", mainXaml.Contains("TbGridOverflow"));
        Check("接线/点格子进启动设置", mainCs.Contains("OpenToolLaunch("));
        Check("接线/右键菜单", mainCs.Contains("ToolTile_RightClick"));
        Check("接线/格子里按宽度设 MaxWidth", mainCs.Contains("nameText.MaxWidth"));
        Check("接线/订单存进设置", mainCs.Contains("SaveToolOrder("));
        Check("接线/刷新时重画网格", mainCs.Contains("RenderToolGrid()"));
        Check("接线/工具表里有 ToolOrder", ReadRepoSources("Dragon Butler/AppState.cs").Contains("ToolOrder"));

        Console.WriteLine($"RESULT toolgrid fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    private static int RunTidy()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        Tidy.Input Ready(bool closeGame = true, bool closeTool = true, bool restore = true, bool failure = false) => new(
            Enabled: true, CloseGameEnabled: closeGame, CloseToolEnabled: closeTool,
            RestoreWindowsEnabled: restore, GameRunning: true, ToolRunning: true,
            RememberedWindows: 2, AnyFailure: failure, DryRun: false);

        // ---- 默认全关 ----
        Check("设置/总开关默认关", !new AppSettings().TidyEnabled);
        Check("设置/关游戏默认关", !new AppSettings().TidyCloseGame);
        Check("设置/关工具默认关", !new AppSettings().TidyCloseTool);
        Check("设置/还原窗口默认关", !new AppSettings().TidyRestoreWindows);

        // ---- 总开关关着时什么都不做 ----
        var off = Tidy.Build(Ready() with { Enabled = false });
        Check("总开关关/没有步骤", off.Steps.Count == 0);
        Check("总开关关/文案说没开", off.Summary.Contains("没开"), off.Summary);
        Check("总开关关/不算有事可做", !off.Anything);

        // ---- 三项都开 ----
        var all = Tidy.Build(Ready());
        Check("全开/三步都在", all.Steps.Count == 3, all.Steps.Count.ToString());
        Check("全开/关游戏是破坏性的", all.Steps.First(s => s.Kind == Tidy.Action.CloseGame).Destructive);
        Check("全开/关工具不是破坏性的", !all.Steps.First(s => s.Kind == Tidy.Action.CloseTool).Destructive);
        Check("全开/结论说已做", all.Summary.StartsWith("已"), all.Summary);

        // ---- 单项开关独立生效 ----
        var onlyTool = Tidy.Build(Ready(closeGame: false, restore: false));
        Check("单项/只开工具就只一步", onlyTool.Steps.Count == 1 && onlyTool.Steps[0].Kind == Tidy.Action.CloseTool);

        var onlyRestore = Tidy.Build(Ready(closeGame: false, closeTool: false));
        Check("单项/只开还原就只一步", onlyRestore.Steps.Count == 1 && onlyRestore.Steps[0].Kind == Tidy.Action.RestoreWindows);
        Check("单项/还原两步说两个窗口", onlyRestore.Steps[0].Text.Contains("2 个"), onlyRestore.Steps[0].Text);

        // ---- 进程没开就无事可做 ----
        var nothing = Tidy.Build(Ready() with { GameRunning = false, ToolRunning = false, RememberedWindows = 0 });
        Check("没事/三步都不成立", nothing.Steps.Count == 0 && !nothing.Anything);
        Check("没事/文案说没什么要收拾", nothing.Summary.Contains("没什么"), nothing.Summary);

        // ---- 有失败时不关游戏（最关键的一条） ----
        var failed = Tidy.Build(Ready(failure: true));
        Check("失败/不关游戏", !failed.Steps.Any(s => s.Kind == Tidy.Action.CloseGame && s.Destructive));
        Check("失败/说清为什么留游戏", failed.Steps.Any(s => s.Text.Contains("没跑成")), failed.Steps[0].Text);
        Check("失败/其余照做", failed.Steps.Any(s => s.Kind == Tidy.Action.CloseTool));

        // ---- 演练不提「已」 ----
        var dry = Tidy.Build(Ready() with { DryRun = true });
        Check("演练/文案带演练二字", dry.Summary.Contains("演练"), dry.Summary);
        Check("演练/不说已做", !dry.Summary.StartsWith("已"), dry.Summary);

        // ---- WorthRemembering：记值得记的窗口 ----
        Check("记忆/正常窗口值得记", Tidy.WorthRemembering(new Tidy.WindowSpot("a", "", 100, 100, 1280, 720), 1920, 1080));
        Check("记忆/太小的不记", !Tidy.WorthRemembering(new Tidy.WindowSpot("a", "", 100, 100, 100, 80), 1920, 1080));
        Check("记忆/整块在屏幕外不记", !Tidy.WorthRemembering(new Tidy.WindowSpot("a", "", -2000, 100, 1280, 720), 1920, 1080));
        Check("记忆/尺寸离谱不记", !Tidy.WorthRemembering(new Tidy.WindowSpot("a", "", 0, 0, 9000, 9000), 1920, 1080));
        Check("记忆/null 不记", !Tidy.WorthRemembering(null, 1920, 1080));

        // ---- SamePlace：差几个像素不算动过 ----
        var p1 = new Tidy.WindowSpot("a", "", 100, 100, 800, 600);
        var p2 = new Tidy.WindowSpot("a", "", 103, 102, 802, 599);
        var p3 = new Tidy.WindowSpot("a", "", 300, 100, 800, 600);
        Check("归位/差三像素算归位", Tidy.SamePlace(p1, p2));
        Check("归位/挪了两百像素不算", !Tidy.SamePlace(p1, p3));
        Check("归位/null 不算", !Tidy.SamePlace(p1, null));

        // ---- 接线 ----
        string queue = ReadRepoSources("Dragon Butler/QueueService.cs");
        string xaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        string win = ReadRepoSources("Dragon Butler/TidyWindow.xaml.cs");
        Check("接线/队列跑完会收拾", queue.Contains("TidyService.Execute(tidy)"), "QueueService.cs");
        Check("接线/收拾在跑完动作之前", queue.IndexOf("TidyService.Execute(tidy)") < queue.IndexOf("AfterRunActionService.Execute(new AfterRunContext"), "顺序");
        Check("接线/开跑前记住窗口", queue.Contains("TidyService.Capture(name)"), "QueueService.cs");
        Check("接线/失败不冒泡", queue.Contains("\"收拾失败：\""), "QueueService.cs");
        Check("接线/主窗有入口", xaml.Contains("BtnTidyConfig"), "MainWindow.xaml");
        Check("接线/窗口写的是设置不是别处", win.Contains("AppState.SaveSettings()"), "TidyWindow.xaml.cs");

        Console.WriteLine($"RESULT tidy fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #8：它会在你不注意的时候变好。
    ///
    /// 这是十条里唯一会碰到用户设置的一条，所以断言的重点全在<b>克制</b>上：
    /// 样本不够闭嘴、差距不够闭嘴、只出建议不动手。
    /// </summary>
    private static int RunSuggest()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        Suggest.ToolStat Stat(string id, int runs, int fails2, double avg = 20) => new(id, id, runs, fails2, avg);

        Suggest.Input Base(int days = 30) => new(
            Tools: new[] { Stat("bettergi", 20, 1), Stat("march7th", 20, 14) },
            SampleDays: days, NotifyCount30d: 0, NotifyOpenedCount: 0,
            ScheduleEnabled: true, AwayEnabled: false, GroupsNeverRun: 0, TotalGroups: 3);

        // ---- 样本不够一律闭嘴 ----
        Check("克制/6 天不给建议", Suggest.Build(Base(6)).Count == 0);
        Check("克制/7 天开始给", Suggest.Build(Base(7)).Count > 0);
        Check("克制/0 天不给", Suggest.Build(Base(0)).Count == 0);
        Check("克制/null 工具列表不抛", Suggest.Build(Base() with { Tools = null }) != null);

        // ---- 队列顺序建议 ----
        var order = Suggest.Build(Base()).FirstOrDefault(i => i.Key == "queue.order");
        Check("顺序/失败率高才提", order != null, order?.Title ?? "null");
        Check("顺序/说清凭什么", order != null && order.Why.Contains("14 次"), order?.Why ?? "");
        Check("顺序/给的是去处不是开关", order?.Action == "view.queue", order?.Action ?? "");

        var balanced = Suggest.Build(Base() with
        {
            Tools = new[] { Stat("a", 20, 5), Stat("b", 20, 4) },
        });
        Check("顺序/差不多就不提", !balanced.Any(i => i.Key == "queue.order"));

        var fewRuns = Suggest.Build(Base() with
        {
            Tools = new[] { Stat("a", 2, 2), Stat("b", 2, 0) },
        });
        Check("顺序/跑得太少不提（样本不足）", !fewRuns.Any(i => i.Key == "queue.order"));

        var allBad = Suggest.Build(Base() with
        {
            Tools = new[] { Stat("a", 10, 9), Stat("b", 10, 8) },
        });
        Check("顺序/两个都差就不比（差距不够）", !allBad.Any(i => i.Key == "queue.order"));

        // ---- 通知噪音 ----
        var noisy = Suggest.Build(Base() with { NotifyCount30d = 40, NotifyOpenedCount = 1 });
        Check("通知/发得多看得少会提", noisy.Any(i => i.Key == "notify.noise"));
        Check("通知/说清两个数", noisy.First(i => i.Key == "notify.noise").Why.Contains("40"));

        var fine = Suggest.Build(Base() with { NotifyCount30d = 40, NotifyOpenedCount = 20 });
        Check("通知/看得多就不提", !fine.Any(i => i.Key == "notify.noise"));

        var few = Suggest.Build(Base() with { NotifyCount30d = 3, NotifyOpenedCount = 0 });
        Check("通知/发得少不提（样本不足）", !few.Any(i => i.Key == "notify.noise"));

        // ---- 没用过的配置组 ----
        var unused = Suggest.Build(Base() with { TotalGroups = 6, GroupsNeverRun = 4 });
        Check("配置组/一半没用过会提", unused.Any(i => i.Key == "groups.unused"));
        Check("配置组/说清几组", unused.First(i => i.Key == "groups.unused").Title.Contains("4"));

        var someUsed = Suggest.Build(Base() with { TotalGroups = 6, GroupsNeverRun = 1 });
        Check("配置组/用得还行不提", !someUsed.Any(i => i.Key == "groups.unused"));

        // ---- 定时建议 ----
        var noSchedule = Suggest.Build(Base(20) with { ScheduleEnabled = false, AwayEnabled = false });
        Check("定时/没开定时且跑得多会提", noSchedule.Any(i => i.Key == "schedule.auto"));

        var hasSchedule = Suggest.Build(Base(20));
        Check("定时/已经开了就不提", !hasSchedule.Any(i => i.Key == "schedule.auto"));

        var away = Suggest.Build(Base(20) with { ScheduleEnabled = false, AwayEnabled = true });
        Check("定时/开了走开模式也不提", !away.Any(i => i.Key == "schedule.auto"));

        // ---- 结论句 ----
        Check("结论/没有建议返回空", Suggest.Headline(Array.Empty<Suggest.Idea>()) == "");
        Check("结论/null 返回空", Suggest.Headline(null) == "");
        Check("结论/一条", Suggest.Headline(new[] { order! }).Contains("一条"), Suggest.Headline(new[] { order! }));

        // ---- 接线：只出建议不动手 ----
        string code = ReadRepoSources("Dragon Butler/Suggest.cs");
        string today = ReadRepoSources("Dragon Butler/TodayWindow.xaml.cs");
        string xaml = ReadRepoSources("Dragon Butler/TodayWindow.xaml");
        Check("接线/建议模块不写设置", !code.Contains("SaveSettings"), "Suggest.cs");
        Check("接线/建议模块不碰磁盘", !code.Contains("File."), "Suggest.cs");
        Check("接线/今日窗口有建议区", xaml.Contains("SuggestSection"), "TodayWindow.xaml");
        Check("接线/没有建议时整块收起", today.Contains("SuggestSection.Visibility = Visibility.Collapsed"), "TodayWindow.xaml.cs");
        Check("接线/不给一键改", !today.Contains("Suggest.Idea") || !today.Contains("Apply("), "TodayWindow.xaml.cs");

        Console.WriteLine($"RESULT suggest fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.15 / L6：通知模板。
    /// 最要紧的不是「能替换」，而是三件不肯让步的事：
    /// 留空必须回到内置正文（老用户升级后一个字都不变）、
    /// 拼错的占位符要原样留着（换成空白的话，用户永远不知道自己拼错了）、
    /// 正文里有左括号没右括号时不能抛。
    /// </summary>
    private static int RunTemplate()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var v = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["结果"] = "成功",
            ["成功"] = "3",
            ["总数"] = "3",
            ["耗时"] = "2 小时 12 分",
            ["原因"] = "",
            ["现场"] = "",
            ["收益"] = "摩拉 12.3 万",
        };

        // ---- 留空：这是升级安全阀 ----
        Check("空模板/空串返回空", NotifyTemplate.Render("", v) == "");
        Check("空模板/空白返回空", NotifyTemplate.Render("   ", v) == "");
        Check("空模板/null 返回空", NotifyTemplate.Render(null, v) == "");
        Check("空模板/默认设置就是空", NotifyTemplate.Render(AppState.Settings.NotifyQueueTemplate, v) == "");

        // ---- 替换 ----
        Check("替换/单个占位符", NotifyTemplate.Render("{成功}", v) == "3");
        Check("替换/一句完整话",
            NotifyTemplate.Render("今晚 {成功}/{总数}，用了 {耗时}。", v) == "今晚 3/3，用了 2 小时 12 分。");
        Check("替换/同一个占位符出现两次",
            NotifyTemplate.Render("{结果}，{结果}", v) == "成功，成功");
        Check("替换/空的占位符值替换成空", NotifyTemplate.Render("原因：{原因}。", v) == "原因：。");
        Check("替换/没有占位符就原样返回", NotifyTemplate.Render("跑完了", v) == "跑完了");
        Check("替换/首尾空白会去掉", NotifyTemplate.Render("  {成功}  ", v) == "3");

        // ---- 认不出的占位符：原样留着 ----
        Check("未知/拼错的留着", NotifyTemplate.Render("{成攻} 了", v) == "{成攻} 了");
        Check("未知/英文键留着", NotifyTemplate.Render("{ok}/{total}", v) == "{ok}/{total}");
        Check("未知/混在句子里只换认得的",
            NotifyTemplate.Render("{成功} 项，{成攻} 别处", v) == "3 项，{成攻} 别处");

        // ---- 坏输入不抛 ----
        bool threw = false;
        try { NotifyTemplate.Render("{成功", v); } catch { threw = true; }
        Check("坏输入/左括号没闭合不抛", !threw);
        Check("坏输入/左括号没闭合原样留着", NotifyTemplate.Render("前 {成功", v) == "前 {成功");

        threw = false;
        try { NotifyTemplate.Render("{}", v); } catch { threw = true; }
        Check("坏输入/空花括号不抛", !threw);
        Check("坏输入/空花括号认不出就留着", NotifyTemplate.Render("{}", v) == "{}");

        threw = false;
        try { NotifyTemplate.Render("{成功}", null); } catch { threw = true; }
        Check("坏输入/没有字典不抛", !threw);
        Check("坏输入/没有字典时保持原样", NotifyTemplate.Render("{成功}", null) == "{成功}");

        // ---- OneLine：换行在部分渠道会被吞，压成一行 ----
        Check("一行/换行变空格", NotifyTemplate.OneLine("第一行\n第二行") == "第一行 第二行");
        Check("一行/CRLF 也变空格", NotifyTemplate.OneLine("甲\r\n乙") == "甲 乙");
        Check("一行/连续空格压成一个", NotifyTemplate.OneLine("甲    乙") == "甲 乙");
        Check("一行/空输入返回空", NotifyTemplate.OneLine("  \n ") == "");

        // ---- 界面：输入框与提示都在 ----
        string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("界面/有模板输入框", mainXaml.Contains("x:Name=\"TbQueueTemplate\"", StringComparison.Ordinal));
        Check("界面/有提示行", mainXaml.Contains("x:Name=\"TbQueueTemplateHint\"", StringComparison.Ordinal));
        Check("界面/输入框接了保存", mainXaml.Contains("LostFocus=\"TbQueueTemplate_LostFocus\"", StringComparison.Ordinal));

        // ---- 清单与实现必须对得上 ----
        Check("清单/七个占位符", NotifyTemplate.Placeholders.Length == 7);
        Check("清单/提示里含带花括号的写法",
            NotifyTemplate.Hint.Contains("{结果}", StringComparison.Ordinal)
            && NotifyTemplate.Hint.Contains("{收益}", StringComparison.Ordinal));
        Check("清单/每个占位符都在字典里有值",
            Array.TrueForAll(NotifyTemplate.Placeholders, p => v.ContainsKey(p)));
        Check("清单/示例能被完整填充",
            !NotifyTemplate.Render(NotifyTemplate.Sample, v).Contains('{'));

        Console.WriteLine($"RESULT template fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>跑完动作 DRYRUN：关机只写日志。</summary>
    private static int RunAfterRun()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- v1.15 / L5：ShouldRun 的判定（纯函数） ----
        Check("判定/仅通知不做", !AfterRunActionService.ShouldRun("Notify", false, false, false));
        Check("判定/null 视作仅通知", !AfterRunActionService.ShouldRun(null, false, false, false));
        Check("判定/关机正常跑完会做", AfterRunActionService.ShouldRun("Shutdown", false, false, false));
        Check("判定/关机有失败不做", !AfterRunActionService.ShouldRun("Shutdown", true, false, false));
        Check("判定/关机有失败但勾了照样做", AfterRunActionService.ShouldRun("Shutdown", true, false, true));
        Check("判定/睡眠等同于关机待遇",
            !AfterRunActionService.ShouldRun("Sleep", true, false, false)
            && AfterRunActionService.ShouldRun("Sleep", false, false, false));
        Check("判定/被停止一律不做", !AfterRunActionService.ShouldRun("Shutdown", false, true, true));

        // 再跑一轮 / 打开程序不是破坏性动作，失败时也该做
        Check("判定/再跑一轮有失败也做", AfterRunActionService.ShouldRun("RunAgain", true, false, false));
        Check("判定/打开程序有失败也做", AfterRunActionService.ShouldRun("OpenApp", true, false, false));
        Check("判定/再跑一轮被停止不做", !AfterRunActionService.ShouldRun("RunAgain", false, true, false));

        Check("判定/关机是破坏性", AfterRunActionService.IsDestructive("Shutdown"));
        Check("判定/睡眠是破坏性", AfterRunActionService.IsDestructive("sleep"));
        Check("判定/再跑一轮不是破坏性", !AfterRunActionService.IsDestructive("RunAgain"));
        Check("判定/打开程序不是破坏性", !AfterRunActionService.IsDestructive("OpenApp"));
        Check("判定/仅通知不是破坏性", !AfterRunActionService.IsDestructive("Notify"));

        // ---- 原有 DRYRUN：关机只写日志，不真关 ----
        AppState.Settings.AfterRunAction = "Shutdown";
        AppState.Settings.AfterRunDryRun = true;
        AppState.Settings.AfterRunOnFailure = false;
        AfterRunActionService.Execute(new AfterRunContext { Total = 2, Success = 2, HasFailure = false });
        AfterRunActionService.Execute(new AfterRunContext { Total = 2, Success = 1, HasFailure = true });

        // ---- OpenApp：路径不存在时不该抛、只写日志 ----
        AppState.Settings.AfterRunAction = "OpenApp";
        AppState.Settings.AfterRunAppPath = Path.Combine(Path.GetTempPath(), "db_afterrun_不存在.exe");
        bool threw = false;
        try { AfterRunActionService.Execute(new AfterRunContext { Total = 1, Success = 1 }); }
        catch { threw = true; }
        Check("打开程序/路径不存在不抛", !threw);

        // 空路径同理
        AppState.Settings.AfterRunAppPath = "";
        threw = false;
        try { AfterRunActionService.Execute(new AfterRunContext { Total = 1, Success = 1 }); }
        catch { threw = true; }
        Check("打开程序/空路径不抛", !threw);

        AppState.Settings.AfterRunAction = "Notify";

        // ---- 界面：五个选项都在（下拉收起时看不到，只能查 XAML） ----
        string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("界面/有仅通知", mainXaml.Contains("Tag=\"Notify\"", StringComparison.Ordinal));
        Check("界面/有再跑一轮", mainXaml.Contains("Tag=\"RunAgain\"", StringComparison.Ordinal)
                                 && mainXaml.Contains("Content=\"再跑一轮\"", StringComparison.Ordinal));
        Check("界面/有打开程序", mainXaml.Contains("Tag=\"OpenApp\"", StringComparison.Ordinal)
                                 && mainXaml.Contains("Content=\"打开程序\"", StringComparison.Ordinal));
        Check("界面/有自动睡眠", mainXaml.Contains("Tag=\"Sleep\"", StringComparison.Ordinal));
        Check("界面/有自动关机", mainXaml.Contains("Tag=\"Shutdown\"", StringComparison.Ordinal));
        Check("界面/程序路径行默认收起", mainXaml.Contains("x:Name=\"AfterRunAppRow\"", StringComparison.Ordinal)
                                        && mainXaml.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal));

        Console.WriteLine($"RESULT afterrun fails={fails.Count}（应只有 DRYRUN 日志，无真实关机）");
        return fails.Count == 0 ? 0 : 1;
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
            "view.preflight", "view.presets", "view.notify",
            "view.handover",
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
    /// v1.15 / L4：配置组「常用」。
    /// 排序、开关、收敛三件事都在 <see cref="GroupFavorites"/> 里，全是纯函数。
    /// 重点验两件事：<b>排序必须稳定</b>（没收藏的相对顺序不能被打乱），
    /// 以及<b>大小写不能当成不同的组</b>（BGI 那边同名不同大小写的情况真出现过）。
    /// </summary>
    private static int RunFavorites()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var groups = new[] { "日常", "活动", "收尾", "周本", "深渊" };

        // ---- 1) Sort：没收藏时原样返回 ----
        var same = GroupFavorites.Sort(groups, Array.Empty<string>());
        Check("排序/无收藏原样", same.SequenceEqual(groups), string.Join(",", same));
        Check("排序/null 收藏原样", GroupFavorites.Sort(groups, null).SequenceEqual(groups));
        Check("排序/null 输入返回空", GroupFavorites.Sort(null!, new[] { "日常" }).Count == 0);

        // ---- 2) Sort：收藏的排前面，其余保持原顺序（稳定） ----
        // 收藏之间也按原顺序排，不按右键先后排——因为没有拖拽，
        // 「先收藏的在前」这种顺序用户改不了也看不懂，原顺序至少是可预测的。
        var sorted = GroupFavorites.Sort(groups, new[] { "深渊", "活动" });
        Check("排序/收藏在前", sorted.Take(2).OrderBy(x => x).SequenceEqual(new[] { "深渊", "活动" }.OrderBy(x => x)),
            string.Join(",", sorted));
        Check("排序/收藏按原序不按右键序", sorted.Take(2).SequenceEqual(new[] { "活动", "深渊" }),
            string.Join(",", sorted));
        Check("排序/未收藏保持原序", sorted.Skip(2).SequenceEqual(new[] { "日常", "收尾", "周本" }), string.Join(",", sorted));
        Check("排序/总数不变", sorted.Count == groups.Length);

        // ---- 3) Sort：收藏里有不存在的组，不能凭空冒出来 ----
        var withGhost = GroupFavorites.Sort(groups, new[] { "已删除的组", "日常" });
        Check("排序/失效收藏不显示", !withGhost.Contains("已删除的组"), string.Join(",", withGhost));
        Check("排序/有效收藏仍生效", withGhost[0] == "日常", string.Join(",", withGhost));

        // ---- 4) Sort：大小写不敏感 ----
        var cases = new[] { "Daily", "Abyss", "Weekly" };
        var caseSorted = GroupFavorites.Sort(cases, new[] { "abyss" });
        Check("排序/大小写不敏感", caseSorted[0] == "Abyss", caseSorted[0]);

        // ---- 5) Toggle ----
        var t1 = GroupFavorites.Toggle(Array.Empty<string>(), "日常");
        Check("开关/新增一条", t1.Length == 1 && t1[0] == "日常", string.Join(",", t1));
        var t2 = GroupFavorites.Toggle(t1, "日常");
        Check("开关/再点一次取消", t2.Length == 0, string.Join(",", t2));
        var t3 = GroupFavorites.Toggle(new[] { "日常" }, " 日常 ");
        Check("开关/带空白也算同一个", t3.Length == 0, string.Join(",", t3));
        var t4 = GroupFavorites.Toggle(new[] { "日常" }, "");
        Check("开关/空名字忽略", t4.Length == 1 && t4[0] == "日常");
        var t5 = GroupFavorites.Toggle(new[] { "日常" }, null!);
        Check("开关/null 名字忽略", t5.Length == 1);
        var t6 = GroupFavorites.Toggle(new[] { "a", "b" }, "A");
        Check("开关/大小写不同算同一条", t6.Length == 1 && t6[0] == "b", string.Join(",", t6));

        // ---- 6) IsFavorite ----
        Check("判定/命中", GroupFavorites.IsFavorite(new[] { "日常" }, "日常"));
        Check("判定/大小写", GroupFavorites.IsFavorite(new[] { "Daily" }, "daily"));
        Check("判定/未命中", !GroupFavorites.IsFavorite(new[] { "日常" }, "活动"));
        Check("判定/空表", !GroupFavorites.IsFavorite(Array.Empty<string>(), "日常"));
        Check("判定/null 表", !GroupFavorites.IsFavorite(null, "日常"));

        // ---- 7) Prune：BGI 里删掉的组要从常用表里清掉 ----
        var pruned = GroupFavorites.Prune(new[] { "日常", "已删除", "活动" }, new[] { "日常", "活动", "收尾" });
        Check("收敛/去掉失效项", pruned.SequenceEqual(new[] { "日常", "活动" }), string.Join(",", pruned));
        Check("收敛/null 收藏返回空", GroupFavorites.Prune(null, groups).Length == 0);
        Check("收敛/全失效清空", GroupFavorites.Prune(new[] { "完全没有" }, groups).Length == 0);

        Console.WriteLine($"RESULT favorites fails={fails.Count}");
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
        // v1.17：系统区从 7 行变 8 行（「备份设置」）；v1.19 再变 9 行（「开跑前体检」）。
        // 这一条不是随手放行的数字：加行的人必须同时改这里，改的时候会想一想
        // 「这一行真的非加不可吗」——这就是它存在的意义。
        // 加行的人必须同时改这里——改的时候会想一想这一行真的非加不可吗。
        Check("系统区 10 行（v1.17 备份 + v1.19 体检 + v1.20 交接单）", sysRows == 10, sysRows.ToString());
        Check("系统区有交接单一行", main.Contains("BtnHandover"), "MainWindow.xaml");
        Check("系统区有备份一行",
            main.Contains("x:Name=\"BtnBackupNow\"", StringComparison.Ordinal)
            && main.Contains("x:Name=\"BtnBackupRestore\"", StringComparison.Ordinal)
            && main.Contains("x:Name=\"TbBackupHint\"", StringComparison.Ordinal));
        Check("系统区有体检一行",
            main.Contains("x:Name=\"BtnPreflight\"", StringComparison.Ordinal)
            && main.Contains("x:Name=\"TbPreflightHint\"", StringComparison.Ordinal));
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

        // v1.16：首次向导是每个新用户看到的第一个界面，之前它的路径框是白底黑字——
        // 代码里写了 Style="{StaticResource InputBox}"，但那是主窗的窗口级资源，
        // 向导窗口取不到就静默退回系统默认浅色。这类问题只有真渲染才看得见。
        string wizard = ReadRepoSources("Dragon Butler/FirstRunWindow.xaml");
        Check("向导有自己的 InputBox 样式",
            wizard.Contains("x:Key=\"InputBox\"", StringComparison.Ordinal));
        Check("向导输入框不是系统默认浅色",
            wizard.Contains("TargetType=\"TextBox\"", StringComparison.Ordinal)
            && wizard.Contains("{DynamicResource InsetBrush}", StringComparison.Ordinal));
        Check("向导勾选框自己画（不退回系统方框）",
            wizard.Contains("x:Key=\"WizCheck\"", StringComparison.Ordinal)
            && wizard.Contains("TargetType=\"CheckBox\"", StringComparison.Ordinal)
            && wizard.Contains("Data=\"M 3.5 8 L 6.6 11 L 12.4 5\"", StringComparison.Ordinal));
        Check("向导三步都有渲染用例", probe.Contains("\"wizard\"", StringComparison.Ordinal));

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
