using System;
using System.Collections.Generic;

namespace DragonButler;

/// <summary>
/// 实时状态快照（v1.13）：把「现在到底在跑什么、跑了多久、大概几点收工」算成一句话。
///
/// <para>
/// 为什么单独一个文件：这几件事以前散在三处——队列知道当前工具（<see cref="QueueService.CurrentToolId"/>），
/// 哨兵知道当前配置组（<see cref="SentinelHealth.CurrentGroup"/>），归档知道历史耗时
/// （<see cref="GroupArchive.MedianMinutes"/>），但没人把它们拼起来给用户看。
/// 拼法本身是纯计算，抽成静态方法就能在探针里喂假数据断言，不用真的跑一局游戏。
/// </para>
/// <para>
/// 刻意不做的事：不预测「还剩几个组」「整个一条龙几点完」——那需要知道队列里每一项要跑多久，
/// 而我们只对 BGI 配置组有历史样本，别的工具没有。算不准的预计比没有预计更糟。
/// </para>
/// </summary>
public static class LiveStatus
{
    /// <summary>界面上要用的全部字段，一次算完。</summary>
    public sealed record Snapshot(
        bool Running,
        string ToolName,
        string GroupName,
        TimeSpan ToolElapsed,
        TimeSpan? GroupElapsed,
        double? MedianMinutes,
        DateTime? EtaAt,
        string Headline,
        string Detail)
    {
        /// <summary>主行：当前组名（或工具名）+ 已跑多久。</summary>
        public bool HasGroup => !string.IsNullOrWhiteSpace(GroupName);
    }

    /// <summary>
    /// 汇总实时状态。
    /// </summary>
    /// <param name="running">队列是否在跑。</param>
    /// <param name="toolName">当前工具的显示名（空 = 不知道）。</param>
    /// <param name="groupName">当前正在执行的 BGI 配置组（空 = 还没进组，或者不是 BGI）。</param>
    /// <param name="toolStartedAt">当前工具开始时刻；null = 未在跑。</param>
    /// <param name="groupStartedAt">当前组开始时刻；null = 该组没有当天的开始记录。</param>
    /// <param name="medianMinutes">该组的历史中位耗时（分钟）；null = 样本不足。</param>
    /// <param name="now">当前时刻（参数化，便于断言）。</param>
    public static Snapshot Build(
        bool running,
        string? toolName,
        string? groupName,
        DateTime? toolStartedAt,
        DateTime? groupStartedAt,
        double? medianMinutes,
        DateTime now)
    {
        string tool = (toolName ?? "").Trim();
        string group = (groupName ?? "").Trim();

        TimeSpan toolElapsed = toolStartedAt.HasValue ? NonNegative(now - toolStartedAt.Value) : TimeSpan.Zero;
        TimeSpan? groupElapsed = groupStartedAt.HasValue ? NonNegative(now - groupStartedAt.Value) : (TimeSpan?)null;

        DateTime? eta = groupStartedAt.HasValue && medianMinutes.HasValue && medianMinutes.Value > 0
            ? groupStartedAt.Value.AddMinutes(medianMinutes.Value)
            : (DateTime?)null;

        var snap = new Snapshot(
            running, tool, group, toolElapsed, groupElapsed, medianMinutes, eta,
            Headline: Headline(running, tool, group, toolElapsed, groupElapsed),
            Detail: Detail(running, groupElapsed, medianMinutes, eta, now));

        return snap;
    }

    /// <summary>主行：带组名时报组名，否则报工具名；都没报就只说「正在跑」。</summary>
    public static string Headline(bool running, string? toolName, string? groupName, TimeSpan toolElapsed, TimeSpan? groupElapsed)
    {
        if (!running) return "空闲";

        string tool = (toolName ?? "").Trim();
        string group = (groupName ?? "").Trim();

        if (group.Length > 0)
        {
            // 组在跑的时候，用户关心的是「哪个组」，不是「哪个工具」——
            // 工具名此时几乎恒定，说了等于没说。
            string tail = groupElapsed.HasValue ? " · " + Short(groupElapsed.Value) : "";
            return group + tail;
        }
        if (tool.Length > 0) return tool + (toolElapsed > TimeSpan.Zero ? " · " + Short(toolElapsed) : "");
        return "正在执行";
    }

    /// <summary>
    /// 次行：一句「还要多久」。没有样本 → 空字符串（界面上就不显示，不占位、不写「未知」）。
    /// </summary>
    public static string Detail(bool running, TimeSpan? groupElapsed, double? medianMinutes, DateTime? etaAt, DateTime now)
    {
        if (!running) return "";
        if (medianMinutes == null || medianMinutes.Value <= 0) return "";
        if (groupElapsed == null || etaAt == null) return "";

        var remaining = etaAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            // 已经跑到中位数以后了。此时说「预计 xx:xx 结束」是在骗人（那个点在过去），
            // 但也不该装作没事——把「往常多久」摆出来，让用户自己判断要不要等。
            return "已超过往常（通常 " + Minutes(medianMinutes.Value) + "）";
        }
        return "还要 " + Short(remaining) + " · 预计 " + etaAt.Value.ToString("HH:mm") + " 结束";
    }

    /// <summary>
    /// 浮条用的短版次行。180 像素宽只放得下一行、十来个汉字，
    /// 「还要 2 分 · 预计 23:03 结束」这种完整句在那里会被截成半截——
    /// 截断的预计比没有预计更让人看不明白，所以这里换成一句说得完的。
    ///
    /// <para>
    /// 分档的理由：等几分钟的时候人关心「还要几分钟」，等一个多小时的时候
    /// 「还要 84 分」这种说法反而要心算，不如直接给钟点。
    /// </para>
    /// </summary>
    public static string Brief(Snapshot s, DateTime now)
    {
        if (!s.Running) return "";
        if (s.MedianMinutes == null || s.MedianMinutes.Value <= 0) return "";
        if (s.GroupElapsed == null || s.EtaAt == null) return "";

        // 已经过了中位数时刻：这时给几点结束是错的信息（那个点在过去）。
        if (s.EtaAt.Value - now <= TimeSpan.Zero) return "已超往常";

        var remaining = s.EtaAt.Value - now;
        return remaining.TotalHours < 1
            ? "还要 " + Short(remaining)
            : "约 " + s.EtaAt.Value.ToString("HH:mm") + " 完";
    }

    /// <summary>工具提示用：把能解释的都摆出来，包括「通常多久」。</summary>
    public static string Tooltip(Snapshot s, int done, int total)
    {
        var lines = new List<string>();
        if (s.Running)
        {
            lines.Add("队列 " + done + "/" + total);
            if (s.ToolName.Length > 0) lines.Add("当前工具：" + s.ToolName + "（已跑 " + Short(s.ToolElapsed) + "）");
            if (s.HasGroup) lines.Add("当前配置组：" + s.GroupName);
            if (s.GroupElapsed.HasValue) lines.Add("本组已跑：" + Short(s.GroupElapsed.Value));
            lines.Add(s.MedianMinutes.HasValue
                ? "往常耗时：" + Minutes(s.MedianMinutes.Value) + "（近 14 天中位数）"
                : "往常耗时：样本不足，暂不预计");
            string d = s.Detail;
            if (d.Length > 0) lines.Add(d);
        }
        else
        {
            lines.Add("队列空闲。点「一键开跑」开始今天的顺序执行。");
            if (done > 0) lines.Add("今天已完成 " + done + " 项。");
        }
        return string.Join("\n", lines);
    }

    /// <summary>时长文案：12 秒 / 12 分 / 1 小时 12 分。秒级只在 1 分钟内出现，之后取整到分。</summary>
    public static string Short(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return (int)Math.Round(span.TotalSeconds) + " 秒";
        int totalMinutes = (int)Math.Round(span.TotalMinutes);
        if (totalMinutes < 60) return totalMinutes + " 分";
        int h = totalMinutes / 60;
        int m = totalMinutes % 60;
        return m == 0 ? h + " 小时" : h + " 小时 " + m + " 分";
    }

    private static string Minutes(double minutes)
        => Short(TimeSpan.FromMinutes(minutes));

    private static TimeSpan NonNegative(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;
}
