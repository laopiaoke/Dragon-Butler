using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 今日运行摘要（v1.13）：把今天已经记下来的两份账（配置组存档 + 队列台账）整理成能看的东西。
///
/// <para>
/// 为什么单独拆一个纯计算类：这份摘要要能断言——「三组跑完、总耗时 42 分、其中一组比往常慢」，
/// 这些结论错了，界面上就会给出错误的自我评价。把它做成不碰 UI、不碰文件的静态函数，
/// 探针喂几条假记录就能验证，不用真的挂机一晚上。
/// </para>
/// <para>
/// 刻意不做的事：不做图表、不做百分比排名、不折算成钱。这一页只回答三个问题——
/// 今天跑了几组、各花多久、有没有比往常反常。
/// </para>
/// </summary>
public static class TodayDigest
{
    /// <summary>与往常差异超过这个比例才算「快/慢」。15% 以内属正常抖动，标出来只会制造噪音。</summary>
    public const double VerdictThreshold = 0.15;

    /// <summary>一组今天的运行情况。</summary>
    public sealed record GroupRow(
        string Name,
        string Start,
        string End,
        TimeSpan? Duration,
        double? MedianMinutes,
        bool Running,
        string Verdict);

    /// <summary>整页的汇总。</summary>
    public sealed record Digest(
        IReadOnlyList<GroupRow> Groups,
        TimeSpan TotalGroupTime,
        int FinishedCount,
        string Headline);

    /// <summary>
    /// 整理今天的配置组。传进来的 <paramref name="medianOf"/> 是「这个组往常要多久」，
    /// 单独作为委托是为了让探针能确定性地喂固定值。
    /// </summary>
    public static Digest Build(IEnumerable<GroupRecord>? records, Func<string, double?> medianOf)
    {
        var rows = new List<GroupRow>();
        TimeSpan total = TimeSpan.Zero;
        int finished = 0;

        foreach (var r in records ?? Enumerable.Empty<GroupRecord>())
        {
            if (r == null || string.IsNullOrWhiteSpace(r.Name)) continue;
            var span = GroupArchive.DurationOf(r);
            bool running = string.IsNullOrWhiteSpace(r.End);

            // 正在跑的那一段不参与「总共跑了多久」：它还没结束，加进去就是个一直在变的数。
            if (span.HasValue && !running)
            {
                total += span.Value;
                finished++;
            }

            double? median = null;
            try { median = medianOf(r.Name); } catch { median = null; }

            rows.Add(new GroupRow(
                r.Name,
                r.Start ?? "",
                r.End ?? "",
                span,
                median,
                running,
                Verdict(span, median, running)));
        }

        return new Digest(rows, total, finished, Headline(rows.Count, finished, total));
    }

    /// <summary>和往常比：快 / 慢 / 差不多。缺一边的样本就不评价。</summary>
    public static string Verdict(TimeSpan? duration, double? medianMinutes, bool running)
    {
        if (running) return "进行中";
        if (duration == null || medianMinutes == null || medianMinutes.Value <= 0) return "";

        double ratio = duration.Value.TotalMinutes / medianMinutes.Value;
        if (ratio < 1 - VerdictThreshold) return "比往常快";
        if (ratio > 1 + VerdictThreshold) return "比往常慢";
        return "和往常差不多";
    }

    private static string Headline(int groups, int finished, TimeSpan total)
    {
        if (groups == 0) return "今天还没有配置组记录。跑一次 BGI 就会出现。";
        if (finished == 0) return "今天有 1 组还在跑，暂时没有跑完的记录。";
        return $"今天跑了 {finished} 组，合计 {LiveStatus.Short(total)}。";
    }

    /// <summary>
    /// 文字版：一行一组，方便直接粘到群里或记事本。
    /// 之所以给这个按钮，是因为「截图给别人看」比「复制几行字」麻烦得多。
    /// </summary>
    public static string Report(Digest digest, IEnumerable<JournalEntry>? journal, DateTime day)
    {
        var lines = new List<string> { $"今日运行（{day:yyyy-MM-dd}）" };

        if (digest.Groups.Count == 0)
        {
            lines.Add("配置组：今天没有记录");
        }
        else
        {
            lines.Add($"配置组：{digest.FinishedCount} 组跑完，合计 {LiveStatus.Short(digest.TotalGroupTime)}");
            foreach (var g in digest.Groups)
            {
                string dur = g.Running ? "进行中" : g.Duration.HasValue ? LiveStatus.Short(g.Duration.Value) : "无结束时间";
                // 进行中的行不再重复一遍「进行中」——括号里已经说了，说两遍像出了 bug。
                string verdict = g.Running || string.IsNullOrEmpty(g.Verdict) ? "" : "，" + g.Verdict;
                lines.Add($"· {g.Name}：{g.Start} → {(g.Running ? "…" : g.End)}（{dur}{verdict}）");
            }
        }

        var entries = (journal ?? Enumerable.Empty<JournalEntry>()).Where(e => e != null).ToList();
        if (entries.Count == 0)
        {
            lines.Add("队列台账：今天没有跑过工具");
        }
        else
        {
            lines.Add("队列台账：");
            foreach (var e in entries)
            {
                string mark = string.Equals(e.State, nameof(ToolRunState.Done), StringComparison.OrdinalIgnoreCase) ? "✓" : "✗";
                string why = string.IsNullOrWhiteSpace(e.Reason) ? "" : "，" + e.Reason;
                lines.Add($"· {mark} {e.Name} {StateText(e.State)}{why}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 工具行的文字（界面上一行一个）：✓ BetterGI · 已完成 / ✗ 三月七助手 · 异常退出，退出码 1。
    ///
    /// <para>
    /// 状态翻成中文：这里是给人看的一页，冒出来一个 Done / Failed 会显得整页只在半中半英。
    /// 认不出的状态就原样返回，而不是硬翻成「未知」——保留原文至少还能让人去查。
    /// </para>
    /// </summary>
    public static string ToolLine(JournalEntry e)
    {
        if (e == null) return "";
        string why = string.IsNullOrWhiteSpace(e.Reason) ? "" : "，" + e.Reason;
        return e.Name + " · " + StateText(e.State) + why;
    }

    /// <summary>队列终态的中文说法。</summary>
    public static string StateText(string? state)
    {
        string s = (state ?? "").Trim();
        if (s.Length == 0) return "";
        if (string.Equals(s, nameof(ToolRunState.Done), StringComparison.OrdinalIgnoreCase)) return "已完成";
        if (string.Equals(s, nameof(ToolRunState.Failed), StringComparison.OrdinalIgnoreCase)) return "没跑成";
        if (string.Equals(s, nameof(ToolRunState.Timeout), StringComparison.OrdinalIgnoreCase)) return "超时";
        if (string.Equals(s, nameof(ToolRunState.Halted), StringComparison.OrdinalIgnoreCase)) return "状态不明已停手";
        if (string.Equals(s, nameof(ToolRunState.Stalled), StringComparison.OrdinalIgnoreCase)) return "疑似卡住";
        if (string.Equals(s, nameof(ToolRunState.Running), StringComparison.OrdinalIgnoreCase)) return "正在跑";
        return s;
    }
}
