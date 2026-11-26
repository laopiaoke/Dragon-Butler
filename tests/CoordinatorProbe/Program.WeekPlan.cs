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
        // v1.43：把时长账本临时指到别处。
        //
        // 这条断言问的是「同一份结构化数据 → 界面和推送数出同一个数字」，
        // 不是在问「今天这台机器上一共跑了多少秒」。可账本是全局共享的：
        // 前面几组探针（queue / dag / stall…）跑完都会往今天这条里记一笔，
        // 于是合计会随时间漂——刚装好的机器上是 11520，跑过几轮之后变成 11524。
        // 那种漂不是回归，却会让这条断言变成「看运气」，所以先把账本隔离出去。
        string? ledgerBackup = DurationLedger.PathOverride;
        string tempLedger = Path.Combine(Path.GetTempPath(), "probe-durations-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        DurationLedger.PathOverride = tempLedger;
        DurationLedger.Load();
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
        // v1.41 / M5-7：日报改成单一生成器，摘要不再由 MorningReportService 自己数。
        // 这里断言的是「同一份结构化数据 → 界面与推送渲染出同一组数字」，所以直接查 DailyReport。
        var report = DailyReport.Build(DateTime.Today.AddDays(-1));
        string summary = $"{report.Groups.Count} 个组 · {report.ToolCount} 个工具 · {DailyReport.SayDuration(report.TotalSeconds)}";
        Console.WriteLine("MORNING summary=" + summary);

        AppState.Settings.MorningReportEnabled = true;
        AppState.Settings.MorningReportDate = "";
        int before = CountLog("[日报]");
        MorningReportService.MaybeShow();
        int after1 = CountLog("[日报]");
        string date1 = AppState.Settings.MorningReportDate;
        MorningReportService.MaybeShow();
        int after2 = CountLog("[日报]");
        Console.WriteLine($"MORNING first={after1 - before} second={after2 - after1} date={date1}");

        // 无运行日：清空归档，开启且未播报 → 不产生 [晨报]
        SaveArchive(new Dictionary<string, List<GroupRecord>>());
        GroupArchive.Load();
        AppState.Settings.MorningReportDate = "";
        int beforeEmpty = CountLog("[日报]");
        MorningReportService.MaybeShow();
        int afterEmpty = CountLog("[日报]");
        Console.WriteLine($"MORNING empty={afterEmpty - beforeEmpty} dateAfterEmpty={AppState.Settings.MorningReportDate}");

        // 归档里两组：A 01:00→02:30 = 90 分 = 5400 秒，B 02:30→04:12 = 102 分 = 6120 秒。
        // 工具账本没有记录，所以工具数是 0，合计从组归档那一侧补上（DailyReport 的内置回退）。
        // 报告已经按隔离账本算完，这里把路径还回去，别影响后面的组。
        DurationLedger.PathOverride = ledgerBackup;
        try { if (File.Exists(tempLedger)) File.Delete(tempLedger); } catch { }
        DurationLedger.Load();
        bool ok = defaultOff
                  && report.Groups.Count == 2
                  && report.TotalSeconds == 5400 + 6120
                  && report.Groups.Any(g => g.Name == "A" && g.Seconds == 5400)
                  && report.Groups.Any(g => g.Name == "B" && g.Seconds == 6120)
                  && after1 - before == 1 && after2 == after1
                  && afterEmpty == beforeEmpty;
        Console.WriteLine("RESULT morning " + (ok ? "PASS" : "FAIL"));
        return ok ? 0 : 1;
    }

}
