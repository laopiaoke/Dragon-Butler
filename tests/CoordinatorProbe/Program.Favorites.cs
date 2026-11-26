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
    /// v1.15 / L4：配置组「常用」。
    /// 排序、开关、收敛三件事都在 <see cref="GroupFavorites"/> 里，全是纯函数。
    /// 重点验两件事：<b>排序必须稳定</b>（没收藏的相对顺序不能被打乱），
    /// 以及<b>大小写不能当成不同的组</b>（BGI 那边同名不同大小写的情况真出现过）。
    /// </summary>
    private static int RunFavorites()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var groups = new[] { "日常", "活动", "收尾", "周本", "深渊" };

        // ---- 1) Sort：没收藏时原样返回 ----
        var same = GroupFavorites.Sort(groups, Array.Empty<string>());
        Check("排序/无收藏原样", same.SequenceEqual(groups), string.Join(",", same));
        Check("排序/null 收藏原样", GroupFavorites.Sort(groups, null).SequenceEqual(groups));
        Check("排序/null 输入返回空", GroupFavorites.Sort(null!, new[] { "日常" }).Count == 0);

        // ---- 2) Sort：收藏的排前面，其余保持原顺序（稳定） ----
        // 收藏之间也按原顺序排，不按右键先后排——因为没有拖拽，
        // 「先收藏的在前」这种顺序用户改不了也看不懂，原顺序至少是可预测的。
        var sorted = GroupFavorites.Sort(groups, new[] { "深渊", "活动" });
        Check("排序/收藏在前", sorted.Take(2).OrderBy(x => x).SequenceEqual(new[] { "深渊", "活动" }.OrderBy(x => x)),
            string.Join(",", sorted));
        Check("排序/收藏按原序不按右键序", sorted.Take(2).SequenceEqual(new[] { "活动", "深渊" }),
            string.Join(",", sorted));
        Check("排序/未收藏保持原序", sorted.Skip(2).SequenceEqual(new[] { "日常", "收尾", "周本" }), string.Join(",", sorted));
        Check("排序/总数不变", sorted.Count == groups.Length);

        // ---- 3) Sort：收藏里有不存在的组，不能凭空冒出来 ----
        var withGhost = GroupFavorites.Sort(groups, new[] { "已删除的组", "日常" });
        Check("排序/失效收藏不显示", !withGhost.Contains("已删除的组"), string.Join(",", withGhost));
        Check("排序/有效收藏仍生效", withGhost[0] == "日常", string.Join(",", withGhost));

        // ---- 4) Sort：大小写不敏感 ----
        var cases = new[] { "Daily", "Abyss", "Weekly" };
        var caseSorted = GroupFavorites.Sort(cases, new[] { "abyss" });
        Check("排序/大小写不敏感", caseSorted[0] == "Abyss", caseSorted[0]);

        // ---- 5) Toggle ----
        var t1 = GroupFavorites.Toggle(Array.Empty<string>(), "日常");
        Check("开关/新增一条", t1.Length == 1 && t1[0] == "日常", string.Join(",", t1));
        var t2 = GroupFavorites.Toggle(t1, "日常");
        Check("开关/再点一次取消", t2.Length == 0, string.Join(",", t2));
        var t3 = GroupFavorites.Toggle(new[] { "日常" }, " 日常 ");
        Check("开关/带空白也算同一个", t3.Length == 0, string.Join(",", t3));
        var t4 = GroupFavorites.Toggle(new[] { "日常" }, "");
        Check("开关/空名字忽略", t4.Length == 1 && t4[0] == "日常");
        var t5 = GroupFavorites.Toggle(new[] { "日常" }, null!);
        Check("开关/null 名字忽略", t5.Length == 1);
        var t6 = GroupFavorites.Toggle(new[] { "a", "b" }, "A");
        Check("开关/大小写不同算同一条", t6.Length == 1 && t6[0] == "b", string.Join(",", t6));

        // ---- 6) IsFavorite ----
        Check("判定/命中", GroupFavorites.IsFavorite(new[] { "日常" }, "日常"));
        Check("判定/大小写", GroupFavorites.IsFavorite(new[] { "Daily" }, "daily"));
        Check("判定/未命中", !GroupFavorites.IsFavorite(new[] { "日常" }, "活动"));
        Check("判定/空表", !GroupFavorites.IsFavorite(Array.Empty<string>(), "日常"));
        Check("判定/null 表", !GroupFavorites.IsFavorite(null, "日常"));

        // ---- 7) Prune：BGI 里删掉的组要从常用表里清掉 ----
        var pruned = GroupFavorites.Prune(new[] { "日常", "已删除", "活动" }, new[] { "日常", "活动", "收尾" });
        Check("收敛/去掉失效项", pruned.SequenceEqual(new[] { "日常", "活动" }), string.Join(",", pruned));
        Check("收敛/null 收藏返回空", GroupFavorites.Prune(null, groups).Length == 0);
        Check("收敛/全失效清空", GroupFavorites.Prune(new[] { "完全没有" }, groups).Length == 0);

        Console.WriteLine($"RESULT favorites fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.14：日志规则。
    /// 判定全在 <see cref="LogRules"/> 里，都是纯函数，可以逐条钉死；
    /// 最后用真实的日志文件验证「试一下」到底扫得对不对。
    /// </summary>
    private static int RunLogRules()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 关键词清洗 ----
        Check("清洗/去首尾空白", LogRule.Normalize("  卡住  ") == "卡住");
        Check("清洗/删换行", LogRule.Normalize("卡\n住\r") == "卡 住");
        Check("清洗/压重复空格", LogRule.Normalize("卡   住") == "卡 住");
        Check("清洗/空串", LogRule.Normalize("   ") == "");
        Check("清洗/null", LogRule.Normalize(null) == "");
        string longKw = LogRule.Normalize(new string('长', 100));
        Check("清洗/超长截断", longKw.Length == LogRule.MaxKeywordChars, longKw.Length.ToString());

        // ---- 2) 命中判定（纯函数，不碰 AppState） ----
        var rules = new List<LogRule>
        {
            new() { Id = "1", Keyword = "卡在", Behavior = LogRule.Notify, Enabled = true },
            new() { Id = "2", Keyword = "Abnormal", Behavior = LogRule.Halt, Enabled = true },
            new() { Id = "3", Keyword = "停用", Behavior = LogRule.Capture, Enabled = false },
            new() { Id = "4", Keyword = "  ", Behavior = LogRule.Notify, Enabled = true },
        };
        var hit = LogRules.Match("任务卡在传送锚点", rules);
        Check("命中/中文子串", hit.Count == 1 && hit[0].Id == "1", hit.Count.ToString());
        var hitCase = LogRules.Match("something ABNORMAL happened", rules);
        Check("命中/大小写不敏感", hitCase.Count == 1 && hitCase[0].Id == "2", hitCase.Count.ToString());
        Check("命中/停用规则不算", LogRules.Match("停用了", rules).Count == 0);
        Check("命中/空关键词不算", LogRules.Match("  ", rules).Count == 0);
        Check("命中/多规则同时命中", LogRules.Match("卡在异常 Abnormal", rules).Count == 2);
        Check("命中/空行", LogRules.Match("", rules).Count == 0);
        Check("命中/null 行", LogRules.Match(null, rules).Count == 0);

        // ---- 3) 节流（纯函数） ----
        var now = new DateTime(2026, 11, 14, 10, 0, 0);
        var fires = new Dictionary<string, DateTime>(StringComparer.Ordinal)
        {
            ["r1"] = now.AddMinutes(-3),
            ["r2"] = now.AddMinutes(-11),
        };
        Check("节流/3 分钟内不再触发", !LogRules.CooledDown("r1", now, fires));
        Check("节流/11 分钟可以触发", LogRules.CooledDown("r2", now, fires));
        Check("节流/没记录过可以触发", LogRules.CooledDown("r3", now, fires));
        Check("节流/恰好到点可以触发",
            LogRules.CooledDown("r4", now, new Dictionary<string, DateTime>(StringComparer.Ordinal)
            {
                ["r4"] = now.AddMinutes(-LogRules.CooldownMinutes),
            }));
        Check("节流/冷却时长是 10 分钟", Math.Abs(LogRules.CooldownMinutes - 10) < 0.001);

        // ---- 4) 行为目录 ----
        Check("行为/三种", LogRule.AllBehaviors.Length == 3);
        Check("行为/中文名", LogRule.BehaviorName(LogRule.Notify) == "提醒"
            && LogRule.BehaviorName(LogRule.Capture) == "留现场"
            && LogRule.BehaviorName(LogRule.Halt) == "停下队列");
        Check("行为/未知取值按提醒", LogRule.BehaviorName("瞎写") == "提醒");
        Check("行为/每种都有解释",
            LogRule.AllBehaviors.All(b => LogRule.BehaviorHint(b).Length > 0));

        // ---- 5) 试一下：真扫一个日志文件 ----
        string sandbox = Path.Combine(Path.GetTempPath(), "dragon-logrules-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            File.WriteAllLines(sandbox, new[]
            {
                "第一行：配置组 \"日常\" 加载完成",
                "任务卡在传送锚点，重试",
                "抽卡：出了个五星",
                "任务卡在传送锚点，重试",
                "Abnormal exit code 3",
                "最后一行：任务卡在传送锚点",
            }, new System.Text.UTF8Encoding(false));

            var scanRules = new List<LogRule>
            {
                new() { Id = "a", Keyword = "卡在", Behavior = LogRule.Notify },
                new() { Id = "b", Keyword = "abnormal", Behavior = LogRule.Halt },
                new() { Id = "c", Keyword = "根本不存在的词", Behavior = LogRule.Notify },
            };
            var scanned = LogRules.Scan(sandbox, scanRules, sampleCount: 2);
            Check("扫描/条数对得上", scanned.Count == 3, scanned.Count.ToString());
            Check("扫描/卡在命中 3 次", scanned[0].Count == 3, scanned[0].Count.ToString());
            Check("扫描/大小写不敏感", scanned[1].Count == 1, scanned[1].Count.ToString());
            Check("扫描/没出现是 0", scanned[2].Count == 0, scanned[2].Count.ToString());
            Check("扫描/样本不超过上限", scanned[0].Samples.Count == 2, scanned[0].Samples.Count.ToString());
            Check("扫描/样本是原文", scanned[0].Samples[0].Contains("卡在", StringComparison.Ordinal),
                scanned[0].Samples.Count > 0 ? scanned[0].Samples[0] : "");

            string text = LogRules.Describe(scanned, sandbox);
            Check("描述/含文件名", text.Contains(Path.GetFileName(sandbox), StringComparison.Ordinal), text);
            Check("描述/含命中总数", text.Contains("共命中 4 次", StringComparison.Ordinal), text);
            Check("描述/没出现的也提一句", text.Contains("没出现", StringComparison.Ordinal), text);

            string noFile = LogRules.Describe(scanned, null);
            Check("描述/没有日志文件说清楚", noFile.Contains("没找到", StringComparison.Ordinal), noFile);
        }
        finally
        {
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
        }

        // 传 null 路径时仍然按「有关键词的规则」给出 0 命中占位——
        // rules 里「停用」那条虽然 Enabled=false，但关键词有效，所以这里算 3 条（空关键词那条不算）。
        Check("扫描/空文件路径只给占位", LogRules.Scan(null, rules).Count == 3,
            LogRules.Scan(null, rules).Count.ToString());
        Check("扫描/不存在的文件不抛", LogRules.Scan(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".log"), rules).Count >= 0);

        // ---- 6) 设置里能存住（默认空表时规则区不生效） ----
        Check("设置/有一节 LogRules", AppState.Settings.LogRules != null);

        Console.WriteLine($"RESULT logrules fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
