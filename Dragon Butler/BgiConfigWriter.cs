using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DragonButler
{
    /// <summary>
    /// v1.15 / M1：写 BetterGI 的一条龙配置——只做「不许写坏」这一半。
    ///
    /// 这一层全部是纯函数，不碰界面、不弹窗、不自己决定要不要写。
    /// 理由：这是整个软件里唯一一处要改别人文件的代码，出错代价是
    /// 「用户今晚跑不成，而且不知道为什么」。所以先把判定抽出来，
    /// 让探针能在假目录上把每一条拒绝理由都试一遍，再接界面。
    ///
    /// 四条硬约束（来自 docs/plans/v1.15-方案.md 的 M1）：
    /// ① 只碰 {BGI}\User\OneDragon\ 下面的 .json，别处的路径一律拒；
    /// ② BGI 正在跑就不写，只回一句「先关掉再改」；
    /// ③ 写之前先备份到 config\bgi-backup\{stamp}\，备份失败就当写失败；
    /// ④ 不用 revision，用文件 mtime 兜底：读的时候记下来，写的时候对不上就拒。
    /// </summary>
    public static class BgiConfigWriter
    {
        /// <summary>一条龙配置放在 BGI 目录的这个子路径下。</summary>
        public const string SubDir = @"User\OneDragon";

        /// <summary>备份放在程序自己目录下的这个子路径（不写进 BGI 目录）。</summary>
        public const string BackupRoot = @"config\bgi-backup";

        /// <summary>配置名允许的最长字符数。</summary>
        public const int MaxNameChars = 64;

        /// <summary>
        /// 写回 JSON 时的序列化设置。
        /// 必须用 UnsafeRelaxedJsonEscaping：默认编码器会把中文转成 \u65E5\u5E38，
        /// BGI 读得懂，但人打开文件看到一片乱码，会以为我们把配置写坏了。
        /// </summary>
        private static readonly JsonSerializerOptions WriteOpt = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// 配置名是否安全。挡的是路径穿越（`..\..\x`）、绝对路径、保留字符与空名。
        /// 只允许写「用户自己看得懂的名字」，不允许借名字把写入引到别处。
        /// </summary>
        public static bool IsSafeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = name.Trim();
            if (n.Length > MaxNameChars) return false;
            if (n == "." || n == "..") return false;
            if (n != name) return false;                     // 首尾有空白 = 名字里藏了东西
            if (n.EndsWith(".", StringComparison.Ordinal)) return false;

            foreach (char c in n)
            {
                if (c < 0x20) return false;
                if ("\\/:*?\"<>|".IndexOf(c) >= 0) return false;
            }
            return true;
        }

        /// <summary>一条龙配置的完整路径；根目录或名字不合法时返回 ""。</summary>
        public static string ConfigPath(string? bgiRoot, string? name)
        {
            if (string.IsNullOrWhiteSpace(bgiRoot)) return "";
            if (!IsSafeName(name)) return "";
            try { return Path.Combine(bgiRoot!.Trim(), SubDir, name!.Trim() + ".json"); }
            catch { return ""; }
        }

        /// <summary>
        /// 目标路径是不是真的落在 {root}\User\OneDragon\ 里。
        /// 这是第二道闸：就算名字和拼接都没问题，也要按绝对路径再核一次，
        /// 防止符号链接之类的东西把写入引出去。
        /// </summary>
        public static bool IsUnderOneDragon(string? path, string? bgiRoot)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(bgiRoot)) return false;
            try
            {
                string want = Path.GetFullPath(Path.Combine(bgiRoot!.Trim(), SubDir));
                string got = Path.GetFullPath(path!);
                string dir = Path.GetDirectoryName(got) ?? "";
                if (!string.Equals(dir.TrimEnd('\\', '/'), want.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    return false;
                return string.Equals(Path.GetExtension(got), ".json", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 现在能不能写。返回 null = 可以写；返回字符串 = 拒绝的原因（直接给用户看的那句话）。
        /// 判定顺序就是优先级：先看是不是我们认识的路径，再看人、再看文件、最后看时间戳。
        /// </summary>
        public static string? RefuseReason(string? path, string? bgiRoot, bool bgiRunning,
            bool fileExists, DateTime? expectedMtime, DateTime? currentMtime)
        {
            if (string.IsNullOrWhiteSpace(bgiRoot)) return "还没填 BetterGI 的路径，先在上面选一下。";
            if (string.IsNullOrWhiteSpace(path)) return "这个配置名不能用，换一个（不能带 \\ / : * ? \" < > |）。";
            if (!IsUnderOneDragon(path, bgiRoot)) return "这个位置不是一个龙配置目录，没敢写。";
            if (bgiRunning) return "BetterGI 正在运行，先关掉再改——它退出时会把内存里的配置写回去，覆盖掉这次的改动。";
            if (!fileExists) return "这个配置文件已经不在了，可能被删掉或改名了。刷新一下再试。";

            if (expectedMtime.HasValue && currentMtime.HasValue)
            {
                // 给 2 秒宽容：FAT 与网络盘的时间精度能到 2 秒，卡太死会误伤。
                var drift = currentMtime.Value - expectedMtime.Value;
                if (drift > TimeSpan.FromSeconds(2) || drift < TimeSpan.FromSeconds(-2))
                    return "这个文件在读完之后被别的程序改过，为了不覆盖你的改动，这次没写。重新打开再试。";
            }
            return null;
        }

        /// <summary>备份目录：程序目录下 config\bgi-backup\yyyyMMdd-HHmmss\。</summary>
        public static string BackupDir(string baseDir, DateTime when)
        {
            return Path.Combine(baseDir, BackupRoot, when.ToString("yyyyMMdd-HHmmss"));
        }

        /// <summary>备份文件名：原名 + 时间戳后缀，同一次操作里多次备份不会互相盖掉。</summary>
        public static string BackupFile(string backupDir, string? name, DateTime when)
        {
            string safe = IsSafeName(name) ? name!.Trim() : "config";
            return Path.Combine(backupDir, safe + "." + when.ToString("HHmmssfff") + ".json");
        }

        /// <summary>
        /// 只改我们认识的那几个布尔开关，其它字段原样保留。
        /// 这是刻意的最小面：不认识的字段一个都不碰，BGI 加了新字段我们也不会写丢。
        /// 返回 null 表示这段 JSON 不是我们敢碰的东西（不是对象、或路径中间不是对象）。
        /// </summary>
        public static string? SetBool(string? json, string keyPath, bool value)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(keyPath)) return null;
            try
            {
                var node = JsonNode.Parse(json);
                if (node is not JsonObject root) return null;

                string[] parts = keyPath.Split('.');
                JsonObject cur = root;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (cur[parts[i]] is JsonObject next) cur = next;
                    else return null;   // 中间缺一层就不猜，交给用户手动改
                }

                string leaf = parts[parts.Length - 1];
                cur[leaf] = JsonValue.Create(value);
                return root.ToJsonString(WriteOpt);
            }
            catch (JsonException) { return null; }
            catch (ArgumentException) { return null; }
        }

        /// <summary>读一个布尔开关；读不到（没这个键、不是布尔、JSON 坏了）就返回 null。</summary>
        public static bool? GetBool(string? json, string keyPath)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(keyPath)) return null;
            try
            {
                var node = JsonNode.Parse(json);
                if (node is not JsonObject cur) return null;

                foreach (string part in keyPath.Split('.'))
                {
                    if (cur[part] is JsonObject next) { cur = next; continue; }
                    if (cur[part] is JsonValue v && v.TryGetValue(out bool b)) return b;
                    return null;
                }
                return null;
            }
            catch (JsonException) { return null; }
            catch (ArgumentException) { return null; }
        }

        /// <summary>
        /// 一条龙跑完后的动作。BGI 自己就是这个键（字符串），合法取值由 BGI 决定，
        /// 我们不硬编码候选值——只知道它是字符串，所以只提供 Read/Write，
        /// 界面这版不暴露改动，等真机确认取值集合再接。
        /// </summary>
        public const string KeyCompletionAction = "settings.completionAction";

        /// <summary>读一个字符串字段；读不到返回 null。</summary>
        public static string? GetString(string? json, string keyPath)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(keyPath)) return null;
            try
            {
                JsonNode? cur = JsonNode.Parse(json);
                foreach (string part in keyPath.Split('.'))
                {
                    if (cur is not JsonObject o) return null;
                    cur = o[part];
                }
                if (cur is JsonValue v && v.TryGetValue(out string? s)) return s;
                return null;
            }
            catch (JsonException) { return null; }
            catch (ArgumentException) { return null; }
        }

        /// <summary>只改一个字符串字段，其它字段原样保留；结构不认识就返回 null。</summary>
        public static string? SetString(string? json, string keyPath, string? value)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(keyPath)) return null;
            try
            {
                var node = JsonNode.Parse(json);
                if (node is not JsonObject root) return null;

                string[] parts = keyPath.Split('.');
                JsonObject cur = root;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (cur[parts[i]] is JsonObject next) cur = next;
                    else return null;
                }
                cur[parts[parts.Length - 1]] = value == null ? null : JsonValue.Create(value);
                return root.ToJsonString(WriteOpt);
            }
            catch (JsonException) { return null; }
            catch (ArgumentException) { return null; }
        }

        /// <summary>
        /// 真正的写入：先备份、再原子替换。返回 (是否成功, 给用户看的一句话)。
        ///
        /// 两个刻意的选择：
        /// ① 备份失败就整个放弃——宁可这次没改成，也不能在没备份的情况下覆盖别人的文件；
        /// ② 用临时文件 + File.Replace 而不是直接覆盖——写到一半断电时，原文件还是完整的。
        /// </summary>
        public static (bool Ok, string Message) Save(string baseDir, string path, string content, DateTime now)
        {
            string tmp = "";
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return (false, "配置文件不在，没写。");

                string dir = BackupDir(baseDir, now);
                Directory.CreateDirectory(dir);
                string bak = BackupFile(dir, Path.GetFileNameWithoutExtension(path), now);
                File.Copy(path, bak, overwrite: true);
                if (!File.Exists(bak))
                    return (false, "备份没成功，为了不丢东西这次没写。");

                tmp = path + ".dbtmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                File.Replace(tmp, path, null);
                return (true, "已保存，改动前的版本在 " + bak);
            }
            catch (Exception ex)
            {
                LogService.LogEvent("写一条龙配置失败：" + ex.Message);
                return (false, "没写成：" + ex.Message);
            }
            finally
            {
                try { if (tmp.Length > 0 && File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
