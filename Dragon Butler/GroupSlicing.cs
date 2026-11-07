using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// v1.9 / P2：配置组切片——让队列里的每一项可以指定"只跑这几个配置组"，
/// 而不是全队共用一份全局勾选。
///
/// 为什么要有它：一个人白天刷日常组、晚上刷活动组时，现在只能每次手动改全局勾选；
/// 改成队列项各自带一份，就能"日常 → 活动 → 收尾"一条队跑完，中间不用人管。
///
/// 三条设计约束（都是踩过的坑，别改）：
/// 1. 空列表＝沿用全局勾选，不是"一个都不跑"。老配置读进来没有这个字段，
///    语义必须等价于以前的行为，否则升级当天所有人都会发现"一个组都不跑"。
/// 2. 组名按 OrdinalIgnoreCase 去重，并保持书写顺序——BGI 的 --startGroups
///    是按给定顺序执行的，顺序有意义。
/// 3. 认不出的组名直接丢掉（BGI 里删掉的组、改名后的残留）：宁可少跑一个组，
///    也不要因为一个已经不存在的组让 BetterGI 启动失败。known 为空＝不做过滤
///    （还没识别过配置组时不该把用户写的全丢掉）。
/// </summary>
public static class GroupSlicing
{
    /// <summary>BGI 的启动参数名。</summary>
    public const string StartGroupsFlag = "--startGroups";

    /// <summary>
    /// 清洗一份组名清单：去空白、去重（忽略大小写）、保序；
    /// <paramref name="known"/> 非空时，剔除不在其中的组名。
    /// </summary>
    public static List<string> Sanitize(IEnumerable<string>? groups, IReadOnlyCollection<string>? known = null)
    {
        var result = new List<string>();
        if (groups == null) return result;

        HashSet<string>? allowed = null;
        if (known != null && known.Count > 0)
            allowed = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in groups)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string name = raw.Trim();
            if (allowed != null && !allowed.Contains(name)) continue;
            if (!seen.Add(name)) continue;
            result.Add(name);
        }
        return result;
    }

    /// <summary>
    /// 某一项实际要跑的组：本项自己写了就用它，否则用全局勾选。
    /// 返回空列表＝交给 BGI 的配置名/默认流程（与以前一致）。
    /// </summary>
    public static List<string> Effective(IReadOnlyList<string>? entryGroups, IReadOnlyList<string>? globalGroups, IReadOnlyList<string>? known = null)
    {
        var own = Sanitize(entryGroups, known);
        if (own.Count > 0) return own;
        return Sanitize(globalGroups, known);
    }

    /// <summary>这一项是不是在用全局勾选（界面上要能一眼看出来）。</summary>
    public static bool UsesGlobal(IReadOnlyList<string>? entryGroups)
        => Sanitize(entryGroups).Count == 0;

    /// <summary>拼 BGI 的 --startGroups 参数；没有组时返回空串（调用方据此走默认参数）。</summary>
    public static string StartGroupsArgument(IEnumerable<string>? groups)
    {
        var list = Sanitize(groups);
        if (list.Count == 0) return "";
        return StartGroupsFlag + " " + string.Join(" ", list.Select(g => "\"" + g + "\""));
    }

    /// <summary>
    /// 队列项之间共享的同一组名去重后的并集，按首次出现顺序——日志和界面摘要用。
    /// </summary>
    public static List<string> Union(IEnumerable<IReadOnlyList<string>?> sources)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (string? raw in source ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string name = raw.Trim();
                if (seen.Add(name)) result.Add(name);
            }
        }
        return result;
    }

    /// <summary>给人看的一句话：用全局 / 指定了哪几个组。</summary>
    public static string Describe(IReadOnlyList<string>? entryGroups)
    {
        var own = Sanitize(entryGroups);
        if (own.Count == 0) return "用全局勾选的配置组";
        if (own.Count == 1) return "只跑 " + own[0];
        if (own.Count == 2) return "只跑 " + own[0] + "、" + own[1];
        return $"只跑 {own[0]} 等 {own.Count} 个组";
    }

    /// <summary>
    /// 把界面上勾出来的结果写回队列项（下标 → 组名列表）。
    /// 传空列表表示"恢复用全局"。
    /// </summary>
    public static void Apply(IReadOnlyList<QueueEntry>? entries, IReadOnlyDictionary<int, List<string>>? picks, IReadOnlyList<string>? known = null)
    {
        if (entries == null || picks == null) return;
        foreach (var pair in picks)
        {
            int index = pair.Key;
            if (index < 0 || index >= entries.Count) continue;
            entries[index].Groups = Sanitize(pair.Value, known);
        }
    }
}
