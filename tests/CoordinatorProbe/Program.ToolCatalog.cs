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
        Check("目录/异环", ToolCatalog.Find("ok-nte") != null);
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
        // ok-nte 是 v1.24 唯一「从自填升级成查到了」的工具：官方 quick-start 就写着
        // `ok-nte.exe -t 2 -e`，而且 ok-script 在 GUI 模式下也认 -t（主窗起来后自动开跑）。
        var nte = ToolCatalog.Find("ok-nte")!;
        Check("异环/游戏名不再是无限暖暖", nte.GameName == "异环", nte.GameName);
        Check("异环/进程名是 HTGame", nte.GameProcessNames.Contains("HTGame"),
            string.Join(",", nte.GameProcessNames));
        Check("异环/默认带 -t 2", nte.DefaultArguments.Contains("-t 2"), nte.DefaultArguments);
        Check("异环/默认跑完退出", nte.DefaultArguments.Contains("-e"), nte.DefaultArguments);
        Check("异环/有一条已证实参数", nte.Args.Any(a => a.Confidence == ArgConfidence.Documented
                                                        && a.Value.Contains("-t 2")));
        Check("异环/仍然留着「只开界面」这一档", nte.Args.Any(a => a.Value.Length == 0));

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

        // ---- 13b) 档案兜底：目录里有的工具必须拿到一份真档案（v1.23） ----
        // v1.22 出过这个漏：引导页列了 16 个工具，档案表里只有内置三个，
        // 用户勾了 BAAH 之后名字显示成裸 id「baah」，自动寻找也因为进程名为空永远失败。
        var baahProfile = ProfileRegistry.Get("baah");
        Check("兜底/名字是中文名不是裸 id", baahProfile.DisplayName == "BAAH", baahProfile.DisplayName);
        Check("兜底/有进程名（自动寻找要用）", !string.IsNullOrWhiteSpace(baahProfile.ManagedProcessName),
            baahProfile.ManagedProcessName);
        Check("兜底/有最长时长", baahProfile.MaxDurationMinutes > 0, baahProfile.MaxDurationMinutes.ToString());
        Check("兜底/有完成判定", baahProfile.PrimarySignal == CompletionSignalKind.ProcessExit0,
            baahProfile.PrimarySignal.ToString());
        var m7aProfile = ProfileRegistry.Get("hsr-march7th");
        Check("兜底/命令行三月七有名字", m7aProfile.DisplayName.Contains("三月七"), m7aProfile.DisplayName);
        // 目录里没有的必须还是老实退化，不能凭空编一个
        var ghost = ProfileRegistry.Get("no-such-tool");
        Check("兜底/目录里没有的仍是裸 id", ghost.DisplayName == "no-such-tool", ghost.DisplayName);
        Check("兜底/不伪造进程名", string.IsNullOrWhiteSpace(ghost.ManagedProcessName));

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

}
