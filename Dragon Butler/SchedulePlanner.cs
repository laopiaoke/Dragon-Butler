using System;

namespace DragonButler;

/// <summary>
/// 定时判定的结果。除了「这次跑不跑」，还要说明「为什么」和「这次是不是补跑」，
/// 这样界面和日志能直接引用，不需要再猜一遍。
/// </summary>
public sealed record ScheduleDecision(
    bool ShouldRun,
    string Reason,
    bool IsCatchUp,
    DateTime? DueAt,
    DateTime? NextDue);

/// <summary>
/// 定时判定器（v1.6 / F1）。
///
/// 旧实现（MainWindow.CheckSchedule）要求 <c>now.Hour == 计划时 &amp;&amp; now.Minute == 计划分</c>
/// 严格相等，且只记「日期」不记「哪个计划点」——电脑睡眠、程序没开、上一轮没结束时
/// 跨过那一整分钟，今天就此永久错过。
///
/// 这里把判定从 UI 线程抽出来做成纯函数，才能被 CoordinatorProbe 断言。
/// 语义：某个计划点 <c>due</c> 在 <c>[due, due + 补跑窗口]</c> 内、且该计划点之后还没跑过 → 该跑。
/// </summary>
public static class SchedulePlanner
{
    /// <summary>补跑窗口的默认值（分钟）。0 = 关闭补跑，退回精确匹配。</summary>
    public const int DefaultCatchUpMinutes = 120;

    /// <summary>补跑窗口上限（分钟）：一天只有 1440 分钟，再长就没有「今天」的意义了。</summary>
    public const int MaxCatchUpMinutes = 1440;

    /// <summary>
    /// 判定此刻该不该因为定时而启动队列。
    /// </summary>
    /// <param name="enabled">定时总开关。</param>
    /// <param name="vacation">度假模式（暂停所有定时）。</param>
    /// <param name="running">队列是否已在跑。</param>
    /// <param name="scheduleHour">计划时（0-23）。</param>
    /// <param name="scheduleMinute">计划分（0-59）。</param>
    /// <param name="weekdayMask">星期掩码：bit0=周一 … bit6=周日。</param>
    /// <param name="catchUpMinutes">补跑窗口（分钟，0=关闭）。</param>
    /// <param name="lastRunAt">上一次「因定时而跑」的计划点（UTC 无关，本地时间）；null=从没跑过。</param>
    /// <param name="now">当前本地时间（显式传入，便于断言）。</param>
    public static ScheduleDecision Decide(
        bool enabled,
        bool vacation,
        bool running,
        int scheduleHour,
        int scheduleMinute,
        int weekdayMask,
        int catchUpMinutes,
        DateTime? lastRunAt,
        DateTime now)
    {
        if (!enabled) return No("定时未开启");
        if (vacation) return No("度假模式生效");
        if (running) return No("队列正在运行");

        int mask = weekdayMask & 0x7F;
        if (mask == 0) return No("未选择任何星期");

        // 往前找最近一个「落在勾选星期上、且已经到点」的计划点（最多回看 8 天，覆盖跨周边界）。
        DateTime? due = null;
        for (int back = 0; back < 8; back++)
        {
            var day = now.Date.AddDays(-back);
            int bit = WeekdayBit(day);
            if ((mask & (1 << bit)) == 0) continue;
            var t = day.AddHours(ClampHour(scheduleHour)).AddMinutes(ClampMinute(scheduleMinute));
            if (t > now) continue;
            due = t;
            break;
        }

        if (due == null)
        {
            // 还没到过最近一个计划点：下次到点就是未来那个。
            return new ScheduleDecision(false, "未到计划时间", false, null, NextDueAfter(mask, scheduleHour, scheduleMinute, now));
        }

        int window = Math.Clamp(catchUpMinutes, 0, MaxCatchUpMinutes);

        // 窗口的终点。窗口为 0 时退化成「只认计划点所在的那一分钟」：
        // 起点固定是 due，终点定在 due+1 分钟，于是 [due, due+1) 之外一律不跑。
        var windowEnd = window == 0 ? due.Value.AddMinutes(1) : due.Value.AddMinutes(window);
        string dueText = due.Value.Date == now.Date ? due.Value.ToString("HH:mm") : due.Value.ToString("MM-dd HH:mm");

        if (now >= windowEnd)
        {
            // 过期了。措辞要同时交代三件事：上一个计划点是什么时候、为什么不算它、下次是什么时候。
            var nextDue = NextDueAfter(mask, scheduleHour, scheduleMinute, now);
            string nextText = nextDue == null ? "" : $"，下次 {nextDue.Value:MM-dd HH:mm}";
            string why = window == 0
                ? $"已过计划时间（补跑已关闭，计划 {dueText}）{nextText}"
                : $"上一个计划点 {dueText} 已过期（补跑窗口 {window} 分钟）{nextText}";
            return new ScheduleDecision(false, why, false, due, nextDue);
        }

        // 这个计划点跑过了吗？只要上次因定时而跑的计划点 >= 本次 due，就算跑过。
        if (lastRunAt != null && lastRunAt.Value >= due.Value)
        {
            return new ScheduleDecision(false, "该计划点已执行过", false, due, NextDueAfter(mask, scheduleHour, scheduleMinute, now));
        }

        bool isCatchUp = window > 0 && now >= due.Value.AddMinutes(1);
        string reason = isCatchUp
            ? $"补跑（计划 {dueText}，实际 {now:HH:mm}）"
            : $"到点（计划 {dueText}）";
        return new ScheduleDecision(true, reason, isCatchUp, due, null);
    }

    /// <summary>严格大于 now 的下一个计划点（界面上「距下次执行」用）。</summary>
    public static DateTime? NextDueAfter(int weekdayMask, int scheduleHour, int scheduleMinute, DateTime now)
    {
        int mask = weekdayMask & 0x7F;
        if (mask == 0) return null;
        for (int i = 0; i < 8; i++)
        {
            var day = now.Date.AddDays(i);
            int bit = WeekdayBit(day);
            if ((mask & (1 << bit)) == 0) continue;
            var t = day.AddHours(ClampHour(scheduleHour)).AddMinutes(ClampMinute(scheduleMinute));
            if (t > now) return t;
        }
        return null;
    }

    /// <summary>今天星期对应掩码位号：周一=0 … 周日=6。与 <see cref="QueueService.WeekdayBit"/> 同义。</summary>
    public static int WeekdayBit(DateTime day) => ((int)day.DayOfWeek + 6) % 7;

    private static int ClampHour(int h) => Math.Clamp(h, 0, 23);

    private static int ClampMinute(int m) => Math.Clamp(m, 0, 59);

    private static ScheduleDecision No(string reason)
        => new(false, reason, false, null, null);

    /// <summary>补跑窗口的中文描述（界面与日志共用一句，避免两处措辞不一致）。</summary>
    public static string WindowText(int catchUpMinutes)
    {
        int w = Math.Clamp(catchUpMinutes, 0, MaxCatchUpMinutes);
        if (w == 0) return "补跑已关闭";
        if (w < 60) return $"错过 {w} 分钟内会补跑";
        if (w % 60 == 0) return $"错过 {w / 60} 小时内会补跑";
        return $"错过 {w} 分钟内会补跑";
    }
}
