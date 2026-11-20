using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace DragonButler;

/// <summary>
/// v1.17：把「这台机器的设置」整包带走 / 整包搬回来。
///
/// 参考的是 AUTO-MAS 的 data_backup.py（它整包打 data/config/history，并用
/// sqlite 的 backup API 拿一致快照）与 ABGI 的 POST /api/backup（它一句话
/// 「备份 BetterGI User 等运行资料」）。我们没有数据库，要打的就是四个 json，
/// 所以整件事的重心不在打包，在**别把别人的东西打进去**和**别把包解到别的目录**。
///
/// 三条边界（都写成纯函数，可断言）：
/// 1. 只收白名单文件：settings / group_archive / run_journal / harvest。日志、
///    现场包、缩略图缓存都不进包——包小到可以发给自己，才有人真的会备份。
/// 2. 一条龙配置是可选附件，且只收 User\OneDragon 目录下我们自己校验过的名。
/// 3. 解包时每一条都要再核一次相对路径：zip 里可以写 "../../Windows"，这不是
///    假想的攻击——任何用户都可能拿到别人给的包。
/// </summary>
public static class SettingsBackup
{
    /// <summary>包内放配置的目录名</summary>
    public const string RootEntry = "dragon-butler/";

    /// <summary>包内一条龙配置的目录名</summary>
    public const string BgiEntry = "bgi-onedragon/";

    /// <summary>包格式标记，用来在解包前认出来「这是我们的包」</summary>
    public const string MarkerName = "backup.json";

    /// <summary>认得的版本；将来改结构时靠它给出可读的拒绝理由</summary>
    public const int FormatVersion = 1;

    /// <summary>白名单：只备份这四个文件（顺序即包内顺序，assert 也依赖它稳定）</summary>
    public static readonly string[] Whitelist = { "settings.json", "group_archive.json", "run_journal.json", "harvest.json" };

    /// <summary>
    /// 备份根目录：exe 旁的「备份」文件夹。刻意与「全部日志」分开——
    /// 日志是排错用的、会被 Trim 清掉；备份是用户资产，不该被同一套清理逻辑顺手删掉。
    /// </summary>
    public static string Dir => Paths.BackupDir;

    /// <summary>默认包名：一条龙管家-20261116-0801.zip</summary>
    public static string FileName(DateTime now) => "一条龙管家-" + now.ToString("yyyyMMdd-HHmm") + ".zip";

    /// <summary>
    /// 只认白名单里的文件名，且大小写不敏感（Windows 上 settings.JSON 就是 settings.json）。
    /// 传入带目录的路径一律拒绝：我们只打包 config 下的平铺文件。
    /// </summary>
    public static bool IsWhitelisted(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0) return false;
        foreach (var w in Whitelist)
            if (string.Equals(w, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 解包前的路径校验。zip 里的 entry 名是外部数据，必须当成不可信输入：
    /// 绝对路径、盘符、`..`、空段全部拒绝；返回 nul 表示这条不能落盘。
    /// </summary>
    public static string? SafeEntryPath(string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;
        // 统一成 '/' 再判，反斜杠在 zip 里也常见
        string p = entryName.Replace('\\', '/').Trim();
        if (p.Length == 0) return null;
        if (p.StartsWith("/", StringComparison.Ordinal)) return null;
        if (p.Length >= 2 && p[1] == ':') return null;          // C:...
        if (p.Contains(":", StringComparison.Ordinal)) return null; // 任何盘符或 ADS
        var parts = p.Split('/');
        foreach (var seg in parts)
        {
            if (seg.Length == 0) return null;                    // // 或结尾 /
            if (seg == "." || seg == "..") return null;
        }
        return p;
    }

    /// <summary>
    /// 把目标路径钉死在 baseDir 里面（二次确认）。即使 SafeEntryPath 已挡过一层，
    /// 落盘前仍要按真实绝对路径再核一次——符号链接与 8.3 短名都可能绕开字符串判断。
    /// </summary>
    public static bool IsInside(string? baseDir, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(fullPath)) return false;
        try
        {
            string root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(fullPath);
            if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return true;
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>包内清单（写进 backup.json，解开时先读它）</summary>
    public sealed class Manifest
    {
        public int Version { get; set; } = FormatVersion;
        public string CreatedAt { get; set; } = "";
        public string Product { get; set; } = "Dragon Butler";
        public List<string> Files { get; set; } = new();
        public List<string> BgiFiles { get; set; } = new();
    }

    private static readonly JsonSerializerOptions ManifestOpt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 要打包哪些文件：白名单里**真实存在**的才收（新装机器上可能只有 settings.json）。
    /// 返回相对 config 的文件名，不含目录。
    /// </summary>
    public static List<string> CollectFiles(string? configDir, Func<string, bool> fileExists)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(configDir) || fileExists == null) return list;
        foreach (var name in Whitelist)
        {
            string path = Path.Combine(configDir, name);
            if (fileExists(path)) list.Add(name);
        }
        return list;
    }

    /// <summary>
    /// 一条龙配置也只在名字通过 BgiConfigWriter 校验时才收——包里的文件名
    /// 将来会被原样写回磁盘，收进一个非法名等于给自己埋一颗雷。
    /// </summary>
    public static List<string> CollectBgiFiles(string? oneDragonDir, Func<string, bool> fileExists, Func<string, IEnumerable<string>> listFiles)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(oneDragonDir) || fileExists == null || listFiles == null) return list;
        if (!Directory.Exists(oneDragonDir) && !fileExists(oneDragonDir)) return list;
        foreach (var full in listFiles(oneDragonDir))
        {
            string name = Path.GetFileName(full);
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            string stem = name.Substring(0, name.Length - 5);
            if (!BgiConfigWriter.IsSafeName(stem)) continue;
            list.Add(name);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// 打包：files 是「相对路径 → 绝对路径」，全部成功才留下 zip；
    /// 任何一步失败都删掉半成品（否则用户会拿到一个看着正常、其实缺一半的包）。
    /// </summary>
    public static (bool Ok, string Message) WritePackage(string zipPath, IEnumerable<KeyValuePair<string, string>> files, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(zipPath)) return (false, "没指定包放哪里。");
        string? dir = null;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(zipPath)); } catch { }
        if (dir == null) return (false, "包路径不像一个文件。");

        string tmp = zipPath + ".dbtmp";
        bool empty = false;
        try
        {
            Directory.CreateDirectory(dir);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var manifest = new Manifest { CreatedAt = now.ToString("yyyy-MM-dd HH:mm:ss") };
                int count = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in files)
                {
                    string? safe = SafeEntryPath(kv.Key);
                    if (safe == null) continue;
                    if (!File.Exists(kv.Value)) continue;
                    // 调用方可能手滑塞进一个不该备份的名字：包内清单会被原样解回来，
                    // 这里再核一次白名单，别让「备份」变成「随手打包任意文件」。
                    string leaf = safe.Substring(safe.LastIndexOf('/') + 1);
                    bool isBgi = safe.StartsWith(BgiEntry, StringComparison.OrdinalIgnoreCase);
                    if (isBgi)
                    {
                        string stem = leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                            ? leaf.Substring(0, leaf.Length - 5) : "";
                        if (!BgiConfigWriter.IsSafeName(stem)) continue;
                    }
                    else if (!IsWhitelisted(leaf))
                    {
                        continue;
                    }
                    if (!seen.Add(safe)) continue;
                    string entryName = RootEntry + safe;
                    if (isBgi)
                        manifest.BgiFiles.Add(safe.Substring(BgiEntry.Length));
                    else
                        manifest.Files.Add(safe);
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    using var src = File.OpenRead(kv.Value);
                    src.CopyTo(es);
                    count++;
                }

                // 一个文件都没有的包没有意义，别让用户以为自己备份成功了。
                // 这个判断必须在写清单**之前**：先写清单再删条目，zip 里会留下一个
                // 空目录，读回来是「有包但没东西」，比直接不产出更让人困惑。
                if (count == 0)
                {
                    empty = true;
                }
                else
                {
                    var mEntry = zip.CreateEntry(RootEntry + MarkerName, CompressionLevel.Optimal);
                    using var ms = mEntry.Open();
                    using var writer = new StreamWriter(ms, new System.Text.UTF8Encoding(false));
                    writer.Write(JsonSerializer.Serialize(manifest, ManifestOpt));
                }
            }

            if (empty)
            {
                TryDelete(tmp);
                return (false, "没有可备份的东西（配置目录里还没东西）。");
            }

            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
            return (true, zipPath);
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            return (false, "打包失败：" + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// 读包内清单。不是我们的包 / 版本读不懂 → 返回 null 并给出理由，
    /// 让界面能说「这个包不是我打出来的」而不是抛一堆异常。
    /// </summary>
    public static Manifest? ReadManifest(string zipPath, out string reason)
    {
        reason = "";
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = FindEntry(zip, RootEntry + MarkerName);
            if (entry == null)
            {
                reason = "这个包不是本软件打出来的（里面没有 backup.json）。";
                return null;
            }
            using var s = entry.Open();
            using var reader = new StreamReader(s, System.Text.Encoding.UTF8);
            var m = JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd());
            if (m == null)
            {
                reason = "包里的清单读不出来，可能已经损坏。";
                return null;
            }
            if (m.Version > FormatVersion)
            {
                reason = $"这个包来自更新的版本（格式 {m.Version}），当前只认到 {FormatVersion}。";
                return null;
            }
            m.Files ??= new List<string>();
            m.BgiFiles ??= new List<string>();
            return m;
        }
        catch (Exception ex)
        {
            reason = "打不开这个包：" + ex.Message;
            return null;
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
    {
        foreach (var e in zip.Entries)
            if (string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>
    /// 解包到目标目录。只解白名单里的文件；每一条都过 SafeEntryPath 与 IsInside。
    /// 返回解出来的文件数（0 表示这个包对我们没用，不算失败，界面照实说）。
    /// </summary>
    public static (int Restored, List<string> Skipped, string Message) Extract(string zipPath, string configDir, string? oneDragonDir, bool includeBgi)
    {
        var skipped = new List<string>();
        int restored = 0;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            Directory.CreateDirectory(configDir);
            if (includeBgi && !string.IsNullOrWhiteSpace(oneDragonDir))
                Directory.CreateDirectory(oneDragonDir);

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                string? safe = SafeEntryPath(entry.FullName);
                if (safe == null) { skipped.Add(entry.FullName); continue; }

                string target;
                string fileName = safe.Substring(safe.LastIndexOf('/') + 1);
                if (safe.StartsWith(BgiEntry, StringComparison.OrdinalIgnoreCase))
                {
                    if (!includeBgi || string.IsNullOrWhiteSpace(oneDragonDir)) { skipped.Add(safe); continue; }
                    string stem = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? fileName.Substring(0, fileName.Length - 5) : "";
                    if (!BgiConfigWriter.IsSafeName(stem)) { skipped.Add(safe); continue; }
                    target = Path.Combine(oneDragonDir, fileName);
                    if (!IsInside(oneDragonDir, target)) { skipped.Add(safe); continue; }
                }
                else if (safe.StartsWith(RootEntry, StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsWhitelisted(fileName)) { skipped.Add(safe); continue; }
                    target = Path.Combine(configDir, fileName);
                    if (!IsInside(configDir, target)) { skipped.Add(safe); continue; }
                }
                else
                {
                    skipped.Add(safe);   // 包里有我们不认识的东西，不动它
                    continue;
                }

                using var es = entry.Open();
                using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                es.CopyTo(dst);
                restored++;
            }

            if (restored == 0) return (0, skipped, "这个包里没有能用的东西。");
            return (restored, skipped, $"已恢复 {restored} 个文件。");
        }
        catch (Exception ex)
        {
            return (restored, skipped, "解包失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 恢复前的措辞：先讲清楚会覆盖什么、我们替它留了什么。
    /// 恢复是唯一一个「点一下就把现有东西换掉」的操作，不能只写「确定吗」。
    /// </summary>
    public static string RestorePrompt(Manifest? m, string zipPath)
    {
        var names = new List<string>();
        if (m != null)
        {
            foreach (var f in m.Files) names.Add(f);
            if (m.BgiFiles.Count > 0) names.Add($"{m.BgiFiles.Count} 个一条龙配置");
        }
        string what = names.Count > 0 ? string.Join("、", names) : "包内的文件";
        return $"会用包里的 {what} 覆盖当前设置。现在这份会先自动备份一份，恢复完要是发现不对，可以从「备份」文件夹拿回来。";
    }
}
