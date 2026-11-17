using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 它会在你不注意的时候变好（v1.20 / 奇思妙想 #8）。
///
/// <para>
/// <b>这是十条里唯一真的会改用户配置的一条，所以它默认关，而且改之前一定先问。</b>
/// 「自动调队列顺序」听起来无害，直到你发现昨晚跑的顺序和你设的不一样、
/// 而你正在找一个「为什么今天先跑了活动」的答案。
/// </para>
/// <para>
/// 所以这里的产物不是「已经做的改动」，而是<b>建议</b>：
/// 每条建议都带着它凭什么这么说（样本几天、差多少），用户点一下才生效。
/// </para>
/// <para>纯函数：只读样本、只出建议，不写设置。</para>
/// </summary>
public static class Suggest
{
    /// <summary>一条建议。<see cref="Key"/> 稳定供断言；<see cref="Apply"/> 是可执行的命令 id。</summary>
    public sealed record Idea(string Key, string Title, string Why, string Action, string Apply);

    public sealed record ToolStat(string ToolId, string Name, int Runs, int Failures, double AvgMinutes);

    public sealed record Input(
        IReadOnlyList<ToolStat>? Tools,
        int SampleDays,
        int NotifyCount30d,
        int NotifyOpenedCount,
        bool ScheduleEnabled,
        bool AwayEnabled,
        int GroupsNeverRun,
        int TotalGroups);

    /// <summary>少于这么多天的数据什么都不建议——「你昨天失败过一次所以换个顺序」是瞎猜。</summary>
    public const int MinDays = 7;

    /// <summary>失败率至少要高出这么多才值得提，否则天天弹「要不要换个顺序」。</summary>
    public const double FailGap = 0.20;

    /// <summary>通知点开率低于这个数，说明它在当噪音。</summary>
    public const double IgnoreRate = 0.15;

    public static IReadOnlyList<Idea> Build(Input input)
    {
        var list = new List<Idea>();
        if (input.SampleDays < MinDays) return list;

        // ---- 常失败的工具：建议往后挪 ----
        var tools = (input.Tools ?? new List<ToolStat>()).Where(t => t.Runs >= 3).ToList();
        if (tools.Count >= 2)
        {
            var worst = tools.OrderByDescending(t => t.Failures / (double)Math.Max(1, t.Runs)).First();
            var best = tools.OrderBy(t => t.Failures / (double)Math.Max(1, t.Runs)).First();
            double worstRate = worst.Failures / (double)Math.Max(1, worst.Runs);
            double bestRate = best.Failures / (double)Math.Max(1, best.Runs);
            if (worst.ToolId != best.ToolId && worstRate - bestRate >= FailGap && worstRate >= 0.3)
            {
                list.Add(new Idea("queue.order",
                    $"把「{worst.Name}」往后挪",
                    $"{input.SampleDays} 天里它跑了 {worst.Runs} 次，{worst.Failures} 次没成；"
                    + $"「{best.Name}」同期只有 {best.Failures} 次没成。把它排后面，前面几项就不会被它拖住。",
                    "view.queue",
                    "noop"));
            }
        }

        // ---- 通知没人看：建议只留本机 ----
        if (input.NotifyCount30d >= 10 && input.NotifyOpenedCount / (double)input.NotifyCount30d < IgnoreRate)
        {
            list.Add(new Idea("notify.noise",
                "提醒可能太吵了",
                $"近 30 天发了 {input.NotifyCount30d} 条，你点开过 {input.NotifyOpenedCount} 条。"
                + "可以改成只在本机弹窗，或者只在失败时推手机。",
                "view.notify",
                "noop"));
        }

        // ---- 从来没跑过的配置组 ----
        if (input.TotalGroups >= 3 && input.GroupsNeverRun >= input.TotalGroups / 2)
        {
            list.Add(new Idea("groups.unused",
                $"有 {input.GroupsNeverRun} 个配置组从没跑过",
                $"一共 {input.TotalGroups} 组，其中 {input.GroupsNeverRun} 组一次都没进过队。"
                + "留着不用的话，可以在配置组里取消勾选，界面会清爽一点。",
                "view.groups",
                "noop"));
        }

        // ---- 定时没开但有稳定习惯 ----
        if (!input.ScheduleEnabled && !input.AwayEnabled && input.SampleDays >= 14 && tools.Sum(t => t.Runs) >= 10)
        {
            list.Add(new Idea("schedule.auto",
                "你可能想开定时",
                $"这 {input.SampleDays} 天里你手动跑了 {tools.Sum(t => t.Runs)} 次。"
                + "开定时之后这段时间就不用自己点了。",
                "view.schedule",
                "noop"));
        }

        return list;
    }

    /// <summary>给通知/晨报用的一句话；没有建议时返回空串（界面据此整块不出现）。</summary>
    public static string Headline(IReadOnlyList<Idea>? ideas)
    {
        if (ideas == null || ideas.Count == 0) return "";
        if (ideas.Count == 1) return "有一条可以改进的地方。";
        return $"有 {ideas.Count} 条可以改进的地方。";
    }
}
