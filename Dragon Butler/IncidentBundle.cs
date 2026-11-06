using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DragonButler;

/// <summary>
/// v1.6 / F3：失败现场包。
///
/// 起因：出事以后只有一句「详情见软件日志」，而日志在用户机器上、还得让人翻。
/// 结果就是每次排障都在问「当时什么情况」——又问不出东西，因为昨天的情况已经没了。
///
/// 这里做的事很朴素：失败 / 悬置 / 挂起的那一刻，把「当时什么情况」原样抄一份到磁盘
/// （日志尾巴、脱敏后的配置、进程列表、屏幕快照），排障时打开文件夹就是现场。
///
/// 三条硬约束：
/// 1. 绝不阻断主流程——任何异常都吞掉，只记一行日志，队列该继续继续。
/// 2. 绝不泄露凭据——token / 密码 / 推送地址一律替换成 ***，靠键名判定，不靠人记得改。
/// 3. 绝不无限增长——只留最近 N 份，超了就删最旧的。
/// </summary>
public static class IncidentBundle
{
    /// <summary>日志尾巴带多少行。够看清上下文，又不至于把文件复制一遍。</summary>
    public const int LogTailLines = 200;

    /// <summary>默认保留份数。</summary>
    public const int DefaultKeep = 10;

    /// <summary>进程列表最多列多少个（按工作集倒序）。</summary>
    private const int MaxProcesses = 30;

    public static string Root =>
        Path.Combine(LogService.Dir, "incidents");

    /// <summary>
    /// 抓一份现场。返回目录路径；被关闭、或写盘失败时返回 null。
    /// 调用方拿到的路径可以直接写进通知正文。
    /// </summary>
    public static string? Capture(string reason, ToolProfile? profile = null, bool includeScreenshot = true)
    {
        try
        {
            if (!AppState.Settings.IncidentBundleEnabled) return null;

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(Root, stamp);
            // 同一秒内两次失败：加后缀，不覆盖上一份
            int n = 1;
            while (Directory.Exists(dir)) dir = Path.Combine(Root, stamp + "-" + n++);
            Directory.CreateDirectory(dir);

            WriteLogTail(dir);
            WriteEnvironment(dir, reason, profile);
            if (includeScreenshot) WriteScreenshot(dir);

            LogService.LogEvent($"现场包：已保存 {reason} 的现场到 {dir}");
            Trim(0);
            return dir;
        }
        catch (Exception ex)
        {
            // 现场包是辅助手段，它自己出问题绝不能影响主流程
            try { LogService.LogEvent("现场包：生成失败（" + ex.Message + "）"); } catch { }
            return null;
        }
    }

    /// <summary>只保留最近 keep 份（keep &lt;= 0 时用配置值）。</summary>
    public static void Trim(int keep)
    {
        try
        {
            if (keep <= 0) keep = Math.Max(1, AppState.Settings.IncidentBundleKeep);
            if (!Directory.Exists(Root)) return;
            var dirs = Directory.GetDirectories(Root)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Skip(keep)
                .ToList();
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, true); } catch { }
            }
        }
        catch { }
    }

    /// <summary>默认值会对中文做 \uXXXX 转义，env.json 会变成天书——这里放开非 ASCII。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>把今天日志的最后 N 行抄进 log.txt。</summary>
    private static void WriteLogTail(string dir)
    {
        try
        {
            string logPath = Path.Combine(LogService.Dir,
                DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_运行.log");
            if (!File.Exists(logPath)) return;
            // 共享读：日志正在被写，不能锁死它
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
                if (lines.Count > LogTailLines) lines.RemoveAt(0);
            }
            File.WriteAllText(Path.Combine(dir, "log.txt"),
                string.Join(Environment.NewLine, lines) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>环境快照：版本、系统、失败原因、档案、进程列表、脱敏配置。</summary>
    private static void WriteEnvironment(string dir, string reason, ToolProfile? profile)
    {
        try
        {
            var root = new JsonObject
            {
                ["生成时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["失败原因"] = reason ?? "",
                ["程序版本"] = Safe(() => App.ProductVersion, "unknown"),
                ["系统"] = Safe(() => Environment.OSVersion.VersionString, "unknown"),
                ["运行时"] = Safe(() => Environment.Version.ToString(), "unknown"),
                ["64位进程"] = Safe(() => Environment.Is64BitProcess.ToString(), "unknown"),
                ["工作目录"] = Safe(() => AppContext.BaseDirectory, ""),
            };

            if (profile != null)
            {
                root["失败工具"] = new JsonObject
                {
                    ["ToolId"] = profile.ToolId,
                    ["显示名"] = profile.DisplayName,
                    ["可执行文件"] = profile.ExecutablePath,
                    ["启动参数"] = profile.LaunchArguments,
                    ["进程名"] = profile.ManagedProcessName,
                    ["主信号"] = profile.PrimarySignal.ToString(),
                    ["最大时长分钟"] = profile.MaxDurationMinutes,
                    ["重试次数"] = profile.RetryCount,
                    ["重试等待秒"] = profile.RetryDelaySeconds,
                    ["重试退避"] = profile.RetryBackoff,
                };
            }

            var procs = new JsonArray();
            try
            {
                foreach (var p in Process.GetProcesses()
                             .OrderByDescending(p => Safe(() => p.WorkingSet64, 0L))
                             .Take(MaxProcesses))
                {
                    try
                    {
                        procs.Add(new JsonObject
                        {
                            ["名称"] = p.ProcessName,
                            ["PID"] = p.Id,
                            ["内存MB"] = Safe(() => p.WorkingSet64 / 1024 / 1024, 0L),
                            ["启动时间"] = Safe(() => p.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ""),
                        });
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            root["进程"] = procs;

            // 配置整体抄一份，但每个值都过一遍脱敏
            root["配置"] = SettingsSnapshot();

            File.WriteAllText(Path.Combine(dir, "env.json"),
                root.ToJsonString(JsonOptions),
                new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>把 AppSettings 摊成 JSON，敏感字段值一律替换。</summary>
    private static JsonObject SettingsSnapshot()
    {
        var obj = new JsonObject();
        try
        {
            foreach (var prop in typeof(AppSettings).GetProperties())
            {
                if (!prop.CanRead) continue;
                object? value;
                try { value = prop.GetValue(AppState.Settings); }
                catch { continue; }

                if (IsSensitiveKey(prop.Name))
                {
                    string text = value?.ToString() ?? "";
                    // 空值就说空，不留 ***，否则看不出到底填没填
                    obj[prop.Name] = text.Length == 0 ? "" : Redacted;
                    continue;
                }
                obj[prop.Name] = ToNode(value);
            }
        }
        catch { }
        return obj;
    }

    private static JsonNode? ToNode(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s;
            case bool b: return b;
            case int i: return i;
            case long l: return l;
            case double d: return d;
            case DateTime dt: return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case Array arr:
                {
                    var a = new JsonArray();
                    foreach (var item in arr) a.Add(ToNode(item));
                    return a;
                }
            case System.Collections.IDictionary dict:
                {
                    var o = new JsonObject();
                    foreach (System.Collections.DictionaryEntry e in dict)
                        o[e.Key?.ToString() ?? ""] = ToNode(e.Value);
                    return o;
                }
            case System.Collections.IEnumerable en:
                {
                    var a = new JsonArray();
                    foreach (var item in en) a.Add(ToNode(item));
                    return a;
                }
            default:
                return value.ToString();
        }
    }

    /// <summary>脱敏后留下的占位符。</summary>
    public const string Redacted = "***";

    /// <summary>
    /// 哪些字段名算敏感。按「包含」判定而不是精确匹配——
    /// 以后新增 NotifyXxxToken 这类字段会自动被盖住，不需要回来改这张表。
    /// </summary>
    public static bool IsSensitiveKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var key in SensitiveFragments)
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] SensitiveFragments =
    {
        "token", "secret", "password", "pass", "key", "url", "webhook", "email",
    };

    private static void WriteScreenshot(string dir)
    {
        try
        {
            // 抓屏会走到窗口句柄与 GDI，失败就算了——现场包的主体是日志与配置
            var (buf, w, h) = ScreenshotMonitor.Capture();
            if (buf == null || w <= 0 || h <= 0) return;
            var src = System.Windows.Media.Imaging.BitmapSource.Create(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, buf, w * 4);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            using var fs = new FileStream(Path.Combine(dir, "screen.png"), FileMode.Create);
            enc.Save(fs);
        }
        catch { }
    }

    private static T Safe<T>(Func<T> get, T fallback)
    {
        try { return get(); } catch { return fallback; }
    }
}
