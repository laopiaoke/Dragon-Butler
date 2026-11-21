using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragonButler;

/// <summary>
/// v1.41 / M5-7：一天的日报，只算一次，两个地方各按自己的样子印。
///
/// <para>
/// 以前 App 的日报页读 <see cref="DurationLedger"/>，IM 晨报读 <see cref="GroupArchive"/>，
/// 两条路各算各的：同一份「今天跑了多久」，界面上按工具排、推送里按组排，
/// 数字对不上时没人说得清哪个是对的。现在先在这里算出一份，
/// 界面拿它画卡片、推送拿它填文字模板——两边不可能再不一致，因为只有一个源头。
/// </para>
/// <para>
/// 这份东西只装事实，不带语气。哪句抬头、哪句安慰，由两个渲染器自己按 docs/VOICE.md 去写。
/// 也正因为只装事实，探针可以直接断言数字，不用去啃界面文案。
/// </para>
/// </summary>
public sealed class DailyReportData
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";

    /// <summary>今天所有工具跑完的秒数合计。没跑过就是 0。</summary>
    [JsonPropertyName("totalSeconds")] public int TotalSeconds { get; set; }

    /// <summary>今天算进去的工具条数（跳过的、没跑完的不算）。</summary>
    [JsonPropertyName("toolCount")] public int ToolCount { get; set; }

    /// <summary>今天一共触发了几次（同一工具跑两遍算两次）。</summary>
    [JsonPropertyName("runCount")] public int RunCount { get; set; }

    /// <summary>昨天的合计秒数。昨天没有记录就是 null——「昨天没跑」和「昨天跑了 0 秒」不是一回事。</summary>
    [JsonPropertyName("previousTotalSeconds")] public int? PreviousTotalSeconds { get; set; }

    /// <summary>按耗时从多到少排的工具行。</summary>
    [JsonPropertyName("tools")] public List<DailyReportTool> Tools { get; set; } = new();

    /// <summary>按耗时从多到少排的配置组行。</summary>
    [JsonPropertyName("groups")] public List<DailyReportGroup> Groups { get; set; } = new();

    /// <summary>「异常与处理」分区。没有异常时是空列表，渲染器据此写「没有需要你操心的事」。</summary>
    [JsonPropertyName("anomalies")] public List<DailyReportAnomaly> Anomalies { get; set; } = new();

    /// <summary>
    /// 只有「暂无」类内容的分区名。渲染器把两个以上这种分区合成一行，
    /// 免得日报底下挂三行「今天还没有记录」。
    /// </summary>
    [JsonPropertyName("emptySections")] public List<string> EmptySections { get; set; } = new();

    public bool IsEmpty => ToolCount == 0 && Groups.Count == 0;

    public string ToJson() => JsonSerializer.Serialize(this, DailyReportJson.Options);
}

public sealed class DailyReportTool
{
    [JsonPropertyName("toolId")] public string ToolId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("seconds")] public int Seconds { get; set; }
    [JsonPropertyName("runs")] public int Runs { get; set; }
}

public sealed class DailyReportGroup
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("seconds")] public int Seconds { get; set; }

    /// <summary>和这个组上一次跑相比差了多少秒：负数 = 这次更快。没有可比的样本就是 null。</summary>
    [JsonPropertyName("deltaSeconds")] public int? DeltaSeconds { get; set; }

    /// <summary>上次跑是哪天（yyyy-MM-dd）。只在 <see cref="DeltaSeconds"/> 有值时有意义。</summary>
    [JsonPropertyName("comparedWith")] public string? ComparedWith { get; set; }
}

public sealed class DailyReportAnomaly
{
    [JsonPropertyName("group")] public string Group { get; set; } = "";

    /// <summary>卡在哪个环节。只写日志里真出现过的说法（比如「疑似挂起」）。</summary>
    [JsonPropertyName("stage")] public string Stage { get; set; } = "";

    /// <summary>管家实际做了什么。只允许写代码里真的发生过的动作。</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "";

    /// <summary>能定位到具体某次运行就带上，App 版据此给一个「看现场」。</summary>
    [JsonPropertyName("runId")] public string? RunId { get; set; }
}

internal static class DailyReportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// 日报的单一生成器。纯读：不写盘、不改设置、不弹窗。
/// </summary>
public static class DailyReport
{
    /// <summary>差异小于这个秒数就算「持平」——秒表噪声不该让人以为今天快了一秒。</summary>
    public const int FlatThresholdSeconds = 1;

    /// <summary>往前找「上一次跑」时最多翻多少天。</summary>
    private const int CompareLookbackDays = 30;

    /// <summary>
    /// 算指定某一天的日报。界面与推送都调这一个，别的地方不许再自己汇总。
    /// </summary>
    public static DailyReportData Build(DateTime day)
    {
        var data = new DailyReportData { Date = day.ToString("yyyy-MM-dd") };

        // ===== 工具那一半：时长账本本来就按工具记 =====
        List<DurationEntry> entries;
        try { entries = DurationLedger.GetDay(day); }
        catch { entries = new List<DurationEntry>(); }

        foreach (var e in entries.OrderByDescending(x => x.Seconds))
        {
            data.Tools.Add(new DailyReportTool
            {
                ToolId = e.ToolId,
                Name = e.Name,
                Seconds = e.Seconds,
                Runs = e.Runs,
            });
        }
        data.TotalSeconds = entries.Sum(e => e.Seconds);
        data.ToolCount = entries.Count;
        data.RunCount = entries.Sum(e => e.Runs);

        // 昨天：只有真的记到过东西才算数。没记录 → null，渲染器会省略「较昨日」。
        try
        {
            var yesterday = DurationLedger.GetDay(day.Date.AddDays(-1));
            if (yesterday.Count > 0) data.PreviousTotalSeconds = yesterday.Sum(e => e.Seconds);
        }
        catch { }

        // ===== 配置组那一半：按组名合并，并跟上一次比 =====
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            foreach (var kv in e.Groups)
            {
                merged.TryGetValue(kv.Key, out int had);
                merged[kv.Key] = had + kv.Value;
            }
        }

        // 账本里没有组名（老记录、或 BetterGI 那边没报组）时退回组归档。
        // 退回这一步是为了让「昨天跑过、但工具账本还没这一栏」的人不至于看到一片空白；
        // 两边读的还是同一个 Build()，所以界面与推送不会因此分叉。
        if (merged.Count == 0)
        {
            try
            {
                foreach (var rec in GroupArchive.GetDay(day))
                {
                    if (string.IsNullOrWhiteSpace(rec.Name)) continue;
                    var span = GroupArchive.DurationOf(rec);
                    if (span == null || span.Value.TotalSeconds <= 0) continue;
                    int seconds = (int)Math.Round(span.Value.TotalSeconds);
                    merged.TryGetValue(rec.Name, out int had);
                    merged[rec.Name] = had + seconds;
                }

                // 这时合计也只能从组这一侧来，否则会出现「合计 0 秒、下面却列着几十分钟」。
                if (data.TotalSeconds == 0 && merged.Count > 0) data.TotalSeconds = merged.Values.Sum();
            }
            catch { }
        }

        foreach (var kv in merged.OrderByDescending(x => x.Value))
        {
            var row = new DailyReportGroup { Name = kv.Key, Seconds = kv.Value };
            var previous = PreviousRunOf(kv.Key, day);
            if (previous != null)
            {
                row.DeltaSeconds = kv.Value - previous.Value.Seconds;
                row.ComparedWith = previous.Value.Date.ToString("yyyy-MM-dd");
            }
            data.Groups.Add(row);
        }

        // ===== 异常与处理：只从日志里捞，捞不到就什么都不写 =====
        data.Anomalies = CollectAnomalies(day);

        // ===== 空分区：渲染器要把它们合成一行 =====
        if (data.Tools.Count == 0) data.EmptySections.Add("每个工具");
        if (data.Groups.Count == 0) data.EmptySections.Add("配置组执行");

        return data;
    }

    /// <summary>
    /// 这个组上一次（不含当天、往前 <see cref="CompareLookbackDays"/> 天之内）跑完用了多久。
    /// 只认整组跑完、且那一天没断过段的记录——中途重开的那条起止跨着两次执行，拿来比会显得今天格外快。
    /// </summary>
    private static (DateTime Date, int Seconds)? PreviousRunOf(string groupName, DateTime day)
    {
        try
        {
            for (int i = 1; i <= CompareLookbackDays; i++)
            {
                var that = day.Date.AddDays(-i);
                foreach (var rec in GroupArchive.GetDay(that))
                {
                    if (!string.Equals(rec.Name, groupName, StringComparison.Ordinal)) continue;
                    if (rec.Segments > 1) continue;
                    var span = GroupArchive.DurationOf(rec);
                    if (span == null || span.Value.TotalSeconds <= 0) continue;
                    return (that, (int)Math.Round(span.Value.TotalSeconds));
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 从当天的运行日志里抽出「出过事、并且管家真的做了什么」的条目。
    ///
    /// <para>
    /// 只认代码里真实写下的那几句话（队列那几处 LogEvent 的原文），
    /// 不认识的异常一律不写——任务书的硬约束是「管家只做过哪些动作就写哪些」，
    /// 宁可这一栏写着「没有需要你操心的事」，也不要编一个没发生过的处理动作。
    /// </para>
    /// </summary>
    private static List<DailyReportAnomaly> CollectAnomalies(DateTime day)
    {
        var list = new List<DailyReportAnomaly>();
        List<LogQuery.Entry> entries;
        try
        {
            entries = LogQuery.Read(LogFilter.None, day, _ => null, limit: 4000);
        }
        catch { return list; }

        // 同一个组、同一种卡法只留一条：日志里悬置之后往往紧接着还有一条「队列停止」，不能算两次。
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in entries)
        {
            string text = e.Text;
            string? group = null;
            string? stage = null;
            string? action = null;

            if (TryMatch(text, " 悬置，队列停止", out group))
            {
                stage = "状态不明（悬置）";
                action = "停下等你";
            }
            else if (TryMatch(text, " 疑似挂起，队列停止", out group))
            {
                stage = "疑似挂起";
                action = "停下等你";
            }
            else if (TryMatch(text, " 疑似挂起，已结束", out group))
            {
                stage = "疑似挂起";
                action = "结束进程，然后按失败策略继续";
            }
            else if (text.Contains("失败策略=中止", StringComparison.Ordinal))
            {
                stage = "上一个工具失败";
                action = "按失败策略中止，剩下的不再跑";
                group = "这一趟";
            }

            if (group == null || stage == null || action == null) continue;

            string key = group + "|" + stage;
            if (!seen.Add(key)) continue;

            list.Add(new DailyReportAnomaly
            {
                Group = group,
                Stage = stage,
                Action = action,
                RunId = e.RunId,
            });
        }

        return list;
    }

    /// <summary>日志行形如「队列：{名字} 悬置，队列停止」。切出名字那一段，切不出来就当没命中。</summary>
    private static bool TryMatch(string text, string tail, out string? name)
    {
        name = null;
        if (!text.Contains(tail, StringComparison.Ordinal)) return false;
        const string prefix = "队列：";
        int start = text.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return false;
        start += prefix.Length;
        int end = text.IndexOf(tail, start, StringComparison.Ordinal);
        if (end <= start) return false;
        name = text.Substring(start, end - start).Trim();
        return name.Length > 0;
    }

    // ===== 说人话的格式：界面与推送共用，免得同一件事两处说法不一 =====

    /// <summary>「5 分 59 秒」「33 秒」「1 小时 2 分」。和时长账本的口径一致。</summary>
    public static string SayDuration(int seconds)
    {
        if (seconds <= 0) return "0 秒";
        int h = seconds / 3600;
        int m = (seconds % 3600) / 60;
        int s = seconds % 60;
        if (h > 0) return m > 0 ? $"{h} 小时 {m} 分" : $"{h} 小时";
        if (m > 0) return s > 0 ? $"{m} 分 {s} 秒" : $"{m} 分";
        return $"{s} 秒";
    }

    /// <summary>
    /// 「快了 33 秒」「慢了 5 分 59 秒」「持平」。差不到 <see cref="FlatThresholdSeconds"/> 秒就算持平，
    /// 秒表的抖动不算「今天更快」。
    /// </summary>
    public static string SayDelta(int deltaSeconds, out bool? faster)
    {
        if (Math.Abs(deltaSeconds) < FlatThresholdSeconds)
        {
            faster = null;
            return "持平";
        }
        faster = deltaSeconds < 0;
        return (faster.Value ? "快了 " : "慢了 ") + SayDuration(Math.Abs(deltaSeconds));
    }

    /// <summary>合计行的趋势：「较昨日 慢了 3 分」。没有昨天的数就不说这句。</summary>
    public static string? SayTrend(int totalSeconds, int? previousTotalSeconds)
    {
        if (previousTotalSeconds == null) return null;
        int delta = totalSeconds - previousTotalSeconds.Value;
        if (Math.Abs(delta) < FlatThresholdSeconds) return "和昨天差不多";
        return "较昨日 " + SayDelta(delta, out _);
    }
}
