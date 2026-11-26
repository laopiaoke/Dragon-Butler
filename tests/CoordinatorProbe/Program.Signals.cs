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

    /// <summary>
    /// v1.43 / 任务一 A：信号仲裁的行为回归。
    ///
    /// <para>
    /// 这一组不看源码里有没有某个字符串，而是真的构造一个 RunCoordinator、
    /// 用可控信号源一条一条把信号喂进去，看它最后停在哪个状态。
    /// 理由很直白：这次要封的坑是「崩溃被当成成功」，而那种错在源码里
    /// 看起来完全正常——`Move(Done, ...)` 那一行当年也长得一本正经。
    /// </para>
    /// </summary>
    private static int RunSignalArbiter()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        ToolProfile Profile(CompletionSignalKind primary, int grace = 5) => new()
        {
            ToolId = "signals",
            DisplayName = "信号探针",
            ExecutablePath = "x.exe",
            PrimarySignal = primary,
            MaxDurationMinutes = 30,
            PostExitGraceMinutes = grace,
        };

        // 不启动真进程：LaunchSucceeded 只需要一个 Process 句柄做日志里的 pid，
        // 传 null 时 SafePid 会兜住返回 -1，状态机照常进 Running。
        ToolRunContext Ctx(ToolProfile p) => new() { Profile = p, Arguments = "" };

        ToolRunState Drive(ToolProfile p, params CompletionSignal[] signals)
        {
            using var coord = new RunCoordinator(p, _ => { });
            var src = new FakeSource();
            coord.SetPrimary(src);
            coord.BeginLaunch();
            coord.LaunchSucceeded(null!, Ctx(p));
            foreach (var s in signals) src.Fire(s);
            return coord.State;
        }

        static CompletionSignal Sig(CompletionSignalKind kind, int? exit = null, string source = "probe") => new()
        {
            Kind = kind,
            ExitCode = exit,
            SourceId = source,
            Confidence = SignalConfidence.High,
            Detail = "probe",
        };

        // ① 主信号是日志锚点，进程却崩了：这是最要命的一条。
        Check("仲裁/崩溃退出判失败",
            Drive(Profile(CompletionSignalKind.LogPattern), Sig(CompletionSignalKind.ProcessExit0, 3, "process-exit"))
                == ToolRunState.Failed);

        // ② 主信号先到、随后发现进程非零退出：不能翻成 Done。
        Check("仲裁/主信号后崩溃不翻完成",
            Drive(Profile(CompletionSignalKind.LogPattern),
                  Sig(CompletionSignalKind.LogPattern),
                  Sig(CompletionSignalKind.ProcessExit0, 5, "process-exit"))
                == ToolRunState.Failed);

        // ③ 锁存之后再来副信号，也不许授权完成。
        Check("仲裁/锁存后副信号不翻完成",
            Drive(Profile(CompletionSignalKind.LogPattern),
                  Sig(CompletionSignalKind.ProcessExit0, 9, "process-exit"),
                  Sig(CompletionSignalKind.GameProcessExit, null, "game-exit"))
                == ToolRunState.Failed);

        // ④ 正常路径不能被误伤：主信号 + 零退出 = 完成。
        Check("仲裁/主信号加零退出算完成",
            Drive(Profile(CompletionSignalKind.LogPattern),
                  Sig(CompletionSignalKind.LogPattern),
                  Sig(CompletionSignalKind.ProcessExit0, 0, "process-exit"))
                == ToolRunState.Done);

        // ⑤ 主信号本身就是进程退出时，零退出直接完成。
        Check("仲裁/零退出主信号算完成",
            Drive(Profile(CompletionSignalKind.ProcessExit0), Sig(CompletionSignalKind.ProcessExit0, 0, "process-exit"))
                == ToolRunState.Done);

        // ⑥ 只有零退出、主信号还没来：按宁等勿进转 Resolving，不许 Done。
        Check("仲裁/只有零退出先等主信号",
            Drive(Profile(CompletionSignalKind.LogPattern), Sig(CompletionSignalKind.ProcessExit0, 0, "process-exit"))
                == ToolRunState.Resolving);

        // ⑦ 游戏进程消失不能单独证明任务成功。
        Check("仲裁/游戏退出不单独算完成",
            Drive(Profile(CompletionSignalKind.LogPattern), Sig(CompletionSignalKind.GameProcessExit, null, "game-exit"))
                == ToolRunState.Running);

        // ⑧ 宽限为 0 时主信号到了也只能悬置。
        Check("仲裁/宽限为零就悬置",
            Drive(Profile(CompletionSignalKind.Webhook, grace: 0), Sig(CompletionSignalKind.Webhook))
                == ToolRunState.Halted);

        Console.WriteLine("RESULT signals fails=" + fails.Count);
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>可手动点火的信号源：由测试决定什么时候抛哪一种信号。</summary>
    private sealed class FakeSource : ICompletionSignalSource
    {
        public string SourceId => "probe";
        public event Action<CompletionSignal>? SignalRaised;
        public void Start(ToolRunContext ctx) { }
        public void Stop() { }
        public void Fire(CompletionSignal signal) => SignalRaised?.Invoke(signal);
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

}
