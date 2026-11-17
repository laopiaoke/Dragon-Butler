using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 把已有存档读成基线样本（v1.20 / 奇思妙想 #3 的适配层）。
///
/// <para>
/// <see cref="Baseline"/> 刻意不碰磁盘；这个类只做一件事：从 <see cref="RunJournal"/> 与
/// <see cref="GroupArchive"/> 里把「过去 N 天每天是多少」数出来。两者都是本机已有的记录，
/// 不新增任何采集。
/// </para>
/// <para>
/// <b>为什么不数今天</b>：今天还没过完。拿半天的数据去比整天的基线，
/// 每天中午都会得出「今天特别少」这种废话。
/// </para>
/// </summary>
public static class BaselineFacts
{
    /// <summary>
    /// 过去 <paramref name="days"/> 天每天「没跑成的项数」。不含今天。
    /// 数据来自队列台账：每项每天一条覆盖记录，正好能数。
    /// </summary>
    public static List<double> DailyFailures(DateTime today, int days = Baseline.DefaultDays)
    {
        var list = new List<double>();
        for (int back = 1; back <= days; back++)
        {
            try
            {
                var entries = RunJournal.GetDay(today.AddDays(-back));
                if (entries.Count == 0) continue;   // 那天根本没跑，不是「0 项失败」
                int failed = entries.Count(e =>
                    !string.Equals(e.State, nameof(ToolRunState.Done), StringComparison.OrdinalIgnoreCase));
                list.Add(failed);
            }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// 过去 <paramref name="days"/> 天每天的配置组总分钟数。不含今天，跳过没跑的日子。
    /// </summary>
    public static List<double> DailyMinutes(DateTime today, int days = Baseline.DefaultDays)
    {
        var list = new List<double>();
        for (int back = 1; back <= days; back++)
        {
            try
            {
                var groups = GroupArchive.GetDay(today.AddDays(-back));
                double total = 0;
                foreach (var g in groups)
                {
                    var d = GroupArchive.DurationOf(g);
                    if (d.HasValue) total += d.Value.TotalMinutes;
                }
                if (total > 0) list.Add(total);
            }
            catch { }
        }
        return list;
    }

    /// <summary>今天到目前为止没跑成的项数。</summary>
    public static int TodayFailures(DateTime today)
    {
        try
        {
            return RunJournal.GetDay(today).Count(e =>
                !string.Equals(e.State, nameof(ToolRunState.Done), StringComparison.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }

    /// <summary>
    /// 今天「没跑成几项」是否异常。样本不足时返回空文案（界面据此不出这一行）。
    /// </summary>
    public static Baseline.Verdict Today(DateTime today, int days = Baseline.DefaultDays)
    {
        var history = DailyFailures(today, days);
        int now = TodayFailures(today);
        return Baseline.Judge(history, now, " 项", "没跑成的项");
    }
}
