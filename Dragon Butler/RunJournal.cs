using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragonButler;

/// <summary>一天里某个工具的最后一次结果。</summary>
public class JournalEntry
{
    [JsonPropertyName("toolId")]
    public string ToolId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>终态名（Done / Failed / Timeout / Halted / Stalled）。</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    /// <summary>结束时间 HH:mm:ss。</summary>
    [JsonPropertyName("at")]
    public string? At { get; set; }

    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// 运行台账（v1.6 / F2）：exe 旁 config\run_journal.json，按天记「哪些工具今天已经跑成了」。
///
/// 为什么需要它：<see cref="GroupArchive"/> 记的是 BGI 的<b>配置组</b>（webhook / 日志尾随来的），
/// 只知道组名，不知道是哪个工具跑的，也不覆盖自定义工具。而「接着跑」要回答的是
/// 「今天哪些队列项还没跑成」，这需要按 ToolId 记一笔。两者互补：
/// 台账管「哪些工具」，存档管「BGI 内部哪些组」。
/// </summary>
public static class RunJournal
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

    /// <summary>保留天数：与日志清理同量级即可，够查一周前的「那天跑到哪」。</summary>
    public const int KeepDays = 30;

    private static Dictionary<string, List<JournalEntry>> _data = new();
    private static bool _loaded;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config", "run_journal.json");

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        try
        {
            if (File.Exists(FilePath))
            {
                _data = JsonSerializer.Deserialize<Dictionary<string, List<JournalEntry>>>(
                    File.ReadAllText(FilePath), ReadOpt) ?? new Dictionary<string, List<JournalEntry>>();
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("run_journal.json 读取失败，已重建：" + ex.Message);
            _data = new Dictionary<string, List<JournalEntry>>();
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
            LogService.LogEvent("保存 run_journal.json 失败：" + ex.Message);
        }
    }

    /// <summary>丢掉太老的日期键，避免文件无限长。</summary>
    private static void Prune()
    {
        var cutoff = DateTime.Today.AddDays(-KeepDays).ToString("yyyy-MM-dd");
        foreach (var key in _data.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            _data.Remove(key);
    }

    /// <summary>记一笔：同一工具当天只保留最后一次结果（重跑覆盖，语义是「现在是什么状态」）。</summary>
    public static void Record(QueueItemResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.ToolId)) return;
        if (result.Skipped) return; // 跳过不算「跑过」，下次接着跑仍要尝试
        lock (Gate)
        {
            EnsureLoaded();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!_data.TryGetValue(today, out var list))
            {
                list = new List<JournalEntry>();
                _data[today] = list;
            }
            var entry = list.FirstOrDefault(e => string.Equals(e.ToolId, result.ToolId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new JournalEntry { ToolId = result.ToolId };
                list.Add(entry);
            }
            entry.Name = result.DisplayName;
            entry.State = result.State.ToString();
            entry.At = DateTime.Now.ToString("HH:mm:ss");
            entry.Attempts = result.Attempts;
            entry.Reason = result.Reason;
            Save();
        }
    }

    /// <summary>某天的全部台账条目（拷贝，外部改不到内部状态）。</summary>
    public static List<JournalEntry> GetDay(DateTime day)
    {
        lock (Gate)
        {
            EnsureLoaded();
            string key = day.ToString("yyyy-MM-dd");
            if (_data.TryGetValue(key, out var list))
                return list.Select(e => new JournalEntry
                {
                    ToolId = e.ToolId, Name = e.Name, State = e.State, At = e.At,
                    Attempts = e.Attempts, Reason = e.Reason,
                }).ToList();
            return new List<JournalEntry>();
        }
    }

    /// <summary>今天已经跑成（终态 Done）的工具 id 集合。</summary>
    public static HashSet<string> DoneToolsToday()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in GetDay(DateTime.Now))
            if (string.Equals(e.State, nameof(ToolRunState.Done), StringComparison.OrdinalIgnoreCase))
                set.Add(e.ToolId);
        return set;
    }

    /// <summary>清空今天的台账（界面上「今天从头再来一次」用）。</summary>
    public static void ClearToday()
    {
        lock (Gate)
        {
            EnsureLoaded();
            _data.Remove(DateTime.Now.ToString("yyyy-MM-dd"));
            Save();
        }
    }
}
