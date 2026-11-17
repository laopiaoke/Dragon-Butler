using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 复盘（v1.20 / 奇思妙想 #1）：把五张表拼成一段人话。
///
/// <para>
/// <b>为什么不复用 <see cref="MorningReport"/> 与 <see cref="TodayDigest"/></b>：那两个是
/// 「表格的文字版」——逐行罗列，读的人得自己在脑子里合并。复盘要回答的是
/// 「昨晚到底发生了什么」，所以它是<b>叙事</b>：先说结论，再补一句最值得说的细节，
/// 最后才说没做到的。三条以内，超了就砍。
/// </para>
/// <para>
/// <b>三个不肯让步</b>：
/// ① 不编。任何一句话都必须能指回一张表的某个字段；没数据就说没数据，不拿「一切正常」凑数。
/// ② 不说教。不出现「建议你」「请注意」「为了更好的体验」。
/// ③ 不报流水账。宁可少说一件事，也不把五张表逐条翻译一遍。
/// </para>
/// <para>
/// 纯函数：不读 <see cref="AppState"/>、不碰磁盘、不看时钟，全部由调用方喂进来。
/// </para>
/// </summary>
public static class Recap
{
    /// <summary>一句话的骨架。Kind 决定语气与去向，测试按 Kind 断言而不是按文案。</summary>
    public enum LineKind
    {
        /// <summary>开头那件最重要的事。</summary>
        Lead,
        /// <summary>值得提一句的细节。</summary>
        Detail,
        /// <summary>没做到的。</summary>
        Gap,
    }

    public sealed record Line(LineKind Kind, string Text);

    /// <summary>一次复盘的全部输入。字段名对应真实来源，便于以后回溯。</summary>
    public sealed record Input(
        DateTime? StartAt,
        DateTime? EndAt,
        int DoneTools,
        int TotalTools,
        IReadOnlyList<GroupRow>? Groups,
        IReadOnlyList<ToolRow>? Tools,
        long Mora,
        long Exp,
        IReadOnlyDictionary<string, int>? Materials,
        string? LastErrorLine,
        bool StoppedByUser,
        bool RanNothing);

    /// <summary>一个配置组在复盘里的样子。<see cref="MedianMinutes"/> 来自近 14 天中位数。</summary>
    public sealed record GroupRow(string Name, TimeSpan? Duration, bool Unfinished, double? MedianMinutes);

    /// <summary>一个队列项在复盘里的样子。</summary>
    public sealed record ToolRow(string Name, string State, string? Reason);

    /// <summary>
    /// 生成复盘。返回的行数在 1..4 之间：第一条永远是 Lead，最后一条（若有）永远是 Gap。
    /// </summary>
    public static IReadOnlyList<Line> Build(Input input)
    {
        var lines = new List<Line>();
        if (input == null) return lines;

        if (input.RanNothing)
        {
            lines.Add(new Line(LineKind.Lead, "昨晚没跑。"));
            return lines;
        }

        bool hasTools = input.TotalTools > 0;
        int failedTools = input.Tools?.Count(t => !IsOk(t.State)) ?? 0;
        bool allOk = hasTools && failedTools == 0 && input.DoneTools >= input.TotalTools;

        lines.Add(new Line(LineKind.Lead, LeadText(input, hasTools, failedTools, allOk)));

        string? detail = DetailText(input, hasTools);
        if (detail != null) lines.Add(new Line(LineKind.Detail, detail));

        string? gap = GapText(input, hasTools, failedTools, allOk);
        if (gap != null) lines.Add(new Line(LineKind.Gap, gap));

        return lines;
    }

    private static string LeadText(Input input, bool hasTools, int failedTools, bool allOk)
    {
        string when = When(input.StartAt);
        string prefix = when.Length > 0 ? when + "开跑，" : "";

        if (input.StoppedByUser)
            return prefix + "你让它停了。";

        if (hasTools && allOk)
        {
            string spent = input.EndAt.HasValue && input.StartAt.HasValue
                ? $"一共 {Duration(input.EndAt.Value - input.StartAt.Value)}。"
                : "";
            return prefix + $"{input.TotalTools} 项都跑成了。" + spent;
        }

        if (hasTools && failedTools > 0)
        {
            int ok = Math.Max(0, input.DoneTools);
            return prefix + $"{ok} 项跑成，{failedTools} 项没成。";
        }

        // 没有队列数据，但有配置组：说明是从日志/Webhook 那侧看到的
        var groups = input.Groups;
        if (groups is { Count: > 0 })
        {
            int finished = groups.Count(g => !g.Unfinished);
            if (finished == groups.Count)
                return prefix + $"{finished} 个配置组都跑完了。";
            return prefix + $"{finished} 个配置组跑完，{groups.Count - finished} 个没结束。";
        }

        return prefix + "跑过，但没有留下可说的记录。";
    }

    private static string? DetailText(Input input, bool hasTools)
    {
        // 优先说「比平时快/慢」——这是唯一一条只有长期积攒才说得出来的话
        var withMedian = input.Groups?
            .Where(g => !g.Unfinished && g.Duration.HasValue && g.MedianMinutes is > 0)
            .ToList();

        if (withMedian is { Count: > 0 })
        {
            // 挑偏离最大的那个，不是挑第一个
            var worst = withMedian
                .OrderByDescending(g => Math.Abs(Ratio(g) - 1.0))
                .First();
            double ratio = Ratio(worst);
            // 带上「往常多少」：只说「慢 95%」读的人还得自己换算到底慢了几分钟。
            if (ratio <= 0.85)
                return $"{worst.Name} {Duration(worst.Duration!.Value)}，比往常快 {Percent(1 - ratio)}"
                     + $"（往常 {Duration(TimeSpan.FromMinutes(worst.MedianMinutes!.Value))}）。";
            if (ratio >= 1.15)
                return $"{worst.Name} {Duration(worst.Duration!.Value)}，比往常慢 {Percent(ratio - 1)}"
                     + $"（往常 {Duration(TimeSpan.FromMinutes(worst.MedianMinutes!.Value))}）。";
        }

        // 没有可比的时间，就说收益——它具体、可核，且是用户真正在意的东西
        if (input.Mora > 0 || input.Exp > 0 || (input.Materials?.Count ?? 0) > 0)
        {
            string income = HarvestLedger.Describe(new Harvest
            {
                Mora = input.Mora,
                Exp = input.Exp,
                Materials = input.Materials == null
                    ? new Dictionary<string, int>(StringComparer.Ordinal)
                    : new Dictionary<string, int>(input.Materials, StringComparer.Ordinal),
            }, "这一趟约");
            if (income.Length > 0) return income + "。";
        }

        // 队列全成但没别的可说：报一下最长的那个组，让人知道时间花在哪
        var longest = input.Groups?
            .Where(g => g.Duration.HasValue)
            .OrderByDescending(g => g.Duration!.Value)
            .FirstOrDefault();
        if (longest != null && hasTools)
            return $"花时间最多的是 {longest.Name}，{Duration(longest.Duration!.Value)}。";

        return null;
    }

    private static string? GapText(Input input, bool hasTools, int failedTools, bool allOk)
    {
        if (input.StoppedByUser) return null;

        if (failedTools > 0 && input.Tools != null)
        {
            var first = input.Tools.FirstOrDefault(t => !IsOk(t.State));
            if (first != null)
            {
                string reason = string.IsNullOrWhiteSpace(first.Reason) ? "" : "，" + first.Reason;
                int left = input.TotalTools - input.DoneTools - failedTools;
                string tail = left > 0 ? $"，后面 {left} 项没开始" : "";
                return $"{first.Name} 没跑成{reason}{tail}。";
            }
        }

        var unfinished = input.Groups?.Where(g => g.Unfinished).ToList();
        if (unfinished is { Count: > 0 })
            return $"{string.Join("、", unfinished.Take(3).Select(g => g.Name))} 没有结束标记，可能被中断了。";

        if (hasTools && !allOk)
            return "有一项没跑成，记录里没留下原因。";

        if (!string.IsNullOrWhiteSpace(input.LastErrorLine))
            return "日志里有报错：" + input.LastErrorLine;

        return null;
    }

    private static double Ratio(GroupRow g)
    {
        double median = g.MedianMinutes!.Value;
        double actual = g.Duration!.Value.TotalMinutes;
        return median <= 0 ? 1.0 : actual / median;
    }

    private static bool IsOk(string? state) =>
        string.Equals(state, nameof(ToolRunState.Done), StringComparison.Ordinal);

    /// <summary>「昨晚 23:04」这种。只给时分，复盘天然是「昨晚」的语境。</summary>
    private static string When(DateTime? at) =>
        at.HasValue ? at.Value.ToString("HH:mm") : "";

    /// <summary>人话时长：不出现「0.5 小时」这种。</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        int totalMinutes = (int)Math.Round(span.TotalMinutes);
        if (totalMinutes < 1) return "不到 1 分钟";
        if (totalMinutes < 60) return totalMinutes + " 分钟";
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return minutes == 0 ? hours + " 小时" : $"{hours} 小时 {minutes} 分";
    }

    /// <summary>百分比取整，不留小数——复盘不是报表。</summary>
    private static string Percent(double fraction)
    {
        int p = (int)Math.Round(Math.Abs(fraction) * 100);
        if (p <= 0) p = 1;
        return p + "%";
    }
}
