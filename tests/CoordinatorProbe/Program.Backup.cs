using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using DragonButler;

namespace CoordinatorProbe;

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。
internal static partial class Program
{
    /// <summary>
    /// v1.17：备份包的打包与解包。
    /// 这个功能真正危险的地方不在打包成功，而在解包——zip 里的路径是外部数据。
    /// 所以断言的重心是「不该落盘的一条都没落」。
    /// </summary>
    private static int RunBackup()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 白名单 ----
        Check("白名单/认得 settings", SettingsBackup.IsWhitelisted("settings.json"));
        Check("白名单/大小写不敏感", SettingsBackup.IsWhitelisted("Settings.JSON"));
        Check("白名单/认得 harvest", SettingsBackup.IsWhitelisted("harvest.json"));
        Check("白名单/拒日志", !SettingsBackup.IsWhitelisted("2026-11-16_运行.log"));
        Check("白名单/拒带目录", !SettingsBackup.IsWhitelisted("config/settings.json"));
        Check("白名单/拒反斜杠路径", !SettingsBackup.IsWhitelisted(@"config\settings.json"));
        Check("白名单/拒空与 null", !SettingsBackup.IsWhitelisted("") && !SettingsBackup.IsWhitelisted(null));
        Check("白名单/只有四个", SettingsBackup.Whitelist.Length == 4);

        // ---- 2) 解包路径校验（zip 里的名字一律当不可信输入） ----
        Check("路径/正常", SettingsBackup.SafeEntryPath("dragon-butler/settings.json") == "dragon-butler/settings.json");
        Check("路径/反斜杠归一", SettingsBackup.SafeEntryPath(@"dragon-butler\settings.json") == "dragon-butler/settings.json");
        Check("路径/拒 ..", SettingsBackup.SafeEntryPath("dragon-butler/../../../Windows/System32/a.dll") == null);
        Check("路径/拒裸 ..", SettingsBackup.SafeEntryPath("../evil.txt") == null);
        Check("路径/拒绝对路径", SettingsBackup.SafeEntryPath("/etc/passwd") == null);
        Check("路径/拒盘符", SettingsBackup.SafeEntryPath(@"C:\Windows\a.dll") == null);
        Check("路径/拒正斜杠盘符", SettingsBackup.SafeEntryPath("C:/Windows/a.dll") == null);
        Check("路径/拒双斜杠", SettingsBackup.SafeEntryPath("dragon-butler//settings.json") == null);
        Check("路径/拒 .", SettingsBackup.SafeEntryPath("dragon-butler/./settings.json") == null);
        Check("路径/拒空段", SettingsBackup.SafeEntryPath("") == null && SettingsBackup.SafeEntryPath(null) == null);

        // ---- 3) 落地位置二次核对 ----
        string root = Path.Combine(Path.GetTempPath(), "dbtest-root");
        Check("位置/里面", SettingsBackup.IsInside(root, Path.Combine(root, "settings.json")));
        Check("位置/子目录", SettingsBackup.IsInside(root, Path.Combine(root, "sub", "a.json")));
        Check("位置/外面", !SettingsBackup.IsInside(root, Path.Combine(Path.GetTempPath(), "elsewhere.json")));
        Check("位置/前缀但不同目录", !SettingsBackup.IsInside(root, root + "Other" + Path.DirectorySeparatorChar + "a.json"));
        Check("位置/空参数", !SettingsBackup.IsInside("", "a") && !SettingsBackup.IsInside(root, ""));

        // ---- 4) 收文件：只收真实存在的白名单文件 ----
        string work = Path.Combine(Path.GetTempPath(), "dbtest-backup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string cfg = Path.Combine(work, "config");
        string oneDragon = Path.Combine(work, "bgi", "User", "OneDragon");
        try
        {
            Directory.CreateDirectory(cfg);
            Directory.CreateDirectory(oneDragon);
            File.WriteAllText(Path.Combine(cfg, "settings.json"), "{\"a\":1}");
            File.WriteAllText(Path.Combine(cfg, "run_journal.json"), "{}");
            File.WriteAllText(Path.Combine(cfg, "nope.log"), "x");
            var collected = SettingsBackup.CollectFiles(cfg, File.Exists);
            Check("收文件/只收存在的", collected.Count == 2, string.Join(",", collected));
            Check("收文件/不收日志", !collected.Any(n => n.EndsWith(".log")));
            Check("收文件/顺序按白名单", collected[0] == "settings.json");
            Check("收文件/空目录返回空", SettingsBackup.CollectFiles(Path.Combine(work, "empty"), File.Exists).Count == 0);
            Check("收文件/null 安全", SettingsBackup.CollectFiles(null, File.Exists).Count == 0);

            // 一条龙配置：只收合法名
            File.WriteAllText(Path.Combine(oneDragon, "日常.json"), "{}");
            File.WriteAllText(Path.Combine(oneDragon, "坏:名字.json"), "{}");
            File.WriteAllText(Path.Combine(oneDragon, "readme.txt"), "x");
            var bgi = SettingsBackup.CollectBgiFiles(oneDragon, File.Exists,
                dir => Directory.GetFiles(dir));
            Check("收一条龙/只收合法 json", bgi.Count == 1, string.Join(",", bgi));
            Check("收一条龙/拒绝非法名", !bgi.Any(n => n.Contains(":")));

            // ---- 5) 打包 ----
            string zip = Path.Combine(work, "pack.zip");
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("settings.json", Path.Combine(cfg, "settings.json")),
                new("run_journal.json", Path.Combine(cfg, "run_journal.json")),
                new("settings.json.bak", Path.Combine(cfg, "nope.log")),   // 不存在的会被跳过
            };
            var write = SettingsBackup.WritePackage(zip, pairs, new DateTime(2026, 11, 16, 8, 30, 0));
            Check("打包/成功", write.Ok, write.Message);
            Check("打包/文件在", File.Exists(zip));
            Check("打包/没有残留临时文件", !File.Exists(zip + ".dbtmp"));

            var manifest = SettingsBackup.ReadManifest(zip, out string reason);
            Check("清单/读得出来", manifest != null, reason);
            Check("清单/版本是 1", manifest != null && manifest.Version == 1);
            Check("清单/记了时间", manifest != null && manifest.CreatedAt.StartsWith("2026-11-16"));
            Check("清单/记了两个文件", manifest != null && manifest.Files.Count == 2, manifest == null ? "" : string.Join(",", manifest.Files));
            Check("清单/不存在的文件没进包", manifest != null && !manifest.Files.Contains("settings.json.bak"));

            // ---- 6) 空包要如实说 ----
            string emptyZip = Path.Combine(work, "empty.zip");
            var emptyWrite = SettingsBackup.WritePackage(emptyZip,
                new List<KeyValuePair<string, string>>(), DateTime.Now);
            Check("打包/空包算失败", !emptyWrite.Ok && emptyWrite.Message.Contains("没有可备份"));
            Check("打包/空包不留文件", !File.Exists(emptyZip));

            // ---- 7) 解包：正常恢复 ----
            string dest = Path.Combine(work, "restore", "config");
            var ex = SettingsBackup.Extract(zip, dest, null, false);
            Check("解包/恢复了 2 个", ex.Restored == 2, ex.Message);
            Check("解包/settings 到位", File.Exists(Path.Combine(dest, "settings.json")));
            Check("解包/内容一致", File.ReadAllText(Path.Combine(dest, "settings.json")).Contains("\"a\":1"));
            Check("解包/没多出东西", Directory.GetFiles(dest).Length == 2);

            // ---- 8) 解包：坏包（自己造一个带 .. 的 zip） ----
            string evilZip = Path.Combine(work, "evil.zip");
            using (var fs = new FileStream(evilZip, FileMode.Create))
            using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                void Add(string name, string body)
                {
                    var e = z.CreateEntry(name);
                    using var s = e.Open();
                    using var w = new StreamWriter(s);
                    w.Write(body);
                }
                Add("dragon-butler/backup.json", "{\"Version\":1,\"Files\":[]}");
                Add("dragon-butler/../escape.txt", "evil");
                Add("dragon-butler/settings.json", "{\"evil\":true}");
            }
            string dest2 = Path.Combine(work, "restore2", "config");
            var ex2 = SettingsBackup.Extract(evilZip, dest2, null, false);
            Check("坏包/只恢复白名单一条", ex2.Restored == 1, ex2.Message);
            Check("坏包/越界那条被跳过", ex2.Skipped.Count >= 1);
            Check("坏包/没有写到上级目录", !File.Exists(Path.Combine(work, "restore2", "escape.txt")));

            // ---- 9) 不是我们的包：给得出人话 ----
            string alienZip = Path.Combine(work, "alien.zip");
            using (var fs = new FileStream(alienZip, FileMode.Create))
            using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("random.txt");
                using var s = e.Open();
                using var w = new StreamWriter(s);
                w.Write("hello");
            }
            var alien = SettingsBackup.ReadManifest(alienZip, out string alienReason);
            Check("别家包/null", alien == null);
            Check("别家包/说人话", alienReason.Contains("不是本软件"));

            // ---- 10) 恢复前的措辞 ----
            string prompt = SettingsBackup.RestorePrompt(manifest, zip);
            Check("措辞/说会覆盖", prompt.Contains("覆盖"));
            Check("措辞/说会先备份", prompt.Contains("备份"));
            Check("措辞/null 清单也能说", SettingsBackup.RestorePrompt(null, zip).Contains("覆盖"));

            // ---- 11) 默认名 ----
            string name = SettingsBackup.FileName(new DateTime(2026, 11, 16, 8, 30, 0));
            Check("命名/带时间", name == "一条龙管家-20261116-0830.zip", name);

            // ---- 12) 接线：界面三个按钮后面必须真有代码 ----
            string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
            Check("接线/存一份", code.Contains("private void BtnBackupNow_Click"));
            Check("接线/从包恢复", code.Contains("private void BtnBackupRestore_Click"));
            Check("接线/打开文件夹", code.Contains("private void BtnBackupOpen_Click"));
            Check("接线/恢复前先自备份", code.Contains("恢复前-"));
            Check("接线/自备份失败就不恢复", code.Contains("为了不丢东西这次没有恢复"));
            Check("接线/恢复后提示重开", code.Contains("重开程序"));

            string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
            Check("界面/三个按钮都在",
                mainXaml.Contains("Click=\"BtnBackupNow_Click\"")
                && mainXaml.Contains("Click=\"BtnBackupRestore_Click\"")
                && mainXaml.Contains("Click=\"BtnBackupOpen_Click\""));
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine($"RESULT backup fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
