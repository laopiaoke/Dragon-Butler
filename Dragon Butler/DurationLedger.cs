using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragonButler;

/// <summary>
/// 某一天里某个工具跑了多久。合计时长直接存，不在显示端累加——
/// 「今天一共几分钟」这句话只有一个来源，算错了只需要看一个地方。
/// </summary>
public sealed class DurationEntry
{
    [JsonPropertyName("toolId")]
    public string ToolId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>这个工具当天累计跑了多少秒（多次运行相加）。</summary>
    [JsonPropertyName("seconds")]
    public int Seconds { get; set; }

    /// <summary>跑了几次。同样是合计，但回答的是「今天跑过几轮」。</summary>
    [JsonPropertyName("runs")]
    public int Runs { get; set; }

    /// <summary>最后一次结束时间 HH:mm:ss。</summary>
    [JsonPropertyName("lastAt")]
    public string? LastAt { get; set; }

    /// <summary>
    /// 配置组名 → 当天累计秒数。BGI 一条龙里真正跑东西的是「配置组」，
    /// 一个工具下面可能挂着好几个组，用户想知道的是「哪个组最费时间」。
    /// </summary>
    [JsonPropertyName("groups")]
    public Dictionary<string, int> Groups { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public TimeSpan Total => TimeSpan.FromSeconds(Seconds);
}

/// <summary>
/// 耗时账本（v1.40）：exe 旁 config\durations.json，按天记「每个工具/每个配置组跑了多久」。
///
/// <para>
/// <b>为什么不能直接用 RunJournal</b>：那份台账记的是「最后什么结果」——状态、原因、结束时刻，
/// 但没有时长；而且同一工具当天只留最后一条，天然装不下「今天跑了三轮、一共两小时」。
/// 「汇报」页要回答的正是这个问题，所以另立一本账。
/// </para>
/// <para>
/// <b>为什么时长由队列给，不由日志猜</b>：<see cref="QueueService"/> 本来就拿 Stopwatch 在算
/// 每一项跑了多久（超时判定、重试退避都要用），那是唯一的权威值。
/// 从日志行推算开始结束时间会碰上「中途卡住」「重试算一次还是两次」这些边界，
/// 而队列在这一刻已经知道答案了。
/// </para>
/// <para>
/// <b>配置组的时间从哪来</b>：<see cref="GroupArchive"/> 已经在记每个组的开始/结束时刻
/// （webhook 与日志尾随双源），这里只是把「本次运行期间开过又关掉的组」挑出来，
/// 用结束减开始算成秒。没结束的组（跑到一半被关掉）不计——记一个猜出来的数比不记更糟。
/// </para>
/// </summary>
public static class DurationLedger
{
    private static readonly object Gate = new();
    private static Dictionary<string, List<DurationEntry>> _data = new();
    private static bool _loaded;

    /// <summary>保留天数。「汇报」看今天和最近一周，60 天足够翻旧账。</summary>
    public const int KeepDays = 60;

    public static string FilePath => PathOverride ?? Paths.Config("durations.json");

    /// <summary>
    /// 覆盖落盘路径。只给验证用——探针往用户的真实账本里写，
    /// 会让「今天跑了多久」变成测试数据，比算错更难查。
    /// 与 <see cref="RunJournal.PathOverride"/>、<see cref="GroupArchive.PathOverride"/> 同一套路。
    /// </summary>
    public static string? PathOverride { get; set; }

    private static readonly JsonSerializerOptions WriteOpt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions ReadOpt = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>显式重新加载（换过 PathOverride 之后要调一次）。</summary>
    public static void Load() { lock (Gate) { _loaded = false; EnsureLoaded(); } }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        try
        {
            if (File.Exists(FilePath))
            {
                _data = JsonSerializer.Deserialize<Dictionary<string, List<DurationEntry>>>(
                    File.ReadAllText(FilePath), ReadOpt) ?? new Dictionary<string, List<DurationEntry>>();
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("durations.json 读取失败，已重建：" + ex.Message);
            _data = new Dictionary<string, List<DurationEntry>>();
        }
        _loaded = true;
    }

    private static void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Prune();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, WriteOpt));
        }
        catch (Exception ex)
        {
            LogService.LogEvent("保存 durations.json 失败：" + ex.Message);
        }
    }

    private static void Prune()
    {
        var cutoff = DateTime.Today.AddDays(-KeepDays).ToString("yyyy-MM-dd");
        foreach (var key in _data.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            _data.Remove(key);
    }

    /// <summary>
    /// 记一笔时长。由队列在单项结束时调用。
    ///
    /// <para>
    /// <b>跳过的不记</b>：<c>Skipped</c> 表示前置检查没过、根本没启动，
    /// 记它会让「今天这一个工具跑了 0 秒」变成一条看起来像跑了但立刻挂掉的记录。
    /// </para>
    /// </summary>
    /// <param name="toolId">工具标识。</param>
    /// <param name="displayName">显示名。</param>
    /// <param name="elapsed">这一项实际耗时（队列的秒表给的）。</param>
    /// <param name="startedAt">这一项开始的时刻，用来挑出属于它的配置组。</param>
    public static void Record(string toolId, string displayName, TimeSpan elapsed, DateTime startedAt)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return;
        if (elapsed <= TimeSpan.Zero) return;

        int seconds = (int)Math.Round(elapsed.TotalSeconds);
        if (seconds <= 0) seconds = 1;   // 不到半秒也算跑过一动，别显示成 0

        var groups = GroupsSince(startedAt);

        lock (Gate)
        {
            EnsureLoaded();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!_data.TryGetValue(today, out var list))
            {
                list = new List<DurationEntry>();
                _data[today] = list;
            }
            var entry = list.FirstOrDefault(e => string.Equals(e.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new DurationEntry { ToolId = toolId };
                list.Add(entry);
            }
            entry.Name = string.IsNullOrWhiteSpace(displayName) ? toolId : displayName;
            entry.Seconds += seconds;
            entry.Runs += 1;
            entry.LastAt = DateTime.Now.ToString("HH:mm:ss");
            foreach (var kv in groups)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value <= 0) continue;
                entry.Groups[kv.Key] = entry.Groups.TryGetValue(kv.Key, out int old) ? old + kv.Value : kv.Value;
            }
            Save();
        }
    }

    /// <summary>
    /// 挑出「从 <paramref name="startedAt"/> 到现在」这段时间里开过又关掉的配置组，
    /// 返回「组名 → 秒数」。同名组多段会相加（<see cref="GroupArchive"/> 的 RecordStart
    /// 每次新段只保留最后一段的 start，所以这里只能拿到最后一段，够用了）。
    /// </summary>
    private static Dictionary<string, int> GroupsSince(DateTime startedAt)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            string from = startedAt.ToString("HH:mm:ss");
            foreach (var rec in GroupArchive.GetTodayGroups())
            {
                if (string.IsNullOrWhiteSpace(rec.Name)) continue;
                if (rec.Start == null || rec.End == null) continue;   // 没结束的不猜
                // HH:mm:ss 是定长零填充的，字符串比较就是时间比较
                if (string.CompareOrdinal(rec.Start, from) < 0) continue;

                var a = TimeSpan.TryParse(rec.Start, out var t1) ? t1 : TimeSpan.MinValue;
                var b = TimeSpan.TryParse(rec.End, out var t2) ? t2 : TimeSpan.MinValue;
                if (a == TimeSpan.MinValue || b == TimeSpan.MinValue) continue;
                int sec = (int)Math.Round((b - a).TotalSeconds);
                if (sec <= 0) continue;
                result[rec.Name] = result.TryGetValue(rec.Name, out int old) ? old + sec : sec;
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("统计配置组时长失败：" + ex.Message);
        }
        return result;
    }

    /// <summary>某天的全部条目（拷贝，外部改不到内部状态）。按耗时降序——最费时间的那一行该在最上面。</summary>
    public static List<DurationEntry> GetDay(DateTime day)
    {
        lock (Gate)
        {
            EnsureLoaded();
            string key = day.ToString("yyyy-MM-dd");
            if (!_data.TryGetValue(key, out var list)) return new List<DurationEntry>();
            return list.Select(e => new DurationEntry
            {
                ToolId = e.ToolId,
                Name = e.Name,
                Seconds = e.Seconds,
                Runs = e.Runs,
                LastAt = e.LastAt,
                Groups = new Dictionary<string, int>(e.Groups, StringComparer.Ordinal),
            }).OrderByDescending(e => e.Seconds).ToList();
        }
    }

    /// <summary>某天合计跑了多久。</summary>
    public static TimeSpan TotalOf(DateTime day)
        => TimeSpan.FromSeconds(GetDay(day).Sum(e => e.Seconds));

    /// <summary>
    /// 最近 <paramref name="days"/> 天每天合计多久（含今天，顺序从旧到新）。
    /// 「汇报」页那条周条形靠它——一天的数是点，七天的数是趋势。
    /// </summary>
    public static List<(DateTime Day, TimeSpan Total)> Recent(int days)
    {
        if (days <= 0) days = 1;
        var result = new List<(DateTime, TimeSpan)>();
        var today = DateTime.Today;
        for (int i = days - 1; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            result.Add((d, TotalOf(d)));
        }
        return result;
    }

    /// <summary>
    /// 往某一天塞一条记录。只给验证用（渲染「汇报」页要造几天像样的数据），
    /// 生产代码一律走 <see cref="Record"/>。与 <see cref="RunJournal.SeedForTest"/> 同一立场：只写内存，不落盘。
    /// </summary>
    public static void SeedForTest(DateTime day, string toolId, string name, int seconds, int runs = 1,
        string? lastAt = null, Dictionary<string, int>? groups = null)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return;
        lock (Gate)
        {
            EnsureLoaded();
            string key = day.ToString("yyyy-MM-dd");
            if (!_data.TryGetValue(key, out var list))
            {
                list = new List<DurationEntry>();
                _data[key] = list;
            }
            int idx = list.FindIndex(e => string.Equals(e.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
            var copy = new DurationEntry
            {
                ToolId = toolId,
                Name = string.IsNullOrWhiteSpace(name) ? toolId : name,
                Seconds = seconds,
                Runs = runs,
                LastAt = lastAt,
                Groups = groups == null
                    ? new Dictionary<string, int>(StringComparer.Ordinal)
                    : new Dictionary<string, int>(groups, StringComparer.Ordinal),
            };
            if (idx >= 0) list[idx] = copy; else list.Add(copy);
        }
    }

    /// <summary>清空（仅验证用）。</summary>
    public static void ClearForTest()
    {
        lock (Gate)
        {
            _data.Clear();
            _loaded = true;
        }
    }
}
