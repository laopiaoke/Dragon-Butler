using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 把 <see cref="Suggest"/> 需要的样本从已有台账里算出来。
///
/// <para>
/// 不新增采集：运行次数、失败次数都来自 <see cref="RunJournal"/> 的 30 天记录，
/// 通知条数来自内存里的 <see cref="DecisionLog"/>（它只留 200 条，所以这里按「拿得到的」算——
/// 数字不准时就<b>不建议</b>，而不是拿不准的数字去劝人改设置）。
/// </para>
/// </summary>
public static class SuggestFacts
{
    /// <summary>扫最近 <paramref name="days"/> 天（默认 30，与台账保留期一致）。</summary>
    public static List<Suggest.ToolStat> ToolStats(DateTime today, int days = 30)
    {
        var runs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fails = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int realDays = 0;

        for (int back = 0; back < days; back++)
        {
            var entries = RunJournal.GetDay(today.AddDays(-back));
            if (entries.Count == 0) continue;
            realDays++;
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.ToolId)) continue;
                // 跳过的不算「跑了」——它根本没启动，算进去会把失败率洗白。
                // Skipped 不进 JournalEntry.State（台账根本不记跳过项），这里只需排掉它。
                var result = e.State;
                bool skipped = string.Equals(result, "Skipped", StringComparison.OrdinalIgnoreCase);
                if (!skipped) runs[e.ToolId] = runs.TryGetValue(e.ToolId, out int r) ? r + 1 : 1;
                if (!skipped && !string.Equals(result, nameof(ToolRunState.Done), StringComparison.OrdinalIgnoreCase))
                {
                    fails[e.ToolId] = fails.TryGetValue(e.ToolId, out int f) ? f + 1 : 1;
                }
                if (!names.ContainsKey(e.ToolId)) names[e.ToolId] = e.Name ?? e.ToolId;
            }
        }

        return runs.Select(kv => new Suggest.ToolStat(
            kv.Key,
            names.TryGetValue(kv.Key, out var n) ? n : kv.Key,
            kv.Value,
            fails.TryGetValue(kv.Key, out int f) ? f : 0,
            0)).ToList();
    }

    /// <summary>台账里真正有记录的天数（不是日历天数）。</summary>
    public static int SampleDays(DateTime today, int days = 30)
    {
        int count = 0;
        for (int back = 0; back < days; back++)
        {
            if (RunJournal.GetDay(today.AddDays(-back)).Count > 0) count++;
        }
        return count;
    }

    public static int CountDecisions(DecisionSource source, DateTime today)
    {
        try { return DecisionLog.Today(today).Count(d => d.Source == source); }
        catch { return 0; }
    }

    public static Suggest.Input Collect(DateTime today)
    {
        var s = AppState.Settings;
        int groups = 0, neverRun = 0;
        try
        {
            var scan = BgiDiscovery.Scan();
            groups = scan.Groups.Count;
            // 「从没跑过」只能按台账里出现过的组名判断——组名出现在 GroupArchive 才算跑过。
            var ran = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int back = 0; back < 30; back++)
            {
                foreach (var g in GroupArchive.GetDay(today.AddDays(-back)))
                    if (g?.Name is { Length: > 0 }) ran.Add(g.Name);
            }
            neverRun = scan.Groups.Count(g => !ran.Contains(g));
        }
        catch { }

        var decisions = new List<Decision>();
        try { decisions = DecisionLog.Today(today); } catch { }

        return new Suggest.Input(
            Tools: ToolStats(today),
            SampleDays: SampleDays(today),
            NotifyCount30d: CountDecisions(DecisionSource.Queue, today) + CountDecisions(DecisionSource.Sentinel, today),
            NotifyOpenedCount: 0,
            ScheduleEnabled: s.ScheduleEnabled,
            AwayEnabled: s.AwayStartEnabled,
            GroupsNeverRun: neverRun,
            TotalGroups: groups);
    }

    public static IReadOnlyList<Suggest.Idea> Build(DateTime today)
    {
        try { return Suggest.Build(Collect(today)); }
        catch (Exception ex)
        {
            LogService.LogEvent("建议生成失败：" + ex.Message);
            return Array.Empty<Suggest.Idea>();
        }
    }
}
