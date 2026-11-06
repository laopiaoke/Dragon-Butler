using System;
using System.Collections.Generic;

namespace DragonButler;

/// <summary>
/// 挂机日历的纯逻辑（v1.8 / F13）。
///
/// 抽出来的理由和 SchedulePlanner 一样：把「怎么记事」和「怎么画格子」分开，
/// 断言就能在没有窗口、没有桌面会话的 CI 里跑到全部判定分支。
/// </summary>
public static class CalendarModel
{
    /// <summary>方格墙横着放多少周。53 周 ≈ 一年零一周，正好一行铺满。</summary>
    public const int Weeks = 53;

    /// <summary>一天的状态：没跑 / 全部跑成 / 部分跑成 / 全没成。</summary>
    public enum DayLevel
    {
        Idle = 0,
        AllOk = 1,
        Partial = 2,
        Failed = 3,
    }

    /// <summary>
    /// 由当天的组记录判定状态。
    /// 「有记录但没有 End」按没跑成算——挂起、崩溃、中途被打断都长这样，
    /// 把它算成成功会让这面墙变成自我安慰。
    /// </summary>
    public static DayLevel LevelOf(IReadOnlyList<GroupRecord>? records)
    {
        if (records == null || records.Count == 0) return DayLevel.Idle;

        int finished = 0;
        foreach (var r in records)
            if (r != null && !string.IsNullOrEmpty(r.End)) finished++;

        if (finished == 0) return DayLevel.Failed;
        if (finished < records.Count) return DayLevel.Partial;
        return DayLevel.AllOk;
    }

    /// <summary>整面墙的第一天：把今天推到本周一，再往回退 Weeks-1 周，让每行永远是同一个星期几。</summary>
    public static DateTime FirstDay(DateTime today)
    {
        DateTime weekStart = today.Date.AddDays(-((int)today.DayOfWeek + 6) % 7);
        return weekStart.AddDays(-(Weeks - 1) * 7);
    }

    /// <summary>格子墙的说明文字。</summary>
    public static string Summary(int ranDays, int allOk, int partial, int failed)
    {
        if (ranDays <= 0) return "还没有任何挂机记录——第一次跑完之后这里就会有颜色。";
        return $"过去一年跑了 {ranDays} 天：全部成功 {allOk} 天，部分成功 {partial} 天，全没成 {failed} 天。";
    }

    /// <summary>单格的悬停提示。</summary>
    public static string TipFor(DateTime day, DayLevel level, int total, int finished)
    {
        string label = day.ToString("yyyy-MM-dd");
        return level switch
        {
            DayLevel.Idle => label + "：没跑",
            DayLevel.AllOk => $"{label}：{finished} 项全部跑成",
            DayLevel.Partial => $"{label}：{finished}/{total} 项跑成",
            _ => $"{label}：{total} 项，全部没跑成",
        };
    }
}
