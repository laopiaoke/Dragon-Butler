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
    /// v1.9 / P2 BGI 更新前置检查：版本归一、四种裁决、只读不联网、失败留底。
    /// 重点断言「读不到版本时保持沉默」——这条塌了就会天天误报。
    /// </summary>
    private static int RunBgiVersion()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 版本归一 ----
        Check("归一/去构建哈希", BgiVersionGuard.Normalize("1.2.3+abc123") == "1.2.3");
        Check("归一/去预发布尾巴", BgiVersionGuard.Normalize("1.2.3-beta") == "1.2.3");
        Check("归一/去空格", BgiVersionGuard.Normalize("  1.2.3  ") == "1.2.3");
        Check("归一/空输入", BgiVersionGuard.Normalize(null) == "" && BgiVersionGuard.Normalize("") == "");
        Check("归一/纯数字保留", BgiVersionGuard.Normalize("4") == "4");

        // ---- 2) 四种裁决 ----
        Check("裁决/没记录过=FirstSeen",
            BgiVersionGuard.Judge("", "1.2.3") == VersionVerdict.FirstSeen);
        Check("裁决/一样=Unchanged",
            BgiVersionGuard.Judge("1.2.3", "1.2.3") == VersionVerdict.Unchanged);
        Check("裁决/哈希不同不算变",
            BgiVersionGuard.Judge("1.2.3+aaa", "1.2.3+bbb") == VersionVerdict.Unchanged);
        Check("裁决/变了=Changed",
            BgiVersionGuard.Judge("1.2.3", "1.3.0") == VersionVerdict.Changed);
        Check("裁决/读不到=Unknown",
            BgiVersionGuard.Judge("1.2.3", "") == VersionVerdict.Unknown);
        Check("裁决/读不到优先于没记录",
            BgiVersionGuard.Judge("", "") == VersionVerdict.Unknown);

        // ---- 3) 文案 ----
        var changed = BgiVersionGuard.Describe(VersionVerdict.Changed, "1.2.3", "1.3.0");
        Check("文案/变了说清前后版本",
            changed.Contains("1.2.3") && changed.Contains("1.3.0"), changed);
        Check("文案/变了给出下一步", changed.Contains("BGI"), changed);
        Check("文案/没变不说话",
            BgiVersionGuard.Describe(VersionVerdict.Unchanged, "1", "1") == "");
        Check("文案/读不到不说话",
            BgiVersionGuard.Describe(VersionVerdict.Unknown, "1", "") == "");
        Check("文案/第一次记录有话说",
            BgiVersionGuard.Describe(VersionVerdict.FirstSeen, "", "1.2.3").Contains("1.2.3"));

        // ---- 4) 读不到版本时不出声、不覆盖 ----
        Check("读不存在的 exe 返回空串", BgiVersionGuard.ReadVersion(@"C:\definitely\not\here\nope.exe") == "");
        Check("空路径返回空串", BgiVersionGuard.ReadVersion("") == "" && BgiVersionGuard.ReadVersion(null) == "");
        Check("读不到时不写台账", !BgiVersionGuard.Remember("") && !BgiVersionGuard.Remember(null));

        var savedVersion = AppState.Settings.LastBgiVersion;
        try
        {
            AppState.Settings.LastBgiVersion = "1.2.3";
            Check("同版本不重复写", !BgiVersionGuard.Remember("1.2.3") && !BgiVersionGuard.Remember("1.2.3+zzz"));
            Check("新版本会被记住", BgiVersionGuard.Remember("1.3.0")
                && AppState.Settings.LastBgiVersion == "1.3.0");

            // Preflight 走真实文件：拿自己这个 exe 当替身（有版本号、离线、不依赖 BGI）
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var pf = BgiVersionGuard.Preflight(self);
            Check("Preflight/读得到真实版本", pf.Current.Length > 0, pf.Current);
            Check("Preflight/和台账一致时不出声",
                BgiVersionGuard.Judge(AppState.Settings.LastBgiVersion, pf.Current) != VersionVerdict.Unknown,
                $"ledger={AppState.Settings.LastBgiVersion} current={pf.Current}");

            AppState.Settings.LastBgiVersion = "0.0.1";
            var pf2 = BgiVersionGuard.Preflight(self);
            Check("Preflight/台账旧了会提醒", pf2.Verdict == VersionVerdict.Changed && pf2.Message.Length > 0,
                $"{pf2.Verdict} {pf2.Message}");

            var pf3 = BgiVersionGuard.Preflight(@"C:\nope\nope.exe");
            Check("Preflight/路径不存在=Unknown 且不说话",
                pf3.Verdict == VersionVerdict.Unknown && pf3.Message.Length == 0, pf3.Verdict.ToString());
        }
        finally
        {
            AppState.Settings.LastBgiVersion = savedVersion;
        }

        // ---- 5) 不联网：源码里不许出现联网调用 ----
        try
        {
            string dir = Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? "";
            for (int i = 0; i < 5 && dir.Length > 0; i++) dir = Path.GetDirectoryName(dir) ?? "";
            string src = Path.Combine(dir, "Dragon Butler", "BgiVersionGuard.cs");
            if (File.Exists(src))
            {
                string text = File.ReadAllText(src);
                Check("源码里没有联网调用",
                    !text.Contains("HttpClient", StringComparison.Ordinal)
                    && !text.Contains("WebRequest", StringComparison.Ordinal)
                    && !text.Contains("WebClient", StringComparison.Ordinal));
                Check("源码里没有下载/写文件",
                    !text.Contains("File.WriteAll", StringComparison.Ordinal)
                    && !text.Contains("DownloadFile", StringComparison.Ordinal));
            }
            else
            {
                Console.WriteLine($"  SKIP 找不到源码：{src}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  SKIP 源码扫描失败：" + ex.Message);
        }

        Console.WriteLine($"RESULT bgiupdate fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.10 / G1 收益账本 + G5 晨报补跑提示。
    /// 解析规则是这整套里最容易被 BGI 改格式搞坏的部分，所以断言写得比功能还多：
    /// 能认的、不能认的、容易误伤的（带单位/区间/千分位）都要钉住。
    /// </summary>
    private static int RunHarvest()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 数字写法 ----
        Check("FormatAmount/万元以下原样", HarvestLedger.FormatAmount(0) == "0" && HarvestLedger.FormatAmount(9999) == "9999",
            HarvestLedger.FormatAmount(9999));
        Check("FormatAmount/一万写成万", HarvestLedger.FormatAmount(10000) == "1 万", HarvestLedger.FormatAmount(10000));
        Check("FormatAmount/保留一位小数", HarvestLedger.FormatAmount(125000) == "12.5 万", HarvestLedger.FormatAmount(125000));
        Check("FormatAmount/整万不留小数点", HarvestLedger.FormatAmount(400000) == "40 万", HarvestLedger.FormatAmount(400000));
        Check("FormatAmount/十万以上取整", HarvestLedger.FormatAmount(1250000) == "125 万", HarvestLedger.FormatAmount(1250000));
        Check("FormatAmount/一亿以上换亿", HarvestLedger.FormatAmount(100000000) == "1 亿", HarvestLedger.FormatAmount(100000000));
        Check("FormatAmount/负数夹到 0", HarvestLedger.FormatAmount(-5) == "0");

        // ---- 2) 解析：该认的 ----
        var s = AppState.Settings;
        bool oldEnabled = s.HarvestEnabled;
        string? oldPath = HarvestLedger.PathOverride;
        // 断言绝不能写用户的真实账本：换到临时文件，跑完删掉
        string sandbox = Path.Combine(Path.GetTempPath(), "dragon-harvest-probe.json");
        try
        {
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
            HarvestLedger.PathOverride = sandbox;
            HarvestLedger.Reload();
            s.HarvestEnabled = true;

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("摩拉 +125000");
            var h1 = HarvestLedger.Take();
            Check("解析/摩拉带加号", h1.Mora == 125000, h1.Mora.ToString());

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("获得摩拉：1,234");
            var h2 = HarvestLedger.Take();
            Check("解析/摩拉带千分位", h2.Mora == 1234, h2.Mora.ToString());

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("拾取 甜甜花 x 12");
            var h3 = HarvestLedger.Take();
            Check("解析/拾取材料",
                h3.Materials.TryGetValue("甜甜花", out int sweet) && sweet == 12,
                string.Join(",", h3.Materials.Select(kv => kv.Key + "=" + kv.Value)));

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("摩拉 +1000");
            HarvestLedger.Observe("摩拉 +2000");
            var h4 = HarvestLedger.Take();
            Check("解析/多行累加", h4.Mora == 3000, h4.Mora.ToString());

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("狗粮经验 +80000");
            var h5 = HarvestLedger.Take();
            Check("解析/狗粮经验", h5.Exp == 80000, h5.Exp.ToString());

            // ---- 3) 解析：绝不该认的（误伤比漏算更糟） ----
            HarvestLedger.ResetPending();
            HarvestLedger.Observe("一趟全部跑完大约 10-12 小时，摩拉收益 26-28w");
            var h6 = HarvestLedger.Take();
            Check("不认/区间与单位混写", h6.IsEmpty, $"mora={h6.Mora} exp={h6.Exp}");

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("12.5万摩拉");
            var h7 = HarvestLedger.Take();
            Check("不认/无分隔符只有万", h7.IsEmpty, $"mora={h7.Mora}");

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("配置组 \"日常\" 执行结束");
            var h8 = HarvestLedger.Take();
            Check("不认/无关日志行", h8.IsEmpty);

            HarvestLedger.ResetPending();
            HarvestLedger.Observe("");
            HarvestLedger.Observe(null);
            var h9 = HarvestLedger.Take();
            Check("不认/空行与 null 不抛", h9.IsEmpty);

            // ---- 4) 开关关闭时什么都不记 ----
            s.HarvestEnabled = false;
            HarvestLedger.ResetPending();
            HarvestLedger.Observe("摩拉 +999999");
            var h10 = HarvestLedger.Take();
            Check("开关关闭时不记账", h10.IsEmpty, $"mora={h10.Mora}");

            // ---- 5) Take 会清零（否则下一趟会重复报） ----
            s.HarvestEnabled = true;
            HarvestLedger.ResetPending();
            HarvestLedger.Observe("摩拉 +100");
            var first = HarvestLedger.Take();
            var second = HarvestLedger.Take();
            Check("Take 取走即清零", first.Mora == 100 && second.IsEmpty, $"first={first.Mora} second={second.Mora}");

            // ---- 6) 文案 ----
            HarvestLedger.ResetPending();
            Check("Describe/空账不说话", HarvestLedger.Describe(new Harvest()).Length == 0);
            Check("Describe/null 不说话", HarvestLedger.Describe(null).Length == 0);
            var sample = new Harvest { Mora = 125000 };
            sample.Materials["甜甜花"] = 12;
            string desc = HarvestLedger.Describe(sample);
            Check("Describe/含摩拉与材料", desc.Contains("12.5 万") && desc.Contains("甜甜花×12"), desc);
            Check("Describe/材料最多列三个", HarvestLedger.Describe(new Harvest
            {
                Materials = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["a"] = 5, ["b"] = 4, ["c"] = 3, ["d"] = 2, ["e"] = 1,
                },
            }).Contains("等 5 种材料"));
            Check("Describe/按数量降序", HarvestLedger.Describe(new Harvest
            {
                Materials = new Dictionary<string, int>(StringComparer.Ordinal) { ["少"] = 1, ["多"] = 9 },
            }).StartsWith("本次约 多×9", StringComparison.Ordinal));

            // ---- 7) 记账与查询 ----
            var today = DateTime.Today;
            var before = HarvestLedger.GetDay(today);
            HarvestLedger.AddToDay(today, new Harvest { Mora = 500 });
            var after = HarvestLedger.GetDay(today);
            Check("按天记账累加", after.Mora == before.Mora + 500, $"{before.Mora}→{after.Mora}");
            Check("取不到的天返回空账而非 null",
                HarvestLedger.GetDay(today.AddDays(-3999)) is { } emptyDay && emptyDay.IsEmpty);
            Check("最近 N 天合计不小于今天",
                HarvestLedger.GetRecent(1).Mora >= after.Mora);
            Check("空账 Add 不改变目标",
                RunAddEmptyCheck());

            // ---- 8) 源码级约束：账本不联网、不写别的文件 ----
            try
            {
                string src = ReadRepoSources("Dragon Butler/HarvestLedger.cs");
                Check("源码/账本不联网",
                    !src.Contains("HttpClient", StringComparison.Ordinal)
                    && !src.Contains("WebRequest", StringComparison.Ordinal)
                    && !src.Contains("DownloadFile", StringComparison.Ordinal));
                Check("源码/HarvestLedger 自己只写 harvest.json",
                    src.Contains("harvest.json", StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                Console.WriteLine("  SKIP 源码扫描失败：" + ex.Message);
            }

            // ---- 9) G5 晨报补跑提示 ----
            Check("补跑提示/定时关着不说话", RunCatchUpCheck(enabled: false) == "");
            Check("补跑提示/没勾任何星期不说话", RunCatchUpCheck(enabled: true, mask: 0) == "");

            // 昨天在计划里 → 该报一句「昨天 HH:mm 没跑成」
            int yesterdayBit = SchedulePlanner.WeekdayBit(DateTime.Today.AddDays(-1));
            string shouldSpeak = RunCatchUpCheck(enabled: true, mask: 1 << yesterdayBit);
            Check("补跑提示/昨天该跑却没跑要说话",
                shouldSpeak.Contains("昨天", StringComparison.Ordinal)
                && shouldSpeak.Contains("没跑成", StringComparison.Ordinal), shouldSpeak);

            // 昨天不在计划里 → 昨天本来就不该跑，没什么可说
            int otherBit = (yesterdayBit + 1) % 7;
            string shouldSilent = RunCatchUpCheck(enabled: true, mask: 1 << otherBit);
            Check("补跑提示/昨天本来不该跑就不说话", shouldSilent == "", shouldSilent);

            // 补跑关着时措辞要换（不能还说「窗口内没跑起来」）
            var sched = AppState.Settings;
            int oldCatchUp = sched.ScheduleCatchUpMinutes;
            try
            {
                sched.ScheduleCatchUpMinutes = 0;
                string noWindow = RunCatchUpCheck(enabled: true, mask: 1 << yesterdayBit);
                Check("补跑提示/补跑关着时措辞不同",
                    noWindow.Contains("补跑是关着的", StringComparison.Ordinal), noWindow);
            }
            finally
            {
                sched.ScheduleCatchUpMinutes = oldCatchUp;
            }

            // v1.38：度假模式删了，原来这条「度假模式下晨报不说话」的断言也就没了对象。
            // 现在该守的是相反的一件事：定时开着、今天没跑，晨报必须开口。
            Check("补跑提示/定时开着就会说话", RunCatchUpSpeaksCheck(1 << yesterdayBit));
        }
        finally
        {
            s.HarvestEnabled = oldEnabled;
            HarvestLedger.ResetPending();
            HarvestLedger.PathOverride = oldPath;
            HarvestLedger.Reload();
            try { if (File.Exists(sandbox)) File.Delete(sandbox); } catch { }
        }

        Console.WriteLine($"RESULT harvest fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
