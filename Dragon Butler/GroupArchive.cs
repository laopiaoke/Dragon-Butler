using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragonButler;

/// <summary>
/// 配置组存档记录（SPBGI）：对应 4.5 节 JSON 格式
/// </summary>
public class GroupRecord
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>开始时间 HH:mm:ss</summary>
    [JsonPropertyName("start")]
    public string? Start { get; set; }

    /// <summary>结束时间 HH:mm:ss；null = 开始但未结束（中断/进行中）</summary>
    [JsonPropertyName("end")]
    public string? End { get; set; }

    /// <summary>同名组被执行的段数（中断重跑会加段）</summary>
    [JsonPropertyName("segments")]
    public int Segments { get; set; } = 1;

    /// <summary>判定来源：webhook / log / unknown</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "unknown";
}

/// <summary>
/// 配置组存档（SPBGI）：exe 旁 config\group_archive.json，按天存。
/// 同名组多段合并 + RecordStart 幂等（防日志/Webhook 双源重复计数）。
/// </summary>
public static class GroupArchive
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions WriteOpt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions ReadOpt = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static Dictionary<string, List<GroupRecord>> _data = new();
    private static bool _loaded;

    /// <summary>group_archive.json 路径</summary>
    public static string FilePath => PathOverride ?? Path.Combine(AppContext.BaseDirectory, "config", "group_archive.json");

    /// <summary>
    /// 覆盖落盘路径。只给验证用（渲染日历要造一年的假记录），
    /// 不设的时候行为完全不变。跟 HarvestLedger.PathOverride 是同一个套路：
    /// 测试污染真实数据比功能算错更难查。
    /// </summary>
    public static string? PathOverride { get; set; }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        try
        {
            if (File.Exists(FilePath))
            {
                _data = JsonSerializer.Deserialize<Dictionary<string, List<GroupRecord>>>(
                    File.ReadAllText(FilePath), ReadOpt) ?? new Dictionary<string, List<GroupRecord>>();
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("group_archive.json 读取失败，已重建：" + ex.Message);
            _data = new Dictionary<string, List<GroupRecord>>();
        }
        _loaded = true;
    }

    /// <summary>显式加载（软件启动时调用）</summary>
    public static void Load() { lock (Gate) { _loaded = false; EnsureLoaded(); } }

    private static void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, WriteOpt));
        }
        catch (Exception ex)
        {
            LogService.LogEvent("保存 group_archive.json 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 记录配置组开始（幂等）：
    /// 当天无同名 → 新增（end=null）；
    /// 有同名且上一段已结束 → 新一段：segments++、start 更新为本段开始时间、end 清空；
    /// 有同名且尚未结束 → 仅更新 source（防日志/Webhook 双源重复计数，事件以先到者为准）。
    /// </summary>
    public static void RecordStart(string name, string source)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (Gate)
        {
            EnsureLoaded();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!_data.TryGetValue(today, out var list))
            {
                list = new List<GroupRecord>();
                _data[today] = list;
            }
            var rec = list.FirstOrDefault(r => r.Name == name);
            if (rec == null)
            {
                list.Add(new GroupRecord
                {
                    Name = name,
                    Start = DateTime.Now.ToString("HH:mm:ss"),
                    End = null,
                    Segments = 1,
                    Source = source,
                });
            }
            else if (rec.End != null)
            {
                // 上一段已结束 → 这是新的一段
                rec.Segments++;
                rec.Start = DateTime.Now.ToString("HH:mm:ss");
                rec.End = null;
                rec.Source = source;
            }
            else
            {
                // 正在进行中 → 双源重复事件，仅记录来源
                rec.Source = source;
            }
            Save();
        }
    }

    /// <summary>记录配置组结束：置 end=当前时间（取最后一段的结束时间）</summary>
    public static void RecordEnd(string name, string source)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (Gate)
        {
            EnsureLoaded();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_data.TryGetValue(today, out var list))
            {
                var rec = list.FirstOrDefault(r => r.Name == name);
                if (rec != null && rec.End == null)
                {
                    rec.End = DateTime.Now.ToString("HH:mm:ss");
                    rec.Source = source;
                    Save();
                }
            }
        }
    }

    /// <summary>
    /// 直接塞一条指定日期的记录。只给验证用（渲染日历要造一年的假数据），
    /// 正常流程走 RecordStart/RecordEnd。
    /// </summary>
    public static void Seed(DateTime day, string name, string start, string? end, string source = "seed")
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (Gate)
        {
            EnsureLoaded();
            string key = day.ToString("yyyy-MM-dd");
            if (!_data.TryGetValue(key, out var list))
            {
                list = new List<GroupRecord>();
                _data[key] = list;
            }
            list.Add(new GroupRecord { Name = name, Start = start, End = end, Segments = 1, Source = source });
        }
    }

    /// <summary>当天全部配置组记录</summary>
    public static List<GroupRecord> GetTodayGroups() => GetDay(DateTime.Now);

    /// <summary>指定某天的配置组记录（v1.5 晨报用；无记录返回空列表）。</summary>
    public static List<GroupRecord> GetDay(DateTime day)
    {
        lock (Gate)
        {
            EnsureLoaded();
            string key = day.ToString("yyyy-MM-dd");
            if (_data.TryGetValue(key, out var list))
                return list.Select(r => new GroupRecord
                {
                    Name = r.Name, Start = r.Start, End = r.End, Segments = r.Segments, Source = r.Source,
                }).ToList();
            return new List<GroupRecord>();
        }
    }

    /// <summary>今天尚未结束的组名列表（供"继续跑"）</summary>
    public static List<string> GetUnfinished()
    {
        lock (Gate)
        {
            return GetTodayGroups().Where(r => r.End == null).Select(r => r.Name).ToList();
        }
    }

    // ===== v1.13：耗时（原本只有晨报内部算过一遍，这里提出来给实时条与今日明细共用） =====

    /// <summary>
    /// 一条记录的真实耗时。开始或结束缺失 → null（进行中的段、只记了开始的段都算不出来）。
    /// 跨零点（end &lt; start）按隔天处理，和晨报的算法保持一致。
    /// </summary>
    public static TimeSpan? DurationOf(GroupRecord? r)
    {
        if (r == null) return null;
        if (string.IsNullOrWhiteSpace(r.Start) || string.IsNullOrWhiteSpace(r.End)) return null;
        if (!TimeSpan.TryParse(r.Start, out var start) || !TimeSpan.TryParse(r.End, out var end)) return null;
        var span = end - start;
        if (span < TimeSpan.Zero) span += TimeSpan.FromDays(1);
        return span;
    }

    /// <summary>
    /// 某个组最近 <paramref name="days"/> 天（不含今天）跑完全程的耗时样本，单位分钟。
    ///
    /// <para>
    /// 只取「同一天里没重开过段」的记录：Segments &gt; 1 意味着这组中途断过重跑，
    /// 那条记录的起止跨着两次执行，拿来当「跑一遍要多久」会严重偏大。
    /// </para>
    /// <para>
    /// 也不取今天的：今天正在跑的那一段会污染样本，而今天已跑完的段拿去估今天剩下的段，
    /// 容易把「今天这台机器特别慢」的情况一路放大。
    /// </para>
    /// </summary>
    public static List<double> RecentDurations(string name, int days = 14)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(name) || days <= 0) return result;
        lock (Gate)
        {
            EnsureLoaded();
            for (int i = 1; i <= days; i++)
            {
                string key = DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd");
                if (!_data.TryGetValue(key, out var list)) continue;
                foreach (var r in list)
                {
                    if (!string.Equals(r.Name, name, StringComparison.Ordinal)) continue;
                    if (r.Segments > 1) continue;
                    var span = DurationOf(r);
                    if (span == null) continue;
                    if (span.Value.TotalMinutes <= 0) continue;
                    result.Add(span.Value.TotalMinutes);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 这个组「通常要跑多久」（分钟，中位数）。样本少于 <paramref name="minSamples"/> 条 → null。
    ///
    /// <para>
    /// 用中位数而不是平均：配置组耗时很容易被一次切场景/掉线拖成几倍，
    /// 平均会被那一次拽走，中位数不会。样本不够就返回 null——宁可界面上不显示预计，
    /// 也不要给一个一看就不对的数（这条和「解析不到就不显示，绝不猜」是同一条规矩）。
    /// </para>
    /// </summary>
    public static double? MedianMinutes(string name, int days = 14, int minSamples = 2)
    {
        var samples = RecentDurations(name, days);
        if (samples.Count < minSamples) return null;
        samples.Sort();
        int mid = samples.Count / 2;
        return samples.Count % 2 == 1
            ? samples[mid]
            : (samples[mid - 1] + samples[mid]) / 2.0;
    }
}
