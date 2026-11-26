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
    private static int RunPresets()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        int seq = 0;
        string NewId() => "id-" + (++seq);

        // ---- 名字清洗 ----
        Check("名字/去首尾空白", ConfigPresets.NormalizeName("  日常  ") == "日常");
        Check("名字/换行转空格", ConfigPresets.NormalizeName("大号\n日常") == "大号 日常");
        Check("名字/压连续空格", ConfigPresets.NormalizeName("大号   日常") == "大号 日常");
        Check("名字/空返回空", ConfigPresets.NormalizeName("") == "");
        Check("名字/null 返回空", ConfigPresets.NormalizeName(null) == "");
        Check("名字/超长截断",
            ConfigPresets.NormalizeName(new string('a', ConfigPresets.MaxNameChars + 10)).Length == ConfigPresets.MaxNameChars);

        // ---- 抓一套 ----
        var p1 = ConfigPresets.Capture(null, " 日常 ", new[] { "日常", "", "活动" }, "大号", NewId);
        Check("抓取/Id 非空", p1.Id.Length > 0);
        Check("抓取/名字清洗过", p1.Name == "大号");
        Check("抓取/配置名去空白", ConfigPresets.Capture(null, " 日常 ", null, "x", NewId).DragonConfigName == "日常");
        Check("抓取/空组名被丢掉", p1.Groups.Length == 2, string.Join("|", p1.Groups));
        Check("抓取/没给名字就自动编号", ConfigPresets.Capture(null, "日常", null, "", NewId).Name == "配置 1");
        Check("抓取/自动编号跳过已占用",
            ConfigPresets.Capture(new[] { new ConfigPreset { Name = "配置 1" } }, "日常", null, null, NewId).Name == "配置 2");
        Check("抓取/自动编号跳过大小写变体",
            ConfigPresets.Capture(new[] { new ConfigPreset { Name = "配置 1" }, new ConfigPreset { Name = "配置 2" }, new ConfigPreset { Name = "配置 3" } },
                "日常", null, null, NewId).Name == "配置 4");

        // ---- 校验 ----
        var existing = new List<ConfigPreset> { p1 };
        Check("校验/合法返回 null",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "小号", DragonConfigName = "日常" }, existing) == null);
        Check("校验/null 拒绝", ConfigPresets.RefuseReason(null, existing) != null);
        Check("校验/空名字拒绝",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "  ", DragonConfigName = "日常" }, existing)?.Contains("名字") == true);
        Check("校验/重名拒绝",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "大号", DragonConfigName = "日常" }, existing)?.Contains("已经有一套") == true);
        Check("校验/重名不看大小写",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "DAILY", DragonConfigName = "日常" },
                new[] { new ConfigPreset { Id = "y", Name = "daily" } })?.Contains("已经有一套") == true);
        Check("校验/改自己不算重名",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = p1.Id, Name = "大号", DragonConfigName = "日常" }, existing) == null);
        Check("校验/什么都没选拒绝",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "空壳" }, existing)?.Contains("什么都没选") == true);
        Check("校验/只有组也行",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "只跑组", Groups = new[] { "日常" } }, existing) == null);
        Check("校验/只有配置名也行",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "x", Name = "只跑配置", DragonConfigName = "日常" }, existing) == null);

        var many = Enumerable.Range(0, ConfigPresets.MaxCount)
            .Select(i => new ConfigPreset { Id = "m" + i, Name = "批量 " + i, DragonConfigName = "日常" }).ToList();
        Check("校验/到达上限后拒绝",
            ConfigPresets.RefuseReason(new ConfigPreset { Id = "new", Name = "再来一套", DragonConfigName = "日常" }, many)
                ?.Contains("最多存") == true);

        // ---- 查找 ----
        Check("查找/按 Id 找到", ConfigPresets.FindById(existing, p1.Id)?.Name == "大号");
        Check("查找/大小写不敏感", ConfigPresets.FindById(existing, p1.Id.ToUpperInvariant()) != null);
        Check("查找/空 Id 返回 null", ConfigPresets.FindById(existing, "") == null);
        Check("查找/没有返回 null", ConfigPresets.FindById(existing, "不存在") == null);
        Check("查找/null 集合安全", ConfigPresets.FindById(null, "x") == null);

        // ---- 排序：用过的在前，没用过的保持原序 ----
        var sortable = new List<ConfigPreset>
        {
            new() { Id = "a", Name = "A" },
            new() { Id = "b", Name = "B", LastUsedAt = "2026-11-15 09:00:00" },
            new() { Id = "c", Name = "C", LastUsedAt = "2026-11-15 11:00:00" },
            new() { Id = "d", Name = "D" },
        };
        var sorted = ConfigPresets.Sort(sortable);
        Check("排序/最近的在前", sorted[0].Id == "c" && sorted[1].Id == "b", string.Join(",", sorted.Select(x => x.Id)));
        Check("排序/没用过的保持原序且在后", sorted[2].Id == "a" && sorted[3].Id == "d");
        Check("排序/总数不变", sorted.Count == sortable.Count);
        Check("排序/空集合安全", ConfigPresets.Sort(null).Count == 0);
        Check("排序/坏时间当没用过处理",
            ConfigPresets.Sort(new[] { new ConfigPreset { Id = "z", Name = "Z", LastUsedAt = "不是时间" } }).Count == 1);

        // ---- 失效组名：只报告不修改 ----
        var stale = new ConfigPreset { Id = "s", Name = "S", Groups = new[] { "日常", "已删除" } };
        var missing = ConfigPresets.MissingGroups(stale, new[] { "日常", "活动" });
        Check("失效组/找得到", missing.Count == 1 && missing[0] == "已删除", string.Join("|", missing));
        Check("失效组/全都在就返回空", ConfigPresets.MissingGroups(stale, new[] { "日常", "已删除" }).Count == 0);
        Check("失效组/没识别到就不判定", ConfigPresets.MissingGroups(stale, Array.Empty<string>()).Count == 0);
        Check("失效组/null 组列表安全", ConfigPresets.MissingGroups(null, new[] { "日常" }).Count == 0);
        Check("失效组/不修改原对象", stale.Groups.Length == 2);
        Check("失效组/大小写不敏感", ConfigPresets.MissingGroups(
            new ConfigPreset { Id = "t", Name = "T", Groups = new[] { "Daily" } }, new[] { "daily" }).Count == 0);

        // ---- 展开 ----
        var (cfg, grps) = ConfigPresets.Expand(p1);
        Check("展开/配置名", cfg == "日常", cfg);
        Check("展开/组名", grps.Count == 2);
        Check("展开/null 预设返回空", ConfigPresets.Expand(null).DragonConfigName == "");
        Check("展开/null 预设组为空", ConfigPresets.Expand(null).Groups.Count == 0);

        // ---- 描述 ----
        Check("描述/配置名加组数", ConfigPresets.Describe(p1) == "日常 · 2 组", ConfigPresets.Describe(p1));
        Check("描述/没有组说默认组",
            ConfigPresets.Describe(new ConfigPreset { Name = "x", DragonConfigName = "日常" }) == "日常 · 默认组");
        Check("描述/只有组没配置名",
            ConfigPresets.Describe(new ConfigPreset { Name = "x", Groups = new[] { "日常" } }) == "1 组");
        Check("描述/null 返回空", ConfigPresets.Describe(null) == "");

        // ---- 使用时间戳 ----
        var stamp = ConfigPresets.StampUsed(new DateTime(2026, 11, 15, 9, 30, 5));
        Check("时间戳/格式", stamp == "2026-11-15 09:30:05", stamp);
        Check("时间戳/能被解析回来", DateTime.TryParse(stamp, out _));

        // ---- Clone 不共享数组 ----
        var clone = p1.Clone();
        clone.Groups[0] = "改过";
        Check("克隆/组数组不共享", p1.Groups[0] == "日常", p1.Groups[0]);
        Check("克隆/Id 一致", clone.Id == p1.Id);

        Console.WriteLine($"RESULT presets fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.15 / M1：写 BetterGI 一条龙配置。
    /// 这是全软件唯一一处会改别人文件的代码，所以每一条拒绝理由都要单独试一遍：
    /// 光测「能写成功」是不够的——真正会出事的是「本不该写的时候写了」。
    /// </summary>
    private static int RunBgiWrite()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        string root = Path.Combine(Path.GetTempPath(), "dragon-bgiwrite");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        string oneDragon = Path.Combine(root, BgiConfigWriter.SubDir);
        Directory.CreateDirectory(oneDragon);
        string cfg = Path.Combine(oneDragon, "日常.json");
        File.WriteAllText(cfg, "{\n  \"name\": \"日常\",\n  \"tasks\": [],\n  \"settings\": { \"completionAction\": \"None\", \"partyName\": \"旅行者\" }\n}");

        // ---- 名字安全：挡的是「借名字把写入引到别处」 ----
        Check("名字/正常中文可以", BgiConfigWriter.IsSafeName("日常"));
        Check("名字/英文数字可以", BgiConfigWriter.IsSafeName("day-run_2"));
        Check("名字/空不行", !BgiConfigWriter.IsSafeName(""));
        Check("名字/空白不行", !BgiConfigWriter.IsSafeName("   "));
        Check("名字/null 不行", !BgiConfigWriter.IsSafeName(null));
        Check("名字/上级目录不行", !BgiConfigWriter.IsSafeName(".."));
        Check("名字/单点不行", !BgiConfigWriter.IsSafeName("."));
        Check("名字/带反斜杠不行", !BgiConfigWriter.IsSafeName(@"..\evil"));
        Check("名字/带正斜杠不行", !BgiConfigWriter.IsSafeName("a/b"));
        Check("名字/带冒号不行", !BgiConfigWriter.IsSafeName("C:日常"));
        Check("名字/首尾有空格不行", !BgiConfigWriter.IsSafeName(" 日常"));
        Check("名字/末尾有点不行", !BgiConfigWriter.IsSafeName("日常."));
        Check("名字/控制字符不行", !BgiConfigWriter.IsSafeName("日\n常"));
        Check("名字/超长不行", !BgiConfigWriter.IsSafeName(new string('a', BgiConfigWriter.MaxNameChars + 1)));
        Check("名字/刚好到上限可以", BgiConfigWriter.IsSafeName(new string('a', BgiConfigWriter.MaxNameChars)));

        // ---- 路径拼接 + 二次核对 ----
        string built = BgiConfigWriter.ConfigPath(root, "日常");
        Check("路径/拼接正确", built == cfg, built);
        Check("路径/名字不合法返回空", BgiConfigWriter.ConfigPath(root, "../x") == "");
        Check("路径/根为空返回空", BgiConfigWriter.ConfigPath("", "日常") == "");
        Check("路径/认得出在一条龙目录里", BgiConfigWriter.IsUnderOneDragon(built, root));
        Check("路径/别处的文件不认", !BgiConfigWriter.IsUnderOneDragon(Path.Combine(root, "其他.json"), root));
        Check("路径/子目录里也不认",
            !BgiConfigWriter.IsUnderOneDragon(Path.Combine(oneDragon, "sub", "x.json"), root));
        Check("路径/非 json 不认", !BgiConfigWriter.IsUnderOneDragon(Path.ChangeExtension(cfg, ".txt"), root));

        // ---- 拒绝写入的六种情况 ----
        var now = DateTime.Now;
        Check("拒绝/没填根目录",
            BgiConfigWriter.RefuseReason(cfg, "", false, true, null, null)?.Contains("路径") == true);
        Check("拒绝/名字拼接失败",
            BgiConfigWriter.RefuseReason("", root, false, true, null, null) != null);
        Check("拒绝/不在一条龙目录",
            BgiConfigWriter.RefuseReason(Path.Combine(root, "x.json"), root, false, true, null, null)
                ?.Contains("没敢写") == true);
        Check("拒绝/BGI 正在跑",
            BgiConfigWriter.RefuseReason(cfg, root, true, true, null, null)?.Contains("先关掉再改") == true);
        Check("拒绝/文件不见了",
            BgiConfigWriter.RefuseReason(cfg, root, false, false, null, null)?.Contains("已经不在了") == true);
        Check("拒绝/mtime 被动过",
            BgiConfigWriter.RefuseReason(cfg, root, false, true, now, now.AddMinutes(3))
                ?.Contains("被别的程序改过") == true);
        Check("拒绝/mtime 退回（也算被动过）",
            BgiConfigWriter.RefuseReason(cfg, root, false, true, now, now.AddMinutes(-3)) != null);
        Check("放行/一切正常", BgiConfigWriter.RefuseReason(cfg, root, false, true, now, now) == null);
        Check("放行/mtime 在 2 秒容差内",
            BgiConfigWriter.RefuseReason(cfg, root, false, true, now, now.AddSeconds(1)) == null);
        Check("放行/没给 mtime 就不比", BgiConfigWriter.RefuseReason(cfg, root, false, true, null, null) == null);

        // ---- 只改认得的那个键，别的一个都不碰 ----
        string json = File.ReadAllText(cfg);
        string? edited = BgiConfigWriter.SetString(json, BgiConfigWriter.KeyCompletionAction, "Shutdown");
        Check("改值/返回了新内容", edited != null);
        Check("改值/目标是新值", BgiConfigWriter.GetString(edited, BgiConfigWriter.KeyCompletionAction) == "Shutdown");
        Check("改值/同层别的字段没动", BgiConfigWriter.GetString(edited, "settings.partyName") == "旅行者");
        Check("改值/顶层字段没动", (edited ?? "").Contains("\"name\": \"日常\"", StringComparison.Ordinal));
        Check("改值/任务列表还在", (edited ?? "").Contains("\"tasks\": []", StringComparison.Ordinal));

        Check("改值/坏 JSON 返回 null", BgiConfigWriter.SetString("{ 不是 json", BgiConfigWriter.KeyCompletionAction, "x") == null);
        Check("改值/顶层不是对象返回 null", BgiConfigWriter.SetString("[1,2]", BgiConfigWriter.KeyCompletionAction, "x") == null);
        Check("改值/中间少一层返回 null", BgiConfigWriter.SetString("{\"name\":\"a\"}", BgiConfigWriter.KeyCompletionAction, "x") == null);
        Check("改值/空 JSON 返回 null", BgiConfigWriter.SetString("", BgiConfigWriter.KeyCompletionAction, "x") == null);
        Check("读值/读得到", BgiConfigWriter.GetString(json, BgiConfigWriter.KeyCompletionAction) == "None");
        Check("读值/键不存在返回 null", BgiConfigWriter.GetString(json, "settings.不存在") == null);
        Check("读值/类型不是字符串返回 null", BgiConfigWriter.GetString("{\"settings\":{\"x\":1}}", "settings.x") == null);

        // ---- 真正的写入：成功路径 ----
        string baseDir = Path.Combine(root, "app");
        Directory.CreateDirectory(baseDir);
        var (ok1, msg1) = BgiConfigWriter.Save(baseDir, cfg, edited!, new DateTime(2026, 11, 15, 9, 30, 0));
        Check("保存/成功", ok1, msg1);
        Check("保存/内容真的变了",
            BgiConfigWriter.GetString(File.ReadAllText(cfg), BgiConfigWriter.KeyCompletionAction) == "Shutdown");
        string bakDir = BgiConfigWriter.BackupDir(baseDir, new DateTime(2026, 11, 15, 9, 30, 0));
        Check("保存/备份目录建了", Directory.Exists(bakDir), bakDir);
        Check("保存/备份里有原件",
            Directory.Exists(bakDir) && Directory.GetFiles(bakDir, "*.json").Length == 1);
        if (Directory.Exists(bakDir))
        {
            string bak = Directory.GetFiles(bakDir, "*.json")[0];
            Check("保存/备份是改之前的内容",
                BgiConfigWriter.GetString(File.ReadAllText(bak), BgiConfigWriter.KeyCompletionAction) == "None");
        }
        Check("保存/没有留下临时文件", !File.Exists(cfg + ".dbtmp"));
        Check("保存/提示里有备份路径", msg1.Contains(bakDir, StringComparison.OrdinalIgnoreCase));

        // ---- 失败路径：文件不在 ----
        var (ok2, _) = BgiConfigWriter.Save(baseDir, Path.Combine(oneDragon, "不存在.json"), "{}", now);
        Check("保存/文件不在就失败", !ok2);

        // ---- 失败路径：备份目录建不出来 ----
        // 占位文件必须落在 config\ 下面才有用：代码拼的是 {baseDir}\config\bgi-backup，
        // 把文件放在 {baseDir}\bgi-backup 挡不住任何东西（我第一版就是这么写错的）。
        string blockedBase = Path.Combine(root, "blocked");
        Directory.CreateDirectory(Path.Combine(blockedBase, "config"));
        File.WriteAllText(Path.Combine(blockedBase, "config", "bgi-backup"), "占位");
        var (ok3, msg3) = BgiConfigWriter.Save(blockedBase, cfg, "{\"settings\":{\"completionAction\":\"None\"}}", now);
        Check("保存/备份失败就不写", !ok3, msg3);
        Check("保存/备份失败后原件没被改",
            BgiConfigWriter.GetString(File.ReadAllText(cfg), BgiConfigWriter.KeyCompletionAction) == "Shutdown");

        // ---- 备份文件名：同一秒里两次备份不会互相覆盖 ----
        string t1 = BgiConfigWriter.BackupFile(bakDir, "日常", new DateTime(2026, 11, 15, 9, 30, 0, 100));
        string t2 = BgiConfigWriter.BackupFile(bakDir, "日常", new DateTime(2026, 11, 15, 9, 30, 0, 700));
        Check("备份名/同一秒内不重名", t1 != t2);
        Check("备份名/保留原名", t1.Contains("日常", StringComparison.Ordinal));
        Check("备份名/名字不合法时兜底", BgiConfigWriter.BackupFile(bakDir, "../x", now).Contains("config", StringComparison.Ordinal));

        // ---- 清理 ----
        try { Directory.Delete(root, true); } catch { }

        Console.WriteLine($"RESULT bgiwrite fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
