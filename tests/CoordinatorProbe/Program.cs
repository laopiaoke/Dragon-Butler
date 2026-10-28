using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
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

        if (!File.Exists(stub))
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
            _ => Help(),
        };
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

    private static int Help()
    {
        Console.WriteLine("CoordinatorProbe <done|failed|timeout|halted|migrate> [stubPath]");
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

        BgiService.LoadSettings();
        ProfileRegistry.Initialize();

        var bgi = ProfileRegistry.BetterGi;
        Console.WriteLine("MIGRATE version=" + BgiService.Settings.Version);
        Console.WriteLine("MIGRATE bgiPath=" + bgi.ExecutablePath);
        Console.WriteLine("MIGRATE configName=" + bgi.CustomData.GetValueOrDefault("dragonConfigName"));
        Console.WriteLine("MIGRATE groups=" + bgi.CustomData.GetValueOrDefault("selectedGroups"));
        Console.WriteLine("MIGRATE schedule=" + BgiService.Settings.ScheduleEnabled + "/" + BgiService.Settings.ScheduleHour);
        Console.WriteLine("MIGRATE backup=" + File.Exists(backup));
        Console.WriteLine("MIGRATE toolsJson=" + File.Exists(Path.Combine(ProfileRegistry.ToolsDir, "bettergi.json")));
        Console.WriteLine("MIGRATE profiles=" + string.Join(",", System.Linq.Enumerable.Select(ProfileRegistry.All, p => p.ToolId)));
        return 0;
    }
}
