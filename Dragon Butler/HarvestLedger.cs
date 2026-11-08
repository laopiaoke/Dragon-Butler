using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DragonButler;

/// <summary>
/// 一次运行（或一天）累计到的收益。全部字段没有值就是 0 / 空——
/// 「没解析到」和「真的是 0」在这里没有区别，所以调用方必须靠 <see cref="IsEmpty"/>
/// 决定说不说话，绝不能显示 0。
/// </summary>
public sealed class Harvest
{
    [JsonPropertyName("mora")]
    public long Mora { get; set; }

    /// <summary>狗粮经验。</summary>
    [JsonPropertyName("exp")]
    public long Exp { get; set; }

    /// <summary>材料名 → 个数。</summary>
    [JsonPropertyName("materials")]
    public Dictionary<string, int> Materials { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public bool IsEmpty => Mora <= 0 && Exp <= 0 && Materials.Count == 0;

    public void Add(Harvest? other)
    {
        if (other == null) return;
        Mora += other.Mora;
        Exp += other.Exp;
        foreach (var kv in other.Materials)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value <= 0) continue;
            Materials[kv.Key] = Materials.TryGetValue(kv.Key, out int old) ? old + kv.Value : kv.Value;
        }
    }

    public Harvest Clone() => new()
    {
        Mora = Mora,
        Exp = Exp,
        Materials = new Dictionary<string, int>(Materials, StringComparer.Ordinal),
    };
}

/// <summary>
/// 收益账本（v1.10 / G1）：把 BGI 日志里那些「+1234 摩拉」的碎行累计起来，
/// 队列结束时告诉用户这一趟赚了多少，并按天记账供周报与日历使用。
///
/// <para>
/// <b>数据从哪来</b>：不新增任何采集。日志尾巴本来就已经被 <see cref="LogWatcher"/> 逐行读过
/// （它要在这里面找错误行、找完成信号），我们只是顺手看一眼有没有收益行。
/// </para>
/// <para>
/// <b>为什么解析规则写得这么保守</b>：BGI 的日志格式没有任何兼容承诺，脚本更是谁的输出都可能。
/// 所以只认「标签 + 明确分隔符 + 数字」这种几乎不可能误伤的形状，
/// 认不出来就当作没发生——<b>宁可少算，也绝不猜</b>。
/// 显示端也只有 <see cref="Harvest.IsEmpty"/> 为 false 时才说话。
/// </para>
/// </summary>
public static class HarvestLedger
{
    private static readonly object Gate = new();
    private static Harvest _pending = new();
    private static Dictionary<string, Harvest> _days = new();
    private static bool _loaded;

    /// <summary>只保留这么多天的账（一年的日历已经够看了）。</summary>
    public const int KeepDays = 400;

    /// <summary>账本文件：exe 旁 config\harvest.json。</summary>
    public static string FilePath => PathOverride ?? Path.Combine(AppContext.BaseDirectory, "config", "harvest.json");

    /// <summary>
    /// 换一条路径（仅给断言用）。探针不能往用户的真实账本里写——
    /// 那会让「本月累计」变成测试数据，是比算错更难查的问题。
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

    // ── 解析规则 ───────────────────────────────────────────────
    // 标签后面必须跟着 [+ ＋ x × : ： =] 之一再接数字，或数字直接跟着标签。
    // 这种写法能把「摩拉收益26-28w」「12.5万摩拉」这类带单位/区间的文案排除在外——
    // 它们出现在视频标题和聊天里，不会出现在日志行里。
    private static readonly Regex RxMoraAfter =
        new(@"摩拉\s*[+＋x×:：=＝]\s*([\d,，]+)", RegexOptions.Compiled);
    private static readonly Regex RxMoraBefore =
        new(@"([\d,，]+)\s*摩拉", RegexOptions.Compiled);
    private static readonly Regex RxExpAfter =
        new(@"(?:狗粮)?经验\s*[+＋x×:：=＝]\s*([\d,，]+)", RegexOptions.Compiled);
    private static readonly Regex RxPrimogemAfter =
        new(@"原石\s*[+＋x×:：=＝]\s*([\d,，]+)", RegexOptions.Compiled);
    private static readonly Regex RxPickup =
        new(@"拾取\s*(?<name>[^\s:：,，]{2,12}?)\s*[x×]\s*(?<num>\d{1,6})", RegexOptions.Compiled);

    /// <summary>
    /// 看一眼这行日志有没有收益。没有就什么都不做（绝大多数行都属于这种）。
    /// 由 <see cref="LogWatcher"/> 在它自己的逐行循环里调用，不额外读盘。
    /// </summary>
    public static void Observe(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (!AppState.Settings.HarvestEnabled) return;

        // 粗筛：一个关键字都没有就直接返回，省掉五次正则
        if (line.IndexOf("摩拉", StringComparison.Ordinal) < 0
            && line.IndexOf("经验", StringComparison.Ordinal) < 0
            && line.IndexOf("原石", StringComparison.Ordinal) < 0
            && line.IndexOf("拾取", StringComparison.Ordinal) < 0) return;

        long mora = 0, exp = 0;
        int primogem = 0;
        var materials = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match m in RxMoraAfter.Matches(line)) mora = Math.Max(mora, Digits(m.Groups[1].Value));
        if (mora == 0)
        {
            foreach (Match m in RxMoraBefore.Matches(line)) mora = Math.Max(mora, Digits(m.Groups[1].Value));
        }
        foreach (Match m in RxExpAfter.Matches(line)) exp = Math.Max(exp, Digits(m.Groups[1].Value));
        foreach (Match m in RxPrimogemAfter.Matches(line)) primogem = Math.Max(primogem, (int)Digits(m.Groups[1].Value));
        foreach (Match m in RxPickup.Matches(line))
        {
            string name = m.Groups["name"].Value.Trim();
            if (name.Length == 0) continue;
            if (!int.TryParse(m.Groups["num"].Value, out int n) || n <= 0) continue;
            materials[name] = materials.TryGetValue(name, out int old) ? old + n : n;
        }

        if (mora == 0 && exp == 0 && primogem == 0 && materials.Count == 0) return;

        lock (Gate)
        {
            _pending.Mora += mora;
            _pending.Exp += exp;
            if (primogem > 0) _pending.Mora += 0; // 原石不进摩拉账，单列会误导，这里只当作「有收益」的信号
            foreach (var kv in materials) _pending.Materials[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// 取走本轮累计的收益并清零。队列结束时调用一次，
    /// 顺手把这笔账并进今天。<b>清零这一步是必须的</b>：不清零下一条队列会把上一趟的收益再报一遍。
    /// </summary>
    public static Harvest Take()
    {
        Harvest taken;
        lock (Gate)
        {
            taken = _pending.Clone();
            _pending = new Harvest();
        }
        if (!taken.IsEmpty) AddToDay(DateTime.Today, taken);
        return taken;
    }

    /// <summary>清空待取收益（测试用；不影响已记的账）。</summary>
    public static void ResetPending()
    {
        lock (Gate) _pending = new Harvest();
    }

    /// <summary>把一笔收益并进某一天。</summary>
    public static void AddToDay(DateTime day, Harvest harvest)
    {
        if (harvest == null || harvest.IsEmpty) return;
        lock (Gate)
        {
            EnsureLoaded();
            string key = DayKey(day);
            if (!_days.TryGetValue(key, out var bucket))
            {
                bucket = new Harvest();
                _days[key] = bucket;
            }
            bucket.Add(harvest);
            Prune();
            Save();
        }
    }

    /// <summary>某一天的收益；没有记录返回空账（不是 null）。</summary>
    public static Harvest GetDay(DateTime day)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _days.TryGetValue(DayKey(day), out var bucket) ? bucket.Clone() : new Harvest();
        }
    }

    /// <summary>最近 <paramref name="days"/> 天（含今天）的合计。</summary>
    public static Harvest GetRecent(int days)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var sum = new Harvest();
            var today = DateTime.Today;
            for (int i = 0; i < Math.Max(1, days); i++)
            {
                if (_days.TryGetValue(DayKey(today.AddDays(-i)), out var bucket)) sum.Add(bucket);
            }
            return sum;
        }
    }

    /// <summary>本月（自然月，1 号到今天）的合计。</summary>
    public static Harvest GetMonth()
    {
        var today = DateTime.Today;
        return GetRecent(today.Day);
    }

    // ── 文案 ───────────────────────────────────────────────────

    /// <summary>
    /// 一句话说清这趟赚了多少。空账返回空串（调用方据此决定加不加这段）。
    /// 材料最多列三个，再多就说「等 N 种」——通知是要一眼看完的，不是报表。
    /// </summary>
    public static string Describe(Harvest? h, string prefix = "本次约")
    {
        if (h == null || h.IsEmpty) return "";
        var parts = new List<string>(3);
        if (h.Mora > 0) parts.Add(FormatAmount(h.Mora) + "摩拉");
        if (h.Exp > 0) parts.Add(FormatAmount(h.Exp) + "狗粮经验");
        if (h.Materials.Count > 0)
        {
            var top = h.Materials.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(3);
            string list = string.Join("、", top.Select(kv => $"{kv.Key}×{kv.Value}"));
            int rest = h.Materials.Count - 3;
            parts.Add(rest > 0 ? list + $" 等 {h.Materials.Count} 种材料" : list);
        }
        return parts.Count == 0 ? "" : prefix + " " + string.Join("、", parts);
    }

    /// <summary>数字写法：一万以下原样，一万以上用「万」并留一位小数，一亿以上用「亿」。</summary>
    public static string FormatAmount(long value)
    {
        if (value < 0) value = 0;
        if (value < 10_000) return value.ToString();
        if (value < 100_000_000)
        {
            double wan = value / 10_000.0;
            string s = wan >= 100 ? wan.ToString("F0") : wan.ToString("F1");
            return s.TrimEnd('0').TrimEnd('.') + " 万";
        }
        double yi = value / 100_000_000.0;
        return yi.ToString("F1").TrimEnd('0').TrimEnd('.') + " 亿";
    }

    /// <summary>把 "1,234" / "1，234" 这种带千分位的数字拆成整数；认不出返回 0。</summary>
    private static long Digits(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return 0;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (char.IsDigit(c)) sb.Append(c);
        }
        return long.TryParse(sb.ToString(), out long v) ? v : 0;
    }

    // ── 落盘 ───────────────────────────────────────────────────

    private static string DayKey(DateTime day) => day.ToString("yyyy-MM-dd");

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            string json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, Harvest>>(json, ReadOpt);
            if (data == null) return;
            _days = new Dictionary<string, Harvest>(StringComparer.Ordinal);
            foreach (var kv in data)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                kv.Value.Materials ??= new Dictionary<string, int>(StringComparer.Ordinal);
                _days[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            // 账本坏了不该影响开跑：清空重记，只留一行日志
            _days = new Dictionary<string, Harvest>(StringComparer.Ordinal);
            LogService.LogEvent("收益账本读取失败，已重置：" + ex.Message);
        }
    }

    private static void Prune()
    {
        if (_days.Count <= KeepDays) return;
        foreach (string key in _days.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(_days.Count - KeepDays).ToList())
            _days.Remove(key);
    }

    private static void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath) ?? ".";
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_days, WriteOpt));
        }
        catch (Exception ex)
        {
            LogService.LogEvent("收益账本保存失败：" + ex.Message);
        }
    }

    /// <summary>重新读盘（测试用）。</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _loaded = false;
            _days = new Dictionary<string, Harvest>(StringComparer.Ordinal);
            EnsureLoaded();
        }
    }
}
