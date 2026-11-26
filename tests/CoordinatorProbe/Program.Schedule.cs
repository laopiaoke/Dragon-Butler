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
    /// v1.6 / F1 定时判定：补跑窗口必须能把「睡眠/关机错过整分钟」的日子救回来，
    /// 且不能重复跑同一个计划点。
    /// </summary>
    private static int RunSchedule()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        // 2026-11-06 是周五（bit4）。掩码 0x7F = 每天。
        var fri0800 = new DateTime(2026, 11, 6, 8, 0, 0);
        var fri0005 = new DateTime(2026, 11, 6, 0, 5, 0);

        // 1) 正好到点：该跑，但不是补跑。
        var d1 = SchedulePlanner.Decide(true, false, 8, 0, 0x7F, 120, null, fri0800);
        Check("到点该跑", d1.ShouldRun && !d1.IsCatchUp, d1.Reason);
        Check("到点 DueAt 对", d1.DueAt == fri0800, d1.DueAt?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        // 2) 睡过头 72 分钟后才开机：窗口内，补跑。
        var slept = fri0800.AddMinutes(72);
        var d2 = SchedulePlanner.Decide(true, false, 8, 0, 0x7F, 120, null, slept);
        Check("窗口内补跑", d2.ShouldRun && d2.IsCatchUp, d2.Reason);

        // 3) 超出窗口：不跑，且理由要说明超了多久。
        var d3 = SchedulePlanner.Decide(true, false, 8, 0, 0x7F, 120, null, fri0800.AddMinutes(200));
        Check("超出窗口不跑", !d3.ShouldRun && d3.DueAt == fri0800, d3.Reason);

        // 4) 该计划点已经跑过：不重复跑（哪怕还在窗口内）。
        var d4 = SchedulePlanner.Decide(true, false, 8, 0, 0x7F, 120, fri0800, fri0800.AddMinutes(30));
        Check("同计划点不重复", !d4.ShouldRun, d4.Reason);

        // 5) 补跑关闭 + 错过整分钟：不跑（老行为，本次修的就是这个）。
        var d5 = SchedulePlanner.Decide(true, false, 8, 0, 0x7F, 0, null, fri0800.AddMinutes(72));
        Check("补跑关闭则错过", !d5.ShouldRun, d5.Reason);

        // 6) 补跑关闭 + 恰好命中那一分钟：跑。
        var d6 = SchedulePlanner.Decide(true, false, 8, 0, 0x7F, 0, null, fri0800.AddSeconds(30));
        Check("补跑关闭仍认整分钟", d6.ShouldRun, d6.Reason);

        // 7) 没勾选今天（只勾周一，今天是周五）：不跑。
        var d7 = SchedulePlanner.Decide(true, false, 8, 0, 0x01, 120, null, fri0800.AddMinutes(10));
        Check("未勾选的星期不跑", !d7.ShouldRun, d7.Reason);

        // 8) 开关/在跑：两种都不跑。（v1.38 起度假模式没了，第三种没了）
        Check("定时关闭不跑", !SchedulePlanner.Decide(false, false, 8, 0, 0x7F, 120, null, fri0800).ShouldRun);
        Check("正在跑不叠加", !SchedulePlanner.Decide(true, true, 8, 0, 0x7F, 120, null, fri0800).ShouldRun);
        // 定时开着、没在跑、正好到点：必须跑。这条是删掉度假模式之后的地基——
        // 原来这格上放的是「度假模式不跑」，一删就漏出个洞，得有人站住。
        Check("定时开着到点就跑", SchedulePlanner.Decide(true, false, 8, 0, 0x7F, 120, null, fri0800).ShouldRun);

        // 9) 还没到点：不跑，且要给出下次时间。
        var d9 = SchedulePlanner.Decide(true, false, 8, 0, 0x7F, 120, null, fri0005);
        Check("未到点不跑", !d9.ShouldRun && d9.NextDue == fri0800, d9.Reason + " next=" + (d9.NextDue?.ToString("HH:mm") ?? "null"));

        // 10) 跨周：周五跑完，周一才该跑（掩码 bit0|bit4）。
        var mon0800 = new DateTime(2026, 11, 9, 8, 0, 0);
        var next = SchedulePlanner.NextDueAfter(0x11, 8, 0, fri0800.AddMinutes(1));
        Check("跨周下次点", next == mon0800, next?.ToString("yyyy-MM-dd HH:mm") ?? "null");

        // 11) 少配了窗口：clamp 不炸，文案仍然可读。
        Check("窗口文字", SchedulePlanner.WindowText(120).Contains("2 小时"), SchedulePlanner.WindowText(120));
        Check("窗口关闭文字", SchedulePlanner.WindowText(0).Contains("关闭"), SchedulePlanner.WindowText(0));

        Console.WriteLine("RESULT schedule fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.6 / F2「接着跑」验证：
    /// 1) 台账只记「跑过且不是跳过」，Done 才算跑成；
    /// 2) 失败的不算跑成，下次接着跑仍会挑出来；
    /// 3) 挑项是纯函数，不改用户真实队列配置。
    /// 用独立 ToolId 前缀，不污染真实台账（跑完清掉今天的记录）。
    /// </summary>
    private static int RunResume(string stub)
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        RunJournal.ClearToday();

        // 1) 跳过 → 不进台账，也不算跑成
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-skip", DisplayName = "跳过项",
            State = ToolRunState.Idle, Reason = "游戏在跑，先不启动", Skipped = true,
        });
        Check("跳过不进台账", RunJournal.GetDay(DateTime.Now).All(e => e.ToolId != "probe-skip"));
        Check("跳过不算跑成", !RunJournal.DoneToolsToday().Contains("probe-skip"));

        // 2) 失败 → 进台账，但不算跑成
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-fail", DisplayName = "失败项",
            State = ToolRunState.Failed, Reason = "退出码 3", Attempts = 2,
        });
        var failEntry = RunJournal.GetDay(DateTime.Now).FirstOrDefault(e => e.ToolId == "probe-fail");
        Check("失败进台账", failEntry != null);
        Check("失败记了原因与次数", failEntry?.Reason == "退出码 3" && failEntry.Attempts == 2,
            $"reason={failEntry?.Reason} attempts={failEntry?.Attempts}");
        Check("失败不算跑成", !RunJournal.DoneToolsToday().Contains("probe-fail"));

        // 3) 成功 → 进台账且算跑成
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-done", DisplayName = "成功项",
            State = ToolRunState.Done, Reason = "正常结束", Attempts = 1,
        });
        Check("成功算跑成", RunJournal.DoneToolsToday().Contains("probe-done"));

        // 4) 同一工具再记一次失败 → 覆盖，状态变为「没跑成」
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-done", DisplayName = "成功项",
            State = ToolRunState.Timeout, Reason = "超过 5 分钟", Attempts = 1,
        });
        Check("重跑覆盖为最新一次", !RunJournal.DoneToolsToday().Contains("probe-done"));
        Check("同工具当天只一条", RunJournal.GetDay(DateTime.Now).Count(e => e.ToolId == "probe-done") == 1);

        // 5) 挑项：Done 的被排除，失败的与没跑过的保留，顺序不变
        var all = new List<ToolProfile>
        {
            Stub("probe-fail", stub, "--seconds 1 --exit 3"),
            Stub("probe-never", stub, "--seconds 1 --exit 0"),
            Stub("probe-done2", stub, "--seconds 1 --exit 0"),
        };
        RunJournal.Record(new QueueItemResult
        {
            ToolId = "probe-done2", DisplayName = "已跑成",
            State = ToolRunState.Done, Reason = "正常结束", Attempts = 1,
        });
        var left = QueueService.FilterUnfinished(all);
        Check("挑项排除已跑成", left.Count == 2 && left.All(p => p.ToolId != "probe-done2"),
            string.Join(",", left.Select(p => p.ToolId)));
        Check("挑项保留顺序", left.Count == 2 && left[0].ToolId == "probe-fail" && left[1].ToolId == "probe-never");

        // 6) 没有未完成项时，接着跑不该启动
        foreach (var p in all)
            RunJournal.Record(new QueueItemResult
            {
                ToolId = p.ToolId, DisplayName = p.DisplayName,
                State = ToolRunState.Done, Reason = "正常结束", Attempts = 1,
            });
        Check("全跑成后挑不出东西", QueueService.FilterUnfinished(all).Count == 0);

        // 7) 端到端：真起一个 StubTool，跑成后进台账
        QueueService.IntervalOverride = TimeSpan.FromSeconds(1);
        var live = Stub("probe-live", stub, "--seconds 1 --exit 0");
        QueueService.RunProfilesAsync(new List<ToolProfile> { live }, QueueTrigger.Manual).GetAwaiter().GetResult();
        Check("端到端跑成进台账", RunJournal.DoneToolsToday().Contains("probe-live"),
            string.Join(",", RunJournal.DoneToolsToday()));

        // 收尾：清掉本轮的假数据，别留在用户台账里
        RunJournal.ClearToday();

        Console.WriteLine("RESULT resume fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.6 / F4「通知说清为什么」验证：断言证据拼装层，不起进程。
    /// 通知正文本身就是这一层拼出来的，所以测这里等于测通知。
    /// </summary>
    private static int RunExplain()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        // 1) 退出码进正文
        string a = RunEvidence.Describe("异常退出", 3, null);
        Check("退出码进正文", a.Contains("异常退出") && a.Contains("退出码 3"), a);

        // 2) 退出码 0 也要如实说（不是「异常退出」时不能撒谎）
        Check("退出码 0 如实写", RunEvidence.Describe("报告完成但进程未退出", 0, null).Contains("退出码 0"));

        // 3) 没有退出码就不编
        Check("无退出码不提退出码", !RunEvidence.Describe("疑似挂起", null, null).Contains("退出码"));

        // 4) 最后一条错误日志行进正文
        string b = RunEvidence.Describe("超时 120 分钟未完成", null, "ERROR 无法识别的界面 2K 分辨率");
        Check("错误行进正文", b.Contains("最后一条错误：ERROR 无法识别的界面 2K 分辨率"), b);

        // 5) 长行压成一行并截断，不把手机通知栏撑爆
        string longLine = "ERROR " + new string('x', 500);
        string compact = RunEvidence.Compact(longLine);
        Check("长行被截断", compact.Length <= RunEvidence.MaxLineChars + 1, "len=" + compact.Length);
        Check("截断保留尾部", compact.StartsWith("…", StringComparison.Ordinal));

        // 6) 换行/制表符压平
        Check("换行被压平", !RunEvidence.Compact("a\r\nb\tc").Contains('\n'));

        // 7) 空原因不至于产生空通知
        Check("空原因有兜底", RunEvidence.Describe("", null, null) == "未完成");

        // 8) Reset 清空上一轮证据，避免串轮
        RunEvidence.NoteErrorLine("ERROR 上一轮的错误");
        Check("记录错误行", RunEvidence.LastErrorLine.Contains("上一轮"));
        RunEvidence.Reset();
        Check("Reset 清空", RunEvidence.LastErrorLine.Length == 0 && RunEvidence.LastAnchorLine.Length == 0);

        // 9) 通知正文里不再出现「详情见软件日志」这种没用的兜底
        var src = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? "", "..", "..", "..", "..", "..", "Dragon Butler", "QueueService.cs"));
        Check("通知不再甩锅给日志", !src.Contains("详情见软件日志", StringComparison.Ordinal));

        Console.WriteLine("RESULT explain fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.6 / F5「重试退避可配」验证：断言等待时长计算与文案，不起进程（起进程要真等 3 秒，太慢）。
    /// </summary>
    private static int RunRetry()
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        // 1) 不翻倍时就是固定基线
        Check("不翻倍=固定基线",
            QueueService.RetryDelayCore(3, false, 1).TotalSeconds == 3
            && QueueService.RetryDelayCore(3, false, 3).TotalSeconds == 3);

        // 2) 翻倍：第一次等基线，第二次翻倍，第三次再翻倍
        Check("翻倍 3→6→12",
            QueueService.RetryDelayCore(3, true, 1).TotalSeconds == 3
            && QueueService.RetryDelayCore(3, true, 2).TotalSeconds == 6
            && QueueService.RetryDelayCore(3, true, 3).TotalSeconds == 12);

        // 3) 封顶 300 秒，不能无限翻下去
        Check("封顶 300 秒", QueueService.RetryDelayCore(3, true, 30).TotalSeconds <= QueueService.MaxRetryDelaySeconds);

        // 4) 基线为 0 且开翻倍，仍然是 0（0 乘任何数都是 0，不该变成 3）
        Check("基线 0 仍是 0", QueueService.RetryDelayCore(0, true, 5).TotalSeconds == 0);

        // 5) 负数被夹到 0，不产生负等待
        Check("负数夹到 0", QueueService.RetryDelayCore(-5, false, 1).TotalSeconds == 0);

        // 6) attempts 从 1 起算：第 1 次失败后的等待不翻倍
        Check("第 1 次失败不翻倍", QueueService.RetryDelayCore(10, true, 1).TotalSeconds == 10);

        // 7) 文案
        Check("文案 0 秒", QueueService.DescribeWait(TimeSpan.Zero) == "立刻");
        Check("文案 45 秒", QueueService.DescribeWait(TimeSpan.FromSeconds(45)) == "45 秒");
        Check("文案 2 分钟", QueueService.DescribeWait(TimeSpan.FromMinutes(2)) == "2 分钟");
        Check("文案 1.5 分钟", QueueService.DescribeWait(TimeSpan.FromSeconds(90)) == "1.5 分钟");

        // 8) 默认档案就是「重试 1 次、等 3 秒、翻倍开」——用户不动也有合理行为
        var d = new ToolProfile();
        Check("默认档案 RetryCount=1", d.RetryCount == 1);
        Check("默认档案 等 3 秒", d.RetryDelaySeconds == 3);
        Check("默认档案 翻倍开", d.RetryBackoff);

        Console.WriteLine("RESULT retry fails=" + fails);
        return fails == 0 ? 0 : 1;
    }

}
