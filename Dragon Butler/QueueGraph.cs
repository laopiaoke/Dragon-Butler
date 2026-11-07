using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>依赖门的判定结果。</summary>
public enum GateVerdict
{
    /// <summary>依赖都已完成，可以跑。</summary>
    Ready = 0,

    /// <summary>还有依赖没跑（可能是同一轮里排在后面，也可能是被计划跳过了）。</summary>
    Waiting = 1,

    /// <summary>有依赖失败了——这一项跑下去只会更糟。</summary>
    Blocked = 2,
}

/// <summary>一次依赖判定的结论。</summary>
public sealed record GateResult(GateVerdict Verdict, string Reason);

/// <summary>
/// v1.9 / P2：队列依赖编排（DAG）。
///
/// 队列原本是一条直线——前面的失败了，后面照样往下跑。可现实里有先后：
/// 「BGI 先把体力清完，再跑整理」；「BetterGI 没跑成，三月七接着跑只会读到旧存档」。
/// 这里只做纯计算，真正的执行仍在 <see cref="QueueService"/>：
/// 排序负责"把依赖放在前面"，门负责"依赖没成就不放行"。
///
/// 刻意不做的两件事：
/// 1. 不抛异常——环只是"永远等不到"，运行时靠门兜底跳过并说清原因，比崩掉好。
/// 2. 不做并行——同一时间只跑一个工具是这个软件的立身之本（见 RunCoordinator 的「宁等勿进」）。
/// </summary>
public static class QueueGraph
{
    /// <summary>依赖失败时的默认策略：跳过这一项，继续后面的。</summary>
    public const string PolicySkip = "Skip";

    /// <summary>依赖失败时的另一种策略：整条队列停下等用户处理。</summary>
    public const string PolicyAbort = "Abort";

    /// <summary>这一项声明的依赖（去空、去重、保持书写顺序）。</summary>
    public static List<string> DependenciesOf(QueueEntry? entry)
    {
        if (entry?.DependsOn == null || entry.DependsOn.Count == 0) return new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (string raw in entry.DependsOn)
        {
            string id = (raw ?? "").Trim();
            if (id.Length == 0) continue;
            if (!seen.Add(id)) continue;
            list.Add(id);
        }
        return list;
    }

    /// <summary>队列里只要有一项写了依赖，就算用了编排。</summary>
    public static bool HasDependencies(IReadOnlyList<QueueEntry>? entries)
        => entries != null && entries.Any(e => DependenciesOf(e).Count > 0);

    /// <summary>
    /// 拓扑排序：被依赖的排前面。同层保持原有顺序（用户拖动过的顺序不该被打乱）。
    /// 有环时把环上的项原序追加到末尾——它们会被门一直挡着，但队列不会因此卡死。
    /// </summary>
    public static List<QueueEntry> Order(IReadOnlyList<QueueEntry>? entries)
        => TryOrder(entries, out List<string> _);

    /// <summary>
    /// 拓扑排序，并告诉调用方"哪些项卡在环里"。
    /// 界面保存前用它提示，免得用户写出一组永远轮不到的依赖。
    /// </summary>
    public static List<QueueEntry> TryOrder(IReadOnlyList<QueueEntry>? entries, out List<string> stuck)
    {
        var source = entries ?? Array.Empty<QueueEntry>();
        var result = new List<QueueEntry>(source.Count);
        stuck = new List<string>();
        if (source.Count == 0) return result;

        // 只认"队列里真实存在"的依赖，写错的 ID 不该产生无解的等待
        var byId = new Dictionary<string, QueueEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in source)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.ToolId)) continue;
            byId.TryAdd(e.ToolId, e);
        }

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = source.Where(e => e != null && !string.IsNullOrWhiteSpace(e.ToolId)).ToList();

        bool progressed = true;
        while (pending.Count > 0 && progressed)
        {
            progressed = false;
            for (int i = 0; i < pending.Count; i++)
            {
                var entry = pending[i];
                bool ready = DependenciesOf(entry)
                    .Where(d => byId.ContainsKey(d))
                    .All(d => placed.Contains(d));
                if (!ready) continue;
                result.Add(entry);
                placed.Add(entry.ToolId);
                pending.RemoveAt(i);
                progressed = true;
                break;
            }
        }

        // 剩下的都在环里（或依赖了环上的项）：原序补到末尾，交给门去解释
        foreach (var e in pending) stuck.Add(e.ToolId);
        result.AddRange(pending);
        return result;
    }

    /// <summary>
    /// 这一项现在能不能跑。
    /// </summary>
    /// <param name="entry">队列项。</param>
    /// <param name="done">今天已经跑成的工具 ID。</param>
    /// <param name="failed">这一轮里失败过的工具 ID。</param>
    public static GateResult Gate(QueueEntry? entry, IReadOnlySet<string>? done, IReadOnlySet<string>? failed)
    {
        var deps = DependenciesOf(entry);
        if (deps.Count == 0) return new GateResult(GateVerdict.Ready, "");

        var failedSet = failed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var doneSet = done ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string dep in deps)
        {
            if (failedSet.Contains(dep))
                return new GateResult(GateVerdict.Blocked, $"依赖 {dep} 这次没跑成");
        }

        var missing = deps.Where(d => !doneSet.Contains(d)).ToList();
        if (missing.Count == 0) return new GateResult(GateVerdict.Ready, "");
        return new GateResult(GateVerdict.Waiting, $"等 {string.Join("、", missing)} 先跑成");
    }

    /// <summary>界面/日志用的一句话。</summary>
    public static string Describe(QueueEntry? entry)
    {
        var deps = DependenciesOf(entry);
        if (deps.Count == 0) return "无依赖";
        return "依赖：" + string.Join("、", deps);
    }

    /// <summary>依赖失败时是"跳过"还是"整队停下"（写错了一律按跳过处理，安全第一）。</summary>
    public static bool ShouldAbortOnDependencyFail(QueueEntry? entry)
        => string.Equals(entry?.OnDependencyFail, PolicyAbort, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从依赖表还原成每项自己的 DependsOn。
    /// 界面是"行等列"的矩阵，存盘时按下标一次性铺回去，比逐个增删可靠。
    /// </summary>
    public static void ApplyMatrix(
        IReadOnlyList<QueueEntry> entries,
        IReadOnlyDictionary<int, List<int>> waitsOn,
        string onDependencyFail)
    {
        if (entries == null) return;
        string policy = string.Equals(onDependencyFail, PolicyAbort, StringComparison.OrdinalIgnoreCase)
            ? PolicyAbort
            : PolicySkip;

        for (int row = 0; row < entries.Count; row++)
        {
            var entry = entries[row];
            if (entry == null) continue;
            var deps = new List<string>();
            if (waitsOn != null && waitsOn.TryGetValue(row, out var cols) && cols != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (int col in cols)
                {
                    if (col < 0 || col >= entries.Count || col == row) continue;
                    string id = entries[col]?.ToolId?.Trim() ?? "";
                    // 自己依赖自己是无解的，直接丢掉
                    if (id.Length == 0) continue;
                    if (string.Equals(id, entry.ToolId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(id)) continue;
                    deps.Add(id);
                }
            }
            entry.DependsOn = deps;
            entry.OnDependencyFail = policy;
        }
    }
}
