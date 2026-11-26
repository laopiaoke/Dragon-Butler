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
    private static int RunAfterRun(string stub)
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

        // ===== v1.43 / 任务一 B + 任务二：收尾授权与关机状态 =====
        //
        // 六参这条路线是任务一的收口：原来只有「有没有失败、有没有被停」两个维度，
        // 于是「一项都没跑」和「依赖没成整队中止」这两种都落进了「没失败」的桶里，
        // 关机照做。下面这几条把这两个桶单独钉住。
        Check("判定/全跳过不关机", !AfterRunActionService.ShouldRun("Shutdown", false, false, false,
            nothingRan: true, dependencyAbort: false));
        Check("判定/全跳过不睡眠", !AfterRunActionService.ShouldRun("Sleep", false, false, false,
            nothingRan: true, dependencyAbort: false));
        Check("判定/全跳过照样发通知", !AfterRunActionService.ShouldRun("Notify", false, false, false,
            nothingRan: true, dependencyAbort: false));
        Check("判定/全跳过后台仍可再跑一轮", AfterRunActionService.ShouldRun("RunAgain", false, false, false,
            nothingRan: true, dependencyAbort: false));
        Check("判定/依赖中折算作失败", !AfterRunActionService.ShouldRun("Shutdown", false, false, false,
            nothingRan: false, dependencyAbort: true));
        Check("判定/依赖中止但勾了失败也跑", AfterRunActionService.ShouldRun("Shutdown", false, false, true,
            nothingRan: false, dependencyAbort: true));
        Check("判定/依赖中止仍可打开程序", AfterRunActionService.ShouldRun("OpenApp", false, false, false,
            nothingRan: false, dependencyAbort: true));
        Check("判定/被停止优先于依赖中止", !AfterRunActionService.ShouldRun("Shutdown", false, true, true,
            nothingRan: false, dependencyAbort: true));

        // 关机状态的生命周期：倒计时结束后界面不能永远留着「取消关机」
        Check("关机状态/默认不在待机", !QueueService.IsShutdownPending);
        Check("关机状态/过期复位不报错", !QueueService.ExpireShutdownPendingIfStale());
        Check("关机状态/没在待机时取消返回 false", !QueueService.CancelShutdown());

        // ---- 原有 DRYRUN：关机只写日志，不真关 ----
        AppState.Settings.AfterRunAction = "Shutdown";
        AppState.Settings.AfterRunDryRun = true;
        AppState.Settings.AfterRunOnFailure = false;
        AfterRunActionService.Execute(new AfterRunContext { Total = 2, Success = 2, HasFailure = false });
        AfterRunActionService.Execute(new AfterRunContext { Total = 2, Success = 1, HasFailure = true });

        // 走真实队列收尾，用 DRYRUN 观察是否会授权电源动作。
        var s = AppState.Settings;
        s.KeepAwakeEnabled = false;
        var profile = Stub("probe-afterrun", stub, "--seconds 1 --exit 0");
        string log = Path.Combine(LogService.Dir, DateTime.Now.ToString("yyyy-MM-dd") + "_运行.log");
        int CountLog(string text) => File.ReadLines(log).Count(line => line.Contains(text, StringComparison.Ordinal));
        int powerActions = CountLog("DRYRUN: 将执行");
        foreach (string action in new[] { "Sleep", "Shutdown" })
        {
            s.AfterRunAction = action;
            s.AfterRunOnFailure = true;
            QueueService.ToggleSkipOnce(profile.ToolId);
            QueueService.RunProfilesAsync(new[] { profile }, QueueTrigger.Manual).GetAwaiter().GetResult();
            Check("实际收尾/全跳过不授权 " + action, CountLog("DRYRUN: 将执行") == powerActions);
        }

        s.AfterRunAction = "RunAgain";
        s.Queue = new() { new QueueEntry { ToolId = profile.ToolId, Enabled = true } };
        ProfileRegistry.Save(profile);
        int reruns = CountLog("跑完动作：再跑一轮");
        Check("实际收尾/补跑走完正常队列", QueueService.StartRerunAsync().GetAwaiter().GetResult()
            && QueueService.LastResults.Any(r => r.State == ToolRunState.Done));
        Check("实际收尾/补跑不会再排下一轮", CountLog("跑完动作：再跑一轮") == reruns);

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

}
