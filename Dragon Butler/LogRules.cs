using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 一条日志规则：日志里出现某个词，就做一件事。
///
/// <para>
/// 为什么只有三个行为：ABGI 那套「行为目录」有二十几种，本质是让用户用下拉框拼一个小脚本。
/// 那是给会写规则的人用的。这里的立场相反——<see cref="LogWatcher"/> 已经在逐行读日志了，
/// 用户真正缺的不是「能编排」，而是「不用一直盯着屏幕也知道出事了」。
/// 所以只留三种动作，每一种都对应一个已经存在的能力：
/// </para>
/// <list type="bullet">
/// <item><b>提醒</b> → <see cref="NotifyService"/>，推到手机或系统通知。</item>
/// <item><b>留现场</b> → <see cref="IncidentBundle"/>，把日志尾巴、配置、截图存一份。</item>
/// <item><b>停下队列</b> → <see cref="QueueService.Stop"/>，不再让后面的工具白跑。</item>
/// </list>
/// </summary>
public sealed class LogRule
{
    // 行为取值（存字符串而不是枚举，settings.json 里可读、加新值不会把老配置读崩）

    public const string Notify = "notify";
    public const string Capture = "capture";
    public const string Halt = "halt";

    public string Id { get; set; } = "";
    public string Keyword { get; set; } = "";

    /// <summary>notify / capture / halt，见上面的三个常量。</summary>
    public string Behavior { get; set; } = Notify;

    public bool Enabled { get; set; } = true;

    /// <summary>备注：自己写这个规则是想抓什么，三个月后回来看还认得出。</summary>
    public string Note { get; set; } = "";

    /// <summary>关键词最长多少字。日志行本来就短，太长的关键词必然是粘错了地方。</summary>
    public const int MaxKeywordChars = 40;

    /// <summary>清洗：去掉首尾空白、压掉换行；超长截断。返回空串表示这条规则无效。</summary>
    public static string Normalize(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return "";
        string s = keyword.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");
        if (s.Length > MaxKeywordChars) s = s[..MaxKeywordChars];
        return s;
    }

    /// <summary>行为的中文名（界面与通知里都用它）。认不出来的取值按「提醒」处理。</summary>
    public static string BehaviorName(string? behavior) => behavior switch
    {
        Capture => "留现场",
        Halt => "停下队列",
        _ => "提醒",
    };

    /// <summary>行为的一句话解释，界面下拉里用得到。</summary>
    public static string BehaviorHint(string? behavior) => behavior switch
    {
        Capture => "存一份日志尾巴 + 截图到 全部日志\\incidents\\",
        Halt => "立刻停止队列（正在跑才有效）",
        _ => "发一条通知，不打扰队列",
    };

    /// <summary>所有可选行为，界面按这个顺序列。</summary>
    public static readonly string[] AllBehaviors = { Notify, Capture, Halt };
}

/// <summary>
/// 日志规则的判定与执行（v1.14）。
///
/// <para>
/// <b>为什么不是「规则引擎」</b>：这里没有表达式、没有上下文变量、没有执行顺序。
/// 一条日志行进来，逐条比对关键词，命中就做那件事——就这么多。
/// 顺序不影响结果，因为每种行为都是幂等的副作用。
/// </para>
/// <para>
/// <b>为什么必须节流</b>：BGI 卡住时那条日志可能一秒刷十遍。不节流的话，
/// 用户会在三分钟里收到四百条「卡住了」——然后把这个功能关掉，再也不打开。
/// 所以同一规则在 <see cref="CooldownMinutes"/> 内只触发一次。
/// </para>
/// </summary>
public static class LogRules
{
    /// <summary>同一规则多少分钟内只触发一次。</summary>
    public const double CooldownMinutes = 10;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> _lastFire = new(StringComparer.Ordinal);

    /// <summary>当前规则表（永远不为 null；settings.json 里缺这一节时是空表）。</summary>
    public static List<LogRule> Rules
    {
        get
        {
            try
            {
                AppState.Settings.LogRules ??= new List<LogRule>();
                return AppState.Settings.LogRules;
            }
            catch { return new List<LogRule>(); }
        }
    }

    /// <summary>启用且关键词有效的规则。</summary>
    public static List<LogRule> ActiveRules()
        => Rules.Where(r => r != null && r.Enabled && LogRule.Normalize(r.Keyword).Length > 0).ToList();

    /// <summary>
    /// 这一行命中了哪些规则（纯函数，不改状态、不节流，便于断言）。
    /// 大小写不敏感；关键词为空或不启用的规则一律跳过。
    /// </summary>
    public static List<LogRule> Match(string? line, IEnumerable<LogRule>? rules = null)
    {
        var hit = new List<LogRule>();
        if (string.IsNullOrEmpty(line)) return hit;
        foreach (var r in rules ?? ActiveRules())
        {
            if (r == null || !r.Enabled) continue;
            string k = LogRule.Normalize(r.Keyword);
            if (k.Length == 0) continue;
            if (line.Contains(k, StringComparison.OrdinalIgnoreCase)) hit.Add(r);
        }
        return hit;
    }

    /// <summary>该规则现在能不能触发（节流判定，纯函数）。</summary>
    public static bool CooledDown(string ruleId, DateTime now, IReadOnlyDictionary<string, DateTime>? fires = null)
    {
        var map = fires ?? Snapshot();
        if (!map.TryGetValue(ruleId ?? "", out var at)) return true;
        return (now - at).TotalMinutes >= CooldownMinutes;
    }

    /// <summary>当前节流表的拷贝（断言用）。</summary>
    public static Dictionary<string, DateTime> Snapshot()
    {
        lock (Gate) return new Dictionary<string, DateTime>(_lastFire, StringComparer.Ordinal);
    }

    /// <summary>清空节流记录（队列重新开始跑时调，让同一规则在新一轮里能再报一次）。</summary>
    public static void ResetThrottle()
    {
        lock (Gate) _lastFire.Clear();
    }

    /// <summary>
    /// 挂在 <see cref="LogWatcher.ProcessLine"/> 的最前面。
    /// 任何异常都吞掉：规则是我们自己的功能，不能因为它把一个正在跑的队列带崩。
    /// </summary>
    public static void Observe(string? line)
    {
        try
        {
            if (string.IsNullOrEmpty(line)) return;
            if (Rules.Count == 0) return;

            List<LogRule> hits;
            try { hits = Match(line); } catch { return; }
            if (hits.Count == 0) return;

            foreach (var rule in hits)
            {
                string id = string.IsNullOrEmpty(rule.Id) ? LogRule.Normalize(rule.Keyword) : rule.Id;
                lock (Gate)
                {
                    if (_lastFire.TryGetValue(id, out var last)
                        && (DateTime.Now - last).TotalMinutes < CooldownMinutes) continue;
                    _lastFire[id] = DateTime.Now;
                }
                Fire(rule, line);
            }
        }
        catch (Exception ex)
        {
            try { LogService.LogEvent("日志规则：判定失败（" + ex.Message + "）"); } catch { }
        }
    }

    /// <summary>真的去做那件事。三种行为都不允许抛。</summary>
    private static void Fire(LogRule rule, string line)
    {
        string keyword = LogRule.Normalize(rule.Keyword);
        string evidence = RunEvidence.Compact(line);
        string title = "日志出现「" + keyword + "」";

        try { LogService.LogEvent($"日志规则命中：{keyword} → {LogRule.BehaviorName(rule.Behavior)}"); } catch { }

        switch (rule.Behavior)
        {
            case LogRule.Halt:
                {
                    bool running = false;
                    try { running = QueueService.IsRunning; } catch { }
                    try
                    {
                        NotifyService.Show(NotifyLevel.Critical, title,
                            running ? "队列已停止。最后一行：" + evidence : "队列当时没在跑。最后一行：" + evidence);
                    }
                    catch { }
                    if (running)
                    {
                        // Stop 会切线程收尾；规则判定在日志泵线程上，不能在这里等它
                        try { System.Threading.Tasks.Task.Run(() => { try { QueueService.Stop(); } catch { } }); } catch { }
                    }
                    break;
                }

            case LogRule.Capture:
                {
                    try
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try { IncidentBundle.Capture("日志关键词「" + keyword + "」"); } catch { }
                        });
                    }
                    catch { }
                    try { NotifyService.Show(NotifyLevel.Warning, title, "已保存现场。" + evidence); } catch { }
                    break;
                }

            default: // Notify
                try { NotifyService.Show(NotifyLevel.Warning, title, evidence); } catch { }
                break;
        }
    }

    // ===== 「试一下」：这是 ABGI 那套规则编辑器没有的东西 =====
    //
    // 写关键词的困难不在语法，在于「你不知道日志里到底长什么样」。
    // 在网页上填完关键词只能等下一条日志来验证——可能等一晚上都没有。
    // 这里直接扫今天的日志文件，告诉你命中几次、原文长什么样。

    /// <summary>一次扫描里，单条规则的结果。</summary>
    public sealed record ScanHit(string Keyword, int Count, List<string> Samples);

    /// <summary>今天的 BGI 日志文件路径；找不到返回 null（不抛）。</summary>
    public static string? TodayLogFile(string? logDirOverride = null)
    {
        try
        {
            string dir = logDirOverride ?? "";
            if (dir.Length == 0)
            {
                string? baseDir = Path.GetDirectoryName(AppState.Settings.BgiPath);
                dir = string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir, "log");
            }
            if (dir.Length == 0 || !Directory.Exists(dir)) return null;

            string today = DateTime.Now.ToString("yyyyMMdd");
            return Directory.GetFiles(dir, "better-genshin-impact*.log")
                .Where(f => Path.GetFileName(f).Contains(today))
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// 扫一个日志文件，统计每条规则命中几次、给出前 <paramref name="sampleCount"/> 条原文。
    /// 文件不存在返回空表；读不动也只记一行日志，不抛。
    /// </summary>
    public static List<ScanHit> Scan(string? filePath, IEnumerable<LogRule> rules, int sampleCount = 3, int maxBytes = 8 * 1024 * 1024)
    {
        var result = new List<ScanHit>();
        var list = (rules ?? Enumerable.Empty<LogRule>()).Where(r => r != null && LogRule.Normalize(r.Keyword).Length > 0).ToList();
        foreach (var r in list) result.Add(new ScanHit(LogRule.Normalize(r.Keyword), 0, new List<string>()));

        if (string.IsNullOrEmpty(filePath)) return result;

        try
        {
            using var fs = new FileStream(filePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // 只读文件尾：日志可能几十 MB，而最近一段才是要验证的内容
            long start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    string k = LogRule.Normalize(list[i].Keyword);
                    if (!line.Contains(k, StringComparison.OrdinalIgnoreCase)) continue;
                    var hits = result[i];
                    if (hits.Count + 0 == int.MaxValue) continue;
                    result[i] = hits with
                    {
                        Count = hits.Count + 1,
                        Samples = hits.Samples.Count < sampleCount
                            ? hits.Samples.Append(RunEvidence.Compact(line)).ToList()
                            : hits.Samples,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            try { LogService.LogEvent("日志规则：扫描失败（" + ex.Message + "）"); } catch { }
        }
        return result;
    }

    /// <summary>扫描结果拼成给人看的一段话（窗口与探针共用）。</summary>
    public static string Describe(IReadOnlyList<ScanHit> hits, string? filePath)
    {
        if (hits == null || hits.Count == 0) return "还没有写规则。";
        if (string.IsNullOrEmpty(filePath)) return "没找到今天的 BGI 日志（先在工具卡里填好 BetterGI 路径）。";

        var sb = new System.Text.StringBuilder();
        sb.Append("扫了 ").Append(Path.GetFileName(filePath)).Append("：");
        int total = hits.Sum(h => h.Count);
        if (total == 0) sb.AppendLine("所有关键词一次都没出现。").Append("换个更短的词试试，日志原文通常比想象中短。");
        else sb.AppendLine($"共命中 {total} 次。");

        foreach (var h in hits)
        {
            sb.Append("· 「").Append(h.Keyword).Append("」");
            sb.Append(h.Count == 0 ? " 没出现" : $" {h.Count} 次");
            sb.AppendLine();
            foreach (var s in h.Samples) sb.Append("    ").AppendLine(s);
        }
        return sb.ToString().TrimEnd();
    }
}
