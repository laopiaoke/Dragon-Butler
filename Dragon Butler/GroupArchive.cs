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
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config", "group_archive.json");

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

    /// <summary>当天全部配置组记录</summary>
    public static List<GroupRecord> GetTodayGroups()
    {
        lock (Gate)
        {
            EnsureLoaded();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_data.TryGetValue(today, out var list))
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
}
