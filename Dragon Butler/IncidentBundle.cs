using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
/// 2. 绝不泄露凭据——token / 密码 / 推送地址 / 账号 / 本地路径一律替换，
///    靠键名与取值形态双重判定，不指望人记得回来加词。
/// 3. 绝不无限增长——只留最近 N 份，超了就删最旧的。
///
/// v1.43 / 任务六把「脱敏」和「完整性」两件事都做实了：
/// 以前所有嵌套对象（包括每个工具配置）都退化成类型名 "DragonButler.ToolProfile"，
/// 而且三次写入都返回 void——缺日志、缺截图也照报「已保存」，
/// 看现场的人分不清「没发生」和「没采到」。
/// </summary>
public static class IncidentBundle
{
    /// <summary>日志尾巴带多少行。够看清上下文，又不至于把文件复制一遍。</summary>
    public const int LogTailLines = 200;

    /// <summary>默认保留份数。</summary>
    public const int DefaultKeep = 10;

    /// <summary>进程列表最多列多少个（按工作集倒序）。</summary>
    private const int MaxProcesses = 30;

    /// <summary>结构化递归的最大层数。配置里不可能真有这么深，超了就是成环了。</summary>
    private const int MaxDepth = 6;

    public static string Root =>
        Paths.IncidentsDir;

    /// <summary>
    /// 现场包里的一块（v1.43 / 任务六）。
    /// <see cref="Detail"/> 是给人看的一句话：成功时说采到了什么，失败时说为什么没采到。
    /// </summary>
    public sealed record Piece(string File, bool Ok, string Detail)
    {
        public static Piece Good(string file, string detail = "") => new(file, true, detail);
        public static Piece Bad(string file, string detail) => new(file, false, detail);
        public string Describe() => Detail.Length == 0 ? File : $"{File}（{Detail}）";
    }

    /// <summary>
    /// 抓一份现场。返回目录路径；被关闭、或写盘失败时返回 null。
    /// 调用方拿到的路径可以直接写进通知正文。
    ///
    /// <para>
    /// v1.43 / 任务六：目录建成功不等于现场完整。这里按块收集结果，
    /// 最终日志把「采到什么」和「缺什么」分开写——缺日志的现场包，
    /// 最需要被标注的就是这件事本身。
    /// </para>
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

            // 先落一行日志再抄日志尾巴。
            // 否则今天第一次失败时，日志文件可能还没被创建过——WriteLogTail 只能空手而归，
            // 现场包里少一份日志，而这恰恰是最该有的那一份。
            try { LogService.LogEvent($"现场包：开始采集（{reason}）"); } catch { }

            var pieces = new List<Piece>();
            pieces.Add(WriteLogTail(dir));
            if (includeScreenshot) pieces.Add(WriteScreenshot(dir));
            // env.json 最后写：它要把前面几块的结果写进包里，
            // 这样拿着文件夹的人不用去翻程序日志就知道少了什么。
            pieces.Add(WriteEnvironment(dir, reason, profile, pieces));

            string okList = string.Join("、", pieces.Where(p => p.Ok).Select(p => p.Describe()));
            string missList = string.Join("、", pieces.Where(p => !p.Ok).Select(p => p.Describe()));
            if (missList.Length == 0)
            {
                LogService.LogEvent($"现场包：已保存 {reason} 的现场到 {dir}（{okList}）");
            }
            else
            {
                LogService.LogEvent(
                    $"现场包：部分保存 {reason} 的现场到 {dir}；" +
                    $"采到：{(okList.Length > 0 ? okList : "无")}；缺：{missList}");
            }

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
                // v1.45 / M12-4（D27）：改成丢进回收站，不再当场抹掉。
                //
                // 原来这里是 Directory.Delete(d, true)。现场包是给人排查用的，
                // 而排查往往发生在「发现问题之后」——那时候第 11 份以前的现场
                // 恰恰可能是关键证据，删了就真没了。进回收站的成本几乎为零，
                // 用户想找还能找回来。
                bool recycled = TryRecycle(d);
                if (!recycled)
                {
                    // 回收站不可用（网络盘、被策略关掉、目录被占用）时退回直接删，
                    // 但必须留痕：静默失败会让「保留 N 份」这条规则悄悄失效。
                    try { Directory.Delete(d, true); } catch { }
                    try
                    {
                        LogService.LogEvent(
                            $"现场包：{Path.GetFileName(d)} 没能放进回收站，已直接删除（保留 {keep} 份的规则照旧）");
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>v1.45 / M12-4：把一份旧现场包丢进回收站；成功返回 true。</summary>
    private static bool TryRecycle(string dir)
    {
        try
        {
            // net8.0-windows 自带 Microsoft.VisualBasic，不需要额外加包。
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                dir,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
            return true;
        }
        catch { return false; }
    }

    /// <summary>默认值会对中文做 \uXXXX 转义，env.json 会变成天书——这里放开非 ASCII。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 把今天日志的最后 N 行抄进 log.txt。
    ///
    /// <para>
    /// v1.43 / 任务六：返回结果。原来日志文件还没建起来（新装的机器、刚过零点）
    /// 就直接 return，调用方无从知道——现场包里少一份日志，看着跟「一切正常」一样。
    /// </para>
    /// </summary>
    private static Piece WriteLogTail(string dir)
    {
        const string name = "log.txt";
        try
        {
            string logPath = Path.Combine(LogService.Dir,
                DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_运行.log");
            if (!File.Exists(logPath))
                return Piece.Bad(name, $"今天的日志还没建起来（{Path.GetFileName(logPath)} 不存在）");

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
            if (lines.Count == 0)
                return Piece.Bad(name, "日志文件是空的");

            File.WriteAllText(Path.Combine(dir, name),
                string.Join(Environment.NewLine, lines) + Environment.NewLine,
                new UTF8Encoding(false));
            return Piece.Good(name, $"最后 {lines.Count} 行");
        }
        catch (Exception ex)
        {
            return Piece.Bad(name, "读日志失败：" + ex.Message);
        }
    }

    /// <summary>环境快照：版本、系统、失败原因、档案、进程列表、脱敏配置、采集结果。</summary>
    private static Piece WriteEnvironment(string dir, string reason, ToolProfile? profile, IReadOnlyList<Piece> pieces)
    {
        const string name = "env.json";
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
                // 工作目录里带用户名，和本地路径一个待遇
                ["工作目录"] = RedactPath(Safe(() => AppContext.BaseDirectory, "")),
            };

            // v1.43 / 任务六：走结构化描述，不再手写一张「假装很全」的字段表。
            // 以前漏掉的 GameProcessNames / SecondarySignals / CustomData、以及明文路径与启动参数，
            // 恰恰是排查依赖、信号、自定义参数时最该看的东西。
            if (profile != null) root["失败工具"] = DescribeObject(profile, 0);

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

            // 配置整体抄一份，但每一层、每个值都过一遍脱敏
            root["配置"] = SettingsSnapshot();

            // 采集结果写进包本身：缺哪一块，包里自己写着
            var collected = new JsonArray();
            foreach (var p in pieces)
            {
                collected.Add(new JsonObject
                {
                    ["文件"] = p.File,
                    ["采到"] = p.Ok,
                    ["说明"] = p.Detail,
                });
            }
            root["采集结果"] = collected;

            File.WriteAllText(Path.Combine(dir, name),
                root.ToJsonString(JsonOptions),
                new UTF8Encoding(false));
            return Piece.Good(name, "版本、系统、进程、脱敏配置");
        }
        catch (Exception ex)
        {
            return Piece.Bad(name, "写不出来：" + ex.Message);
        }
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
                if (prop.GetIndexParameters().Length > 0) continue;
                object? value;
                try { value = prop.GetValue(AppState.Settings); }
                catch { continue; }

                if (IsSensitiveKey(prop.Name))
                {
                    obj[prop.Name] = RedactValue(prop.Name, value);
                    continue;
                }
                obj[prop.Name] = ToNode(value, 0);
            }
        }
        catch { }
        return obj;
    }

    /// <summary>
    /// 任意值摊成 JSON（v1.43 / 任务六 A-2）。
    ///
    /// <para>
    /// 原来这里 <c>default: return value.ToString();</c>，于是 <c>Tools</c> 那份配置里
    /// 每个工具都变成字符串 "DragonButler.ToolProfile"——现场包看着有内容，其实一个字都没有。
    /// 排查「这个工具的启动参数是不是配错了」时，那一格完全没用。
    /// 现在默认走反射摊开，并且每个字段都过脱敏。
    /// </para>
    /// </summary>
    private static JsonNode? ToNode(object? value, int depth)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s;
            case bool b: return b;
            case int i: return i;
            case long l: return l;
            case double d: return d;
            case decimal m: return (double)m;
            case DateTime dt: return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case Enum e: return e.ToString();
            case JsonNode jn: return jn.DeepClone();
            case Array arr:
                {
                    var a = new JsonArray();
                    foreach (var item in arr) a.Add(ToNode(item, depth + 1));
                    return a;
                }
            case System.Collections.IDictionary dict:
                {
                    var o = new JsonObject();
                    foreach (System.Collections.DictionaryEntry e in dict)
                    {
                        string key = e.Key?.ToString() ?? "";
                        // 字典的键也要过一遍：CustomData 里就有 customFilePath / apiToken 这种
                        o[key] = IsSensitiveKey(key) ? RedactValue(key, e.Value) : ToNode(e.Value, depth + 1);
                    }
                    return o;
                }
            case System.Collections.IEnumerable en:
                {
                    var a = new JsonArray();
                    foreach (var item in en) a.Add(ToNode(item, depth + 1));
                    return a;
                }
            default:
                return DescribeObject(value, depth + 1);
        }
    }

    /// <summary>
    /// v1.43 / 任务六：把任意 POCO 摊成结构化 JSON，逐字段脱敏。
    /// 递归有层数上限——配置对象之间理论上不该成环，但真成了环，现场包不能把程序拖死。
    /// </summary>
    private static JsonNode? DescribeObject(object value, int depth)
    {
        if (depth > MaxDepth) return Redacted;
        var o = new JsonObject();
        try
        {
            foreach (var prop in value.GetType().GetProperties())
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                object? v;
                try { v = prop.GetValue(value); }
                catch { continue; }

                o[prop.Name] = IsSensitiveKey(prop.Name) ? RedactValue(prop.Name, v) : ToNode(v, depth + 1);
            }
        }
        catch (Exception ex)
        {
            o["（展开失败）"] = ex.Message;
        }
        return o;
    }

    /// <summary>
    /// 敏感字段的值怎么替换（v1.43 / 任务六）。
    ///
    /// <para>
    /// 空值就留空——标成 *** 反而看不出「到底填没填」，而这一条对排障很关键。
    /// 路径类只留文件名：知道是哪个 exe 有用，知道它在谁的用户目录下没有用。
    /// </para>
    /// </summary>
    private static JsonNode? RedactValue(string name, object? value)
    {
        if (value == null) return null;
        string text = value switch
        {
            string s => s,
            string[] arr => string.Join(";", arr),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
        if (text.Length == 0) return "";

        // 路径只留文件名：知道是哪个 exe 有用，知道它在谁的用户目录下没有用
        if (LooksLikePath(text)) return RedactPath(text);

        // 自由文本（启动参数这类）：结构本身是排障要看的，只盖里面的凭据。
        // 注意这里不能直接把整行替换成 ***，否则「参数是不是配错了」就无从判断。
        if (IsFreeTextKey(name)) return RedactText(text);

        // 其余敏感键的值本身就是一个凭据（token / 账号 / chat id），整体替换。
        // 不能只跑 RedactText：一个裸 token 里没有 "token=" 这种形态，跑完等于没盖。
        return Redacted;
    }

    /// <summary>
    /// 这些键的值是「一整行配置」而不是单个凭据，
    /// 脱敏时保留结构、只盖掉嵌在里面的秘密（v1.43 / 任务六 A-4）。
    /// </summary>
    public static bool IsFreeTextKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var key in FreeTextKeys)
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] FreeTextKeys =
    {
        "argument", "args", "customLogTexts", "pattern",
    };

    /// <summary>脱敏后留下的占位符。</summary>
    public const string Redacted = "***";

    /// <summary>是不是一个本地路径（含盘符、UNC、或反斜杠分隔）。</summary>
    public static bool LooksLikePath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (Regex.IsMatch(s, @"^[A-Za-z]:[\\/]")) return true;
        if (s.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (s.StartsWith("/", StringComparison.Ordinal)) return true;
        return s.Contains('\\') && !s.Contains(' ');
    }

    /// <summary>
    /// 路径脱敏：只留最后一段的文件名（或文件夹名）。
    /// 例如 <c>C:\Users\someone\Games\BetterGI\BetterGI.exe</c> → <c>…\BetterGI.exe</c>。
    /// 留名字是有意的：现场包里「哪个工具出的问题」必须还能看懂。
    /// </summary>
    public static string RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            string trimmed = path.TrimEnd('\\', '/');
            string name = Path.GetFileName(trimmed);
            if (name.Length == 0) return Redacted;
            if (name.Length <= 80 && !name.Contains(' ')) return "…" + Path.DirectorySeparatorChar + name;
            return Redacted;
        }
        catch { return Redacted; }
    }

    /// <summary>
    /// 自由文本里的凭据（v1.43 / 任务六 A-4）。
    /// 启动参数是一整行字符串，光靠属性名判断不了，得看形态：
    /// <c>--token=abc</c> / <c>-apikey XYZ</c> / <c>password: hunter2</c> 都要盖住。
    /// </summary>
    private static readonly Regex SecretInText = new(
        @"(?i)\b(token|secret|password|passwd|pwd|apikey|api[_-]?key|auth|authorization|session|signature|sig)\b(\s*[=:]\s*|\s+)([^\s&""';]+)",
        RegexOptions.Compiled);

    /// <summary>路径形态的片段（启动参数里可能有）。</summary>
    private static readonly Regex PathInText = new(
        @"[A-Za-z]:\\[^\s""';]+",
        RegexOptions.Compiled);

    /// <summary>把一段自由文本里的凭据与本地路径都盖掉。</summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string s = SecretInText.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
        s = PathInText.Replace(s, m => RedactPath(m.Value));
        return s;
    }

    /// <summary>
    /// 哪些字段名算敏感。按「包含」判定而不是精确匹配——
    /// 以后新增 NotifyXxxToken 这类字段会自动被盖住，不需要回来改这张表。
    ///
    /// <para>
    /// v1.43 / 任务六修了两个方向的错：
    /// </para>
    /// <para>
    /// ① 误伤：<c>BgiWebhookPort</c> / <c>NotifyEmailPort</c> / <c>Hotkey*</c> 只因为名字里
    /// 带了 webhook / email / key 就被盖成 ***，可端口号和热键正是排障要看的非敏感值。
    /// 走白名单先放行。
    /// </para>
    /// <para>
    /// ② 漏判：<c>NotifyEmailUser</c> / <c>NotifyTelegramChat</c> / <c>BgiPath</c> /
    /// <c>CustomBgImage</c> / <c>AfterRunAppPath</c> / 各工具的路径与启动参数，
    /// 名字里一个敏感词都没有，以前是原样写进包里的。补上这些形态。
    /// </para>
    /// </summary>
    public static bool IsSensitiveKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // 先放行：这些名字命中敏感词，但值本身不敏感，而且是排障必需的
        foreach (var okKey in NonSensitiveKeys)
            if (string.Equals(name, okKey, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var key in SensitiveFragments)
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>名字命中敏感词、但值不敏感（端口号、热键、开关）。</summary>
    private static readonly string[] NonSensitiveKeys =
    {
        "BgiWebhookPort", "NotifyEmailPort",
        "HotkeyEnabled", "HotkeyStart", "HotkeyStop",
    };

    private static readonly string[] SensitiveFragments =
    {
        // 凭据
        "token", "secret", "password", "pass", "key", "auth",
        // 地址与账号
        "url", "webhook", "email", "chat", "user", "host",
        // 本地路径与启动参数
        "path", "dir", "folder", "argument", "args", "image",
    };

    /// <summary>截图并如实记录（v1.43 / 任务六 B-3、B-4、B-5）。</summary>
    private static Piece WriteScreenshot(string dir)
    {
        const string name = "screen.png";
        try
        {
            // 抓屏会走到窗口句柄与 GDI。失败不是「就算了」，是要在包里写明为什么没有。
            var cap = ScreenshotMonitor.CaptureEx();
            if (cap.Buf == null || cap.W <= 0 || cap.H <= 0)
                return Piece.Bad(name, cap.Error ?? "没抓到");

            var src = System.Windows.Media.Imaging.BitmapSource.Create(
                cap.W, cap.H, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, cap.Buf, cap.W * 4);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            string target = Path.Combine(dir, name);
            using (var fs = new FileStream(target, FileMode.Create)) enc.Save(fs);

            // 「文件建出来了」不等于「图是有效的」：0 字节或写失败都算没截到
            var info = new FileInfo(target);
            if (!info.Exists || info.Length == 0)
                return Piece.Bad(name, "写出来的文件是空的");

            return Piece.Good(name, cap.Scope);
        }
        catch (Exception ex)
        {
            return Piece.Bad(name, "截图失败：" + ex.Message);
        }
    }

    private static T Safe<T>(Func<T> get, T fallback)
    {
        try { return get(); } catch { return fallback; }
    }
}
