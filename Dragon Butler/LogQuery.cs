using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DragonButler;

/// <summary>
/// 打开日志视图时带过去的过滤条件（v1.41 / M1-2）。
///
/// 以前日志是底部一个独立的 Tab：点进去是「今天的全部」，想找某一次、
/// 某个工具、某一天，只能自己在几百行里翻。现在它不再占 Tab，
/// 而是从几个具体的地方「带着问题」进来，进来就已经筛好了。
///
/// 三个参数可以同时给，取交集：
///   RunId  —— 哪一次运行（日志行里的 [run:xxxxxxxx]，见 RunCoordinator）
///   ToolId —— 哪个工具
///   Date   —— 哪一天
///
/// 全部为空 = 全量日志（主页「更多 → 全部日志」走这条）。
/// </summary>
public readonly record struct LogFilter(string? RunId, string? ToolId, DateTime? Date)
{
    public static readonly LogFilter None = new(null, null, null);

    public static LogFilter ForRun(string runId) => new(runId, null, null);
    public static LogFilter ForTool(string toolId) => new(null, toolId, null);
    public static LogFilter ForGroup(string groupName, DateTime date) => new(null, GroupPrefix + groupName, date);

    /// <summary>
    /// 「按配置组看」这一路没有独立的字段：日志里没有组 ID，只有组名出现在「组开始：X」这样的行里。
    /// 为了不让 ToolId 这个字段变得含混，按组过滤时给 ToolId 加一个不会和真实工具 id 撞车的前缀。
    /// </summary>
    public const string GroupPrefix = "group:";

    public bool IsEmpty => string.IsNullOrEmpty(RunId) && string.IsNullOrEmpty(ToolId) && Date == null;

    public bool IsGroup => ToolId != null && ToolId.StartsWith(GroupPrefix, StringComparison.Ordinal);

    public string? GroupName => IsGroup ? ToolId![GroupPrefix.Length..] : null;

    /// <summary>
    /// 界面上那行「正在看：…」。空条件返回「全部日志」。
    /// 组名/工具名这一类要人能读的写法由调用方给（它才知道显示名），
    /// 这里只负责把三个条件的骨架拼出来。
    /// </summary>
    public string Describe(Func<string, string?> nameOf)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(RunId)) parts.Add("运行 " + RunId);
        if (!string.IsNullOrEmpty(ToolId))
        {
            string? name = IsGroup ? GroupName : nameOf(ToolId!);
            parts.Add(name ?? ToolId!);
        }
        if (Date.HasValue) parts.Add(Date.Value.ToString("yyyy-MM-dd"));
        return parts.Count == 0 ? "全部日志" : string.Join(" · ", parts);
    }
}

/// <summary>
/// 读日志文件并按 <see cref="LogFilter"/> 过滤（v1.41 / M1-2）。
///
/// 日志的格式是固定的：<c>HH:mm:ss 事件：{正文}</c>，一天一个文件
/// （<c>yyyy-MM-dd_运行.log</c>，见 LogService.LogEvent）。
/// 所以这里不做任何猜测式的解析——行首不是时间的行原样保留、时间记空，
/// 宁可显示得朴素一点，也不要把「读不懂」当成「没有」。
/// </summary>
public static class LogQuery
{
    /// <summary>日志一行的解析结果。<see cref="RunId"/> 为空表示这行不属于任何一次运行。</summary>
    public sealed record Entry(string Time, string Text, string? RunId);

    private static readonly Regex RunTag = new(@"\[run:([0-9a-f]{8})\]", RegexOptions.Compiled);

    /// <summary>一次最多读多少行（从文件末尾往前）。不是为了省内存，是为了别把人埋了。</summary>
    public const int DefaultLimit = 400;

    public static string FileOf(DateTime day) =>
        Path.Combine(LogService.Dir, day.ToString("yyyy-MM-dd") + "_运行.log");

    /// <summary>
    /// 按条件读日志。条件全空时读 <paramref name="now"/> 那天的全量。
    /// 文件不存在返回空列表——「今天还没跑」和「读不出来」在界面上是同一句话，
    /// 但这里不编造，让调用方自己决定说什么。
    /// </summary>
    public static List<Entry> Read(LogFilter filter, DateTime now, Func<string, string?> nameOf, int limit = DefaultLimit)
    {
        DateTime day = filter.Date ?? now.Date;
        var result = new List<Entry>();
        string file = FileOf(day);
        try
        {
            if (!File.Exists(file)) return result;
            // 只取尾部：日志可以很长，而人要找的几乎总是在最近。
            var lines = TailLines(file, limit * 4);
            string? toolName = null;
            if (!string.IsNullOrEmpty(filter.ToolId) && !filter.IsGroup)
                toolName = nameOf(filter.ToolId!);
            string? groupName = filter.GroupName;

            foreach (string raw in lines)
            {
                if (raw.Length == 0) continue;
                var entry = Parse(raw);
                if (!Matches(entry, filter, toolName, groupName)) continue;
                result.Add(entry);
            }

            if (result.Count > limit) result = result.Skip(result.Count - limit).ToList();
            return result;
        }
        catch
        {
            return result;
        }
    }

    public static Entry Parse(string raw)
    {
        string time = "";
        string text = raw;
        // 「HH:mm:ss 事件：」这 11 个字符是 LogService 写的固定前缀
        if (raw.Length > 11 && raw[2] == ':' && raw[5] == ':' && raw[8] == ' ')
        {
            time = raw[..8];
            text = raw[11..];
        }
        var m = RunTag.Match(raw);
        return new Entry(time, text, m.Success ? m.Groups[1].Value : null);
    }

    /// <summary>
    /// 三个条件取交集。
    /// 工具那一路认两个名字：日志正文里的显示名（队列写的是 DisplayName），
    /// 以及带工具 id 的行（如「单工具执行异常」之外的那些）。两样都认，是因为
    /// 队列和守护写日志时用的不是同一个称呼，只认一个会漏。
    /// </summary>
    public static bool Matches(Entry entry, LogFilter filter, string? toolName, string? groupName)
    {
        if (!string.IsNullOrEmpty(filter.RunId)
            && !string.Equals(entry.RunId, filter.RunId, StringComparison.OrdinalIgnoreCase)) return false;

        if (!string.IsNullOrEmpty(groupName)
            && entry.Text.IndexOf(groupName, StringComparison.OrdinalIgnoreCase) < 0) return false;

        if (!string.IsNullOrEmpty(filter.ToolId) && groupName == null)
        {
            bool hit = entry.Text.IndexOf(filter.ToolId!, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hit && !string.IsNullOrEmpty(toolName))
                hit = entry.Text.IndexOf(toolName!, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hit) return false;
        }

        return true;
    }

    private static List<string> TailLines(string file, int want)
    {
        var all = File.ReadAllLines(file);
        if (all.Length <= want) return all.ToList();
        return all.Skip(all.Length - want).ToList();
    }

    /// <summary>
    /// 从日志里捞出「最近一次没跑顺」的 runId（M1-2 主页「看现场」要用）。
    ///
    /// 为什么是捞而不是记：runId 现在只活在日志行的 [run:xxxxxxxx] 里，
    /// 队列没把它存进设置。多存一个字段当然更省事，但那等于为了一个视图
    /// 动一次持久化格式；而日志本来就在那儿，读一遍就够。
    /// 找不到就返回 null——那时候「看现场」应该老实说没有现场可看，而不是打开一个空页面。
    /// </summary>
    public static string? LastFailedRunId(DateTime now)
    {
        try
        {
            for (int back = 0; back < 3; back++)
            {
                var day = now.Date.AddDays(-back);
                string file = FileOf(day);
                if (!File.Exists(file)) continue;
                var lines = TailLines(file, 4000);
                // 从后往前找「有 run 标记、且这一行说了不顺」的记录
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    var e = Parse(lines[i]);
                    if (e.RunId == null) continue;
                    if (IsTrouble(e.Text)) return e.RunId;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 哪些行算「这次不顺」。只认代码里真实写下的那几种说法
    /// （见 QueueService / RunCoordinator 的日志文本），不自造词。
    /// </summary>
    public static bool IsTrouble(string text) =>
        text.Contains("疑似挂起", StringComparison.Ordinal)
        || text.Contains("失败策略=中止", StringComparison.Ordinal)
        || text.Contains("悬置", StringComparison.Ordinal)
        || text.Contains("异常", StringComparison.Ordinal)
        || text.Contains("未命中", StringComparison.Ordinal);
}
