using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 某一天的队列概况（纯数据，供 <see cref="WeeklyReportService.BuildWeekly"/> 拼句）。
/// </summary>
public sealed record DayStat(DateTime Day, int Runs, int Finished, double Minutes)
{
    /// <summary>这天跑了没有。</summary>
    public bool Ran => Runs > 0;

    /// <summary>这天跑的都成了没有（没跑的日子不看这个）。</summary>
    public bool AllOk => Runs > 0 && Finished >= Runs;
}

/// <summary>
/// 周报（v1.7 / F8，默认关）：每周第一次启动时，用一句话汇报过去七天的队列情况。
///
/// 两条设计约束：
/// 1) 数据源只有 <see cref="GroupArchive"/>（零新增采集）——和晨报同源，不引入第二套统计口径；
/// 2) <see cref="BuildWeekly"/> 是纯函数，字符串直接可断言，不依赖当前时间、不依赖设置。
///    时间范围由调用方算好传进来，所以 Probe 能拿固定输入验固定输出。
/// </summary>
public static class WeeklyReportService
{
    /// <summary>周报覆盖的天数（含今天）。</summary>
    public const int WindowDays = 7;

    /// <summary>
    /// 启动时调用一次：开启、且今天还没播报过、且过去七天有记录 → 播报一条。
    /// 没有数据也记日期，避免每次启动都去算一遍归档。
    /// </summary>
    public static void MaybeShow()
    {
        try
        {
            var s = AppState.Settings;
            if (!s.WeeklyReportEnabled) return;

            string today = DateTime.Today.ToString("yyyy-MM-dd");
            if (string.Equals(s.WeeklyReportDate, today, StringComparison.Ordinal)) return;

            s.WeeklyReportDate = today;
            AppState.SaveSettings();

            var days = Collect();
            if (days.All(d => !d.Ran))
            {
                LogService.LogEvent("周报：过去七天无运行记录，跳过播报");
                return;
            }

            string text = BuildWeekly(days);
            LogService.LogEvent("[周报] " + text);
            NotifyService.Show(NotifyLevel.Info, "龙管家 · 周报", text);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("周报生成失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 收集过去 <see cref="WindowDays"/> 天（含今天）的逐日统计，按时间从早到晚。
    /// 只读归档，不改任何状态。
    /// </summary>
    public static List<DayStat> Collect(DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var days = new List<DayStat>(WindowDays);
        for (int i = WindowDays - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            days.Add(SummarizeDay(GroupArchive.GetDay(day), day));
        }
        return days;
    }

    /// <summary>把某一天的组归档压成一个 <see cref="DayStat"/>（纯函数）。</summary>
    public static DayStat SummarizeDay(IReadOnlyList<GroupRecord> records, DateTime day)
    {
        int runs = records.Count;
        int finished = records.Count(r => !string.IsNullOrWhiteSpace(r.End));

        double minutes = 0;
        foreach (var r in records)
        {
            var span = DurationOf(r);
            if (span.HasValue) minutes += span.Value.TotalMinutes;
        }

        return new DayStat(day.Date, runs, finished, minutes);
    }

    /// <summary>
    /// 把七天的统计拼成一句话（纯函数，Probe 直接断言）：
    /// 「本周队列：7 天里跑了 4 天，共 9 次运行，全部成功，总耗时 6小时18分，平均每次 42 分。跑得最多的是周三（3 次）。」
    /// </summary>
    public static string BuildWeekly(IReadOnlyList<DayStat> days)
    {
        if (days == null || days.Count == 0) return "本周队列：没有可统计的记录。";

        var active = days.Where(d => d.Ran).ToList();
        if (active.Count == 0) return "本周队列：一次都没跑。";

        int runs = active.Sum(d => d.Runs);
        int finished = active.Sum(d => d.Finished);
        double minutes = active.Sum(d => d.Minutes);

        // 成功率：只在有失败时才报具体数字——全成功时写「全部成功」更像人话
        string state = finished >= runs
            ? "全部成功"
            : $"成功 {finished} 次（{finished * 100 / runs}%）";

        // 平均每次：只按有耗时的运行数分摊，避免把没跑完的那些算进分母把均值压低
        int timed = active.Count(d => d.Minutes > 0);
        string average = timed > 0
            ? $"，平均每次 {MorningReportService.FormatDuration(minutes / timed)}"
            : "";

        string busiest = BusiestDay(active);

        // 「7 天里跑了 2 天」在全勤那天读着别扭，全勤就直接说「每天都跑了」
        string span = active.Count >= days.Count
            ? "每天都跑了"
            : $"{days.Count} 天里跑了 {active.Count} 天";

        return $"本周队列：{span}，共 {runs} 次运行，{state}，"
             + $"总耗时 {MorningReportService.FormatDuration(minutes)}{average}。{busiest}";
    }

    /// <summary>
    /// 跑得最多的那天。并列时取较早的一天（稳定、可断言，不随字典顺序漂移）。
    /// </summary>
    private static string BusiestDay(IReadOnlyList<DayStat> active)
    {
        var top = active[0];
        foreach (var d in active)
        {
            if (d.Runs > top.Runs) top = d;
        }

        if (top.Runs <= 1) return "每天都只跑了一次。";
        return $"跑得最多的是{WeekdayName(top.Day)}（{top.Runs} 次）。";
    }

    private static string WeekdayName(DateTime day) => ((int)day.DayOfWeek) switch
    {
        0 => "周日",
        1 => "周一",
        2 => "周二",
        3 => "周三",
        4 => "周四",
        5 => "周五",
        _ => "周六",
    };

    /// <summary>单条记录的耗时（跨零点按一天补），与晨报同一套算法。</summary>
    private static TimeSpan? DurationOf(GroupRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.Start) || string.IsNullOrWhiteSpace(r.End)) return null;
        if (!TimeSpan.TryParse(r.Start, out var start) || !TimeSpan.TryParse(r.End, out var end)) return null;
        var span = end - start;
        if (span < TimeSpan.Zero) span += TimeSpan.FromDays(1); // 跨零点
        return span;
    }
}
