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

}
