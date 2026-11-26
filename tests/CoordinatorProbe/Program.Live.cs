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

}
