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

}
