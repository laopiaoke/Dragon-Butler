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
    /// v1.17 / M1-B：一条龙「跑完后动作」。
    /// 最要紧的三件事：候选只能来自用户自己的配置（不硬编码枚举）、
    /// `Sleep` 与 `sleep` 是两个值不能合并、写入前必须过那四道拒写闸。
    /// </summary>
    private static int RunCompletion()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 候选值清洗 ----
        Check("清洗/去首尾空白", DragonCompletion.CleanValue("  Shutdown  ") == "Shutdown");
        Check("清洗/换行转掉", DragonCompletion.CleanValue("Sleep\n") == "Sleep");
        Check("清洗/中间控制字符去掉", !DragonCompletion.CleanValue("Sle\tep").Contains('\t'));
        Check("清洗/空返回空", DragonCompletion.CleanValue("") == "");
        Check("清洗/全空白返回空", DragonCompletion.CleanValue("   ") == "");
        Check("清洗/null 返回空", DragonCompletion.CleanValue(null) == "");
        Check("清洗/超长截断", DragonCompletion.CleanValue(new string('a', 200)).Length == DragonCompletion.MaxValueChars);

        // ---- 目标文件：留空就不改 ----
        string dir = Path.Combine(Path.GetTempPath(), "dragon-completion");
        Check("目标/正常拼出", DragonCompletion.TargetJson(dir, "日常") == Path.Combine(dir, "日常.json"));
        Check("目标/名字留空返回 null", DragonCompletion.TargetJson(dir, "") == null);
        Check("目标/名字不安全返回 null", DragonCompletion.TargetJson(dir, "../x") == null);
        Check("目标/目录为空返回 null", DragonCompletion.TargetJson("", "日常") == null);
        Check("目标/名字 null 返回 null", DragonCompletion.TargetJson(dir, null) == null);

        // ---- 扫描：候选全部来自文件，且大小写不合并 ----
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(dir, "日常.json")] =
                "{\"name\":\"日常\",\"settings\":{\"completionAction\":\"Shutdown\"}}",
            [Path.Combine(dir, "深渊.json")] =
                "{\"name\":\"深渊\",\"settings\":{\"completionAction\":\"Shutdown\"}}",
            [Path.Combine(dir, "挖矿.json")] =
                "{\"name\":\"挖矿\",\"settings\":{\"completionAction\":\"Sleep\"}}",
            [Path.Combine(dir, "小写.json")] =
                "{\"name\":\"小写\",\"settings\":{\"completionAction\":\"sleep\"}}",
            [Path.Combine(dir, "没设.json")] =
                "{\"name\":\"没设\",\"settings\":{}}",
            [Path.Combine(dir, "坏了.json")] = "{ 这不是 json",
            [Path.Combine(dir, "说明.txt")] = "ignore",
        };
        string? ReadFile(string p) => files.TryGetValue(p, out var t) ? t : null;
        IEnumerable<string> ListAll(string _) => files.Keys;

        var options = DragonCompletion.Scan(dir, ReadFile, ListAll);
        var values = options.Select(o => o.Value).ToList();
        Check("扫描/只收出现过的值", values.Count == 3, string.Join(",", values));
        Check("扫描/含 Shutdown", values.Contains("Shutdown"));
        Check("扫描/含 Sleep", values.Contains("Sleep"));
        Check("扫描/大写和小写是两个值", values.Contains("sleep") && values.Count(v => v.Equals("sleep", StringComparison.OrdinalIgnoreCase)) == 2);
        Check("扫描/坏了的那份不参与", !options.Any(o => o.UsedBy.Contains("坏了")));
        Check("扫描/没设值的不参与", !options.Any(o => o.UsedBy.Contains("没设")));
        Check("扫描/不收非 json", !options.Any(o => o.UsedBy.Contains("说明")));
        var shutdown = options.First(o => o.Value == "Shutdown");
        Check("扫描/记录哪些配置在用", shutdown.UsedBy.Count == 2, string.Join(",", shutdown.UsedBy));
        Check("扫描/UsedBy 不重复", options.All(o => o.UsedBy.Distinct(StringComparer.OrdinalIgnoreCase).Count() == o.UsedBy.Count));
        Check("扫描/按值排序", values.SequenceEqual(options.Select(o => o.Value).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ThenBy(v => v, StringComparer.Ordinal)));
        Check("扫描/目录空返回空", DragonCompletion.Scan("", ReadFile, ListAll).Count == 0);
        Check("扫描/目录不存在不抛", DragonCompletion.Scan(Path.Combine(dir, "没有"), ReadFile, p => throw new IOException()).Count == 0);
        Check("扫描/读文件抛异常跳过", DragonCompletion.Scan(dir, _ => throw new IOException(), ListAll).Count == 0);

        // ---- 读当前值 ----
        Check("当前值/读得到", DragonCompletion.CurrentValue(files[Path.Combine(dir, "日常.json")]) == "Shutdown");
        Check("当前值/没设返回空", DragonCompletion.CurrentValue(files[Path.Combine(dir, "没设.json")]) == "");
        Check("当前值/坏 JSON 返回空", DragonCompletion.CurrentValue("{ 坏") == "");

        // ---- 比较：区分大小写 ----
        Check("比较/相同为真", DragonCompletion.IsSameValue("Sleep", "Sleep"));
        Check("比较/大小写不同为假", !DragonCompletion.IsSameValue("Sleep", "sleep"));
        Check("比较/两个空为真", DragonCompletion.IsSameValue("", ""));
        Check("比较/null 与空等价", DragonCompletion.IsSameValue(null, ""));

        // ---- 改值：只动那一个键 ----
        string src = files[Path.Combine(dir, "日常.json")];
        string? applied = DragonCompletion.Apply(src, "Sleep");
        Check("改值/返回新内容", applied != null);
        Check("改值/目标变了", DragonCompletion.CurrentValue(applied!) == "Sleep");
        // 注意：SetString 走 WriteIndented，写回去是 "name": "日常"（冒号后有空格），
        // 所以这里按读回来的值判断，不去匹配格式化后的字面量。
        Check("改值/别的字段没动", BgiConfigWriter.GetString(applied, "name") == "日常",
            BgiConfigWriter.GetString(applied, "name") ?? "null");
        Check("改值/中文没被转义", (applied ?? "").Contains("日常", StringComparison.Ordinal));
        Check("改值/空值返回 null", DragonCompletion.Apply(src, "") == null);
        Check("改值/结构不认识返回 null", DragonCompletion.Apply("{\"name\":\"x\"}", "Sleep") == null);
        Check("改值/坏 JSON 返回 null", DragonCompletion.Apply("{ 坏", "Sleep") == null);
        Check("改值/值会先清洗", DragonCompletion.CurrentValue(DragonCompletion.Apply(src, "  Sleep  ") ?? "") == "Sleep");

        // ---- 下拉文案：呈现事实，不编中文含义 ----
        Check("文案/带用例", DragonCompletion.OptionText(shutdown).Contains("Shutdown") && DragonCompletion.OptionText(shutdown).Contains("日常"));
        Check("文案/没用例就只给值", DragonCompletion.OptionText(new CompletionOption { Value = "X" }) == "X");
        Check("文案/null 返回空", DragonCompletion.OptionText(null) == "");

        // ---- 确认弹窗必须说清三件事 ----
        string confirm = DragonCompletion.ConfirmText("日常", "Shutdown", "Sleep");
        Check("确认/含文件名", confirm.Contains("日常.json", StringComparison.Ordinal));
        Check("确认/含从什么到什么", confirm.Contains("Shutdown", StringComparison.Ordinal) && confirm.Contains("Sleep", StringComparison.Ordinal));
        Check("确认/提到会备份", confirm.Contains("bgi-backup", StringComparison.Ordinal));
        Check("确认/说明含义由 BGI 决定", confirm.Contains("BetterGI", StringComparison.Ordinal));
        Check("确认/原值为空时说不空转", DragonCompletion.ConfirmText("日常", "", "Sleep").Contains("现在是空的", StringComparison.Ordinal));

        // ---- 接线：界面里真的接上了 ----
        string mainXaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        string mainCs = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("接线/下拉在界面上", mainXaml.Contains("\"CbCompletion\"", StringComparison.Ordinal));
        Check("接线/有提示行", mainXaml.Contains("\"TbCompletionHint\"", StringComparison.Ordinal));
        Check("接线/选了会处理", mainXaml.Contains("CbCompletion_SelectionChanged", StringComparison.Ordinal));
        Check("接线/写入前过拒写闸", mainCs.Contains("BgiConfigWriter.RefuseReason(target", StringComparison.Ordinal));
        Check("接线/写入走 Save（带备份）", mainCs.Contains("BgiConfigWriter.Save(AppContext.BaseDirectory, target", StringComparison.Ordinal));
        Check("接线/不忽略大小写选值", mainCs.Contains("SelectCompletionByValue", StringComparison.Ordinal));
        Check("接线/有节流", mainCs.Contains("_completionRefreshedAt", StringComparison.Ordinal));

        Console.WriteLine($"RESULT completion fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.20 / 奇思妙想 #1：复盘。
    ///
    /// 这一段要钉住的不是「能拼出句子」，而是三件不肯让步的事：
    /// ① 不编——没有数据就不出那一条，绝不拿「一切正常」凑数；
    /// ② 顺序——Lead 永远第一，Gap 永远最后，中间最多一条 Detail；
    /// ③ 不报流水账——四行封顶。
    /// </summary>
    private static int RunRecap()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        var start = new DateTime(2026, 11, 17, 23, 4, 0);

        // 一切都好、还比平时快：Lead 说全成，Detail 说快了多少，没有 Gap
        Recap.Input Good() => new(
            StartAt: start,
            EndAt: start.AddMinutes(41),
            DoneTools: 2,
            TotalTools: 2,
            Groups: new[]
            {
                new Recap.GroupRow("日常", TimeSpan.FromMinutes(18), false, 22),
            },
            Tools: new[]
            {
                new Recap.ToolRow("BetterGI", nameof(ToolRunState.Done), null),
                new Recap.ToolRow("三月七助手", nameof(ToolRunState.Done), null),
            },
            Mora: 0,
            Exp: 0,
            Materials: null,
            LastErrorLine: null,
            StoppedByUser: false,
            RanNothing: false);

        // ---- 1) 全成 ----
        var good = Recap.Build(Good());
        Check("全成/有话说", good.Count >= 2, good.Count.ToString());
        Check("全成/第一条是 Lead", good[0].Kind == Recap.LineKind.Lead);
        Check("全成/说了几项跑成", good[0].Text.Contains("项都跑成了"), good[0].Text);
        Check("全成/带开跑时刻", good[0].Text.Contains("23:04"), good[0].Text);
        Check("全成/没有 Gap", good.All(l => l.Kind != Recap.LineKind.Gap), string.Join(" | ", good.Select(l => l.Kind)));
        Check("全成/提到比往常快", good.Any(l => l.Text.Contains("比往常快")), string.Join(" | ", good.Select(l => l.Text)));

        // ---- 2) 有失败：Gap 要最后一条，且带原因 ----
        var bad = Recap.Build(Good() with
        {
            DoneTools = 1,
            Tools = new[]
            {
                new Recap.ToolRow("BetterGI", nameof(ToolRunState.Done), null),
                new Recap.ToolRow("三月七助手", nameof(ToolRunState.Failed), "退出码 1"),
            },
        });
        Check("失败/说了几成几败", bad[0].Text.Contains("1 项跑成") && bad[0].Text.Contains("1 项没成"), bad[0].Text);
        Check("失败/最后一条是 Gap", bad[^1].Kind == Recap.LineKind.Gap, bad[^1].Kind.ToString());
        Check("失败/Gap 带名字", bad[^1].Text.Contains("三月七助手"), bad[^1].Text);
        Check("失败/Gap 带原因", bad[^1].Text.Contains("退出码 1"), bad[^1].Text);

        // ---- 3) 一项都没跑：只说一句，不硬凑 ----
        var none = Recap.Build(Good() with { RanNothing = true, DoneTools = 0, TotalTools = 0, Groups = null, Tools = null });
        Check("空/只有一句", none.Count == 1, none.Count.ToString());
        Check("空/说的是没跑", none[0].Text.Contains("没跑"), none[0].Text);

        // ---- 4) 用户主动停 ----
        var stopped = Recap.Build(Good() with { StoppedByUser = true });
        Check("停/第一条说停了", stopped[0].Text.Contains("你让它停了"), stopped[0].Text);
        Check("停/不出 Gap（是他自己停的，不算没做到）", stopped.All(l => l.Kind != Recap.LineKind.Gap),
            string.Join(" | ", stopped.Select(l => l.Kind)));

        // ---- 5) 比往常慢 ----
        var slow = Recap.Build(Good() with
        {
            Groups = new[] { new Recap.GroupRow("日常", TimeSpan.FromMinutes(40), false, 20) },
        });
        Check("慢/说慢了", slow.Any(l => l.Text.Contains("比往常慢")), string.Join(" | ", slow.Select(l => l.Text)));
        Check("慢/带百分比", slow.Any(l => l.Text.Contains("%")), string.Join(" | ", slow.Select(l => l.Text)));

        // ---- 6) 偏离不大就不说（±15% 以内是噪声） ----
        var same = Recap.Build(Good() with
        {
            Groups = new[] { new Recap.GroupRow("日常", TimeSpan.FromMinutes(21), false, 20) },
        });
        Check("相近/不说快慢", same.All(l => !l.Text.Contains("比往常")), string.Join(" | ", same.Select(l => l.Text)));

        // ---- 7) 样本不足：宁可不说，也不给个错数 ----
        var noMedian = Recap.Build(Good() with
        {
            Groups = new[] { new Recap.GroupRow("日常", TimeSpan.FromMinutes(18), false, null) },
        });
        Check("无样本/不说比往常", noMedian.All(l => !l.Text.Contains("比往常")), string.Join(" | ", noMedian.Select(l => l.Text)));

        // ---- 8) 没时间可评就说收益 ----
        var income = Recap.Build(Good() with
        {
            Groups = null,
            Mora = 12000,
            Exp = 30,
            Materials = new Dictionary<string, int>(StringComparer.Ordinal) { ["刀镡"] = 12 },
        });
        Check("收益/提到摩拉", income.Any(l => l.Text.Contains("摩拉")), string.Join(" | ", income.Select(l => l.Text)));

        // ---- 9) 配置组没结束 ----
        var unfinished = Recap.Build(Good() with
        {
            DoneTools = 0,
            TotalTools = 0,
            Tools = null,
            Groups = new[]
            {
                new Recap.GroupRow("日常", TimeSpan.FromMinutes(18), false, 20),
                new Recap.GroupRow("挖矿", null, true, null),
            },
        });
        Check("未结束/说了一组没结束", unfinished[0].Text.Contains("没结束"), unfinished[0].Text);
        Check("未结束/Gap 点名", unfinished[^1].Text.Contains("挖矿"), unfinished[^1].Text);

        // ---- 10) 顺序与封顶：任何输入都不许超过 4 行，Lead 第一 Gap 最后 ----
        var cases = new List<IReadOnlyList<Recap.Line>> { good, bad, none, stopped, slow, same, noMedian, income, unfinished };
        Check("封顶/最多 4 行", cases.All(c => c.Count <= 4), string.Join(",", cases.Select(c => c.Count)));
        Check("封顶/至少 1 行", cases.All(c => c.Count >= 1), string.Join(",", cases.Select(c => c.Count)));
        Check("顺序/Lead 永远第一", cases.All(c => c[0].Kind == Recap.LineKind.Lead));
        Check("顺序/Gap 只在最后", cases.All(c => c.Count(l => l.Kind == Recap.LineKind.Gap) <= 1
                                                  && (c[^1].Kind == Recap.LineKind.Gap
                                                      || c.All(l => l.Kind != Recap.LineKind.Gap))));

        // ---- 11) 不说教、不出现产品腔 ----
        string all = string.Join(" ", cases.SelectMany(c => c.Select(l => l.Text)));
        Check("语气/没有「建议你」", !all.Contains("建议你"), all);
        Check("语气/没有「请注意」", !all.Contains("请注意"), all);
        Check("语气/没有「为了更好」", !all.Contains("为了更好"), all);
        Check("语气/没有感叹号", !all.Contains("！"), all);

        // ---- 12) Duration 人话 ----
        Check("时长/不到一分钟", Recap.Duration(TimeSpan.FromSeconds(20)) == "不到 1 分钟");
        Check("时长/分钟", Recap.Duration(TimeSpan.FromMinutes(18)) == "18 分钟");
        Check("时长/整小时", Recap.Duration(TimeSpan.FromMinutes(120)) == "2 小时");
        Check("时长/小时加分钟", Recap.Duration(TimeSpan.FromMinutes(95)) == "1 小时 35 分");
        Check("时长/负数当零", Recap.Duration(TimeSpan.FromMinutes(-5)) == "不到 1 分钟");

        // ---- 13) null 输入不抛 ----
        Check("空输入/不抛", Recap.Build(null!).Count == 0);

        // ---- 14) 接线：主卡里真的渲染了 ----
        string xaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("接线/主卡有复盘容器", xaml.Contains("HomeRecap"), "MainWindow.xaml");
        Check("接线/有刷新方法", code.Contains("private void RefreshHomeRecap()"), "MainWindow.xaml.cs");
        Check("接线/刷主卡时会一起刷", code.Contains("RefreshHomeRecap();"), "MainWindow.xaml.cs");
        Check("接线/取数走五张表", code.Contains("GroupArchive.GetDay") && code.Contains("RunJournal.GetDay")
                                && code.Contains("HarvestLedger.GetDay"), "MainWindow.xaml.cs");
        Check("接线/正在跑时不显示（免得以为跑完了）", code.Contains("!QueueService.IsRunning"), "MainWindow.xaml.cs");

        Console.WriteLine($"RESULT recap fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
