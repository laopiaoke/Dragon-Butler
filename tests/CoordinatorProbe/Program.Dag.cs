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
    /// v1.9 / P2「依赖编排」验证：排序、门、矩阵回写都是纯函数，先断言它们；
    /// 最后真起两个 StubTool 跑一遍，确认依赖没跑成时子项被跳过（不是干等、也不是照跑）。
    /// </summary>
    private static int RunDag(string stub)
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 依赖解析 ----
        var bare = new QueueEntry { ToolId = "a" };
        Check("默认无依赖", QueueGraph.DependenciesOf(bare).Count == 0);
        Check("默认策略=跳过", bare.OnDependencyFail == QueueGraph.PolicySkip);

        var messy = new QueueEntry { ToolId = "b", DependsOn = new List<string> { " a ", "", "a", "  " } };
        var deps = QueueGraph.DependenciesOf(messy);
        Check("去空白去重", deps.Count == 1 && deps[0] == "a", string.Join("|", deps));

        // ---- 2) 拓扑排序 ----
        var e1 = new QueueEntry { ToolId = "first" };
        var e2 = new QueueEntry { ToolId = "second", DependsOn = new List<string> { "first" } };
        var e3 = new QueueEntry { ToolId = "third", DependsOn = new List<string> { "second" } };

        // 故意给反序：third, second, first
        var shuffled = new List<QueueEntry> { e3, e2, e1 };
        var ordered = QueueGraph.Order(shuffled);
        Check("排序把依赖提前",
            ordered.Select(e => e.ToolId).SequenceEqual(new[] { "first", "second", "third" }),
            string.Join("→", ordered.Select(e => e.ToolId)));
        Check("排序不丢项", ordered.Count == 3);

        // 同层保持原序（用户拖过的不该被打乱）
        var x = new QueueEntry { ToolId = "x" };
        var y = new QueueEntry { ToolId = "y" };
        var z = new QueueEntry { ToolId = "z", DependsOn = new List<string> { "x", "y" } };
        var same = QueueGraph.Order(new List<QueueEntry> { x, y, z });
        Check("同层保持原序", same.Select(e => e.ToolId).SequenceEqual(new[] { "x", "y", "z" }),
            string.Join("→", same.Select(e => e.ToolId)));

        // 环：不抛异常，环上的项原序留到最后
        var c1 = new QueueEntry { ToolId = "c1", DependsOn = new List<string> { "c2" } };
        var c2 = new QueueEntry { ToolId = "c2", DependsOn = new List<string> { "c1" } };
        var ring = QueueGraph.TryOrder(new List<QueueEntry> { c1, c2 }, out List<string> stuck);
        Check("环不抛异常", ring.Count == 2);
        Check("环上的项被标出来", stuck.Count == 2 && stuck.Contains("c1") && stuck.Contains("c2"),
            string.Join(",", stuck));
        Check("无环时没有卡住的项", QueueGraph.TryOrder(new List<QueueEntry> { e1 }, out List<string> none).Count == 1
            && none.Count == 0);

        // 依赖了不存在的工具：不能因此永远排不上
        var ghost = new QueueEntry { ToolId = "g", DependsOn = new List<string> { "not-exist" } };
        var ghostOut = QueueGraph.TryOrder(new List<QueueEntry> { ghost }, out List<string> ghostStuck);
        Check("依赖不存在的项照常排上", ghostOut.Count == 1 && ghostStuck.Count == 0);

        // ---- 3) 依赖门 ----
        var set = (string s) => new HashSet<string>(new[] { s }, StringComparer.OrdinalIgnoreCase);
        var none2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Check("无依赖=放行", QueueGraph.Gate(bare, none2, none2).Verdict == GateVerdict.Ready);

        var child = new QueueEntry { ToolId = "child", DependsOn = new List<string> { "parent" } };
        Check("依赖已完成=放行", QueueGraph.Gate(child, set("parent"), none2).Verdict == GateVerdict.Ready);
        Check("依赖没跑=等待", QueueGraph.Gate(child, none2, none2).Verdict == GateVerdict.Waiting);
        Check("依赖失败=阻断", QueueGraph.Gate(child, none2, set("parent")).Verdict == GateVerdict.Blocked);

        var blocked = QueueGraph.Gate(child, none2, set("parent"));
        Check("阻断理由说清是谁", blocked.Reason.Contains("parent"), blocked.Reason);
        var waiting = QueueGraph.Gate(child, none2, none2);
        Check("等待理由说清等谁", waiting.Reason.Contains("parent"), waiting.Reason);

        // 两个依赖只成了其一 → 还在等
        var two = new QueueEntry { ToolId = "two", DependsOn = new List<string> { "p1", "p2" } };
        var half = new HashSet<string>(new[] { "p1" }, StringComparer.OrdinalIgnoreCase);
        Check("多依赖成了其一=还在等", QueueGraph.Gate(two, half, none2).Verdict == GateVerdict.Waiting);
        var mixed = new HashSet<string>(new[] { "p1", "p2" }, StringComparer.OrdinalIgnoreCase);
        Check("多依赖全成=放行", QueueGraph.Gate(two, mixed, none2).Verdict == GateVerdict.Ready);
        Check("失败优先于等待", QueueGraph.Gate(two, half, set("p2")).Verdict == GateVerdict.Blocked);

        // 策略
        Check("策略默认跳过", !QueueGraph.ShouldAbortOnDependencyFail(child));
        child.OnDependencyFail = QueueGraph.PolicyAbort;
        Check("策略可设整队停下", QueueGraph.ShouldAbortOnDependencyFail(child));
        child.OnDependencyFail = "乱七八糟";
        Check("策略写错按跳过处理", !QueueGraph.ShouldAbortOnDependencyFail(child));
        child.OnDependencyFail = QueueGraph.PolicySkip;

        // ---- 4) 矩阵回写 ----
        var m = new List<QueueEntry>
        {
            new() { ToolId = "m1" },
            new() { ToolId = "m2" },
            new() { ToolId = "m3" },
        };
        QueueGraph.ApplyMatrix(m, new Dictionary<int, List<int>> { { 2, new List<int> { 0, 1, 2, 0 } } }, QueueGraph.PolicyAbort);
        Check("矩阵回写只写同一行", QueueGraph.DependenciesOf(m[0]).Count == 0);
        Check("矩阵回写去重且排除自己",
            QueueGraph.DependenciesOf(m[2]).SequenceEqual(new[] { "m1", "m2" }),
            string.Join(",", QueueGraph.DependenciesOf(m[2])));
        Check("矩阵回写策略一致", m[2].OnDependencyFail == QueueGraph.PolicyAbort);
        QueueGraph.ApplyMatrix(m, new Dictionary<int, List<int>>(), QueueGraph.PolicySkip);
        Check("矩阵清空后回到无依赖", m.All(e => QueueGraph.DependenciesOf(e).Count == 0));

        // 描述文案
        Check("描述/无依赖", QueueGraph.Describe(bare) == "无依赖");
        Check("描述/有依赖", QueueGraph.Describe(m == null ? bare : new QueueEntry { ToolId = "d", DependsOn = new List<string> { "p" } }).Contains("p"));

        // ---- 5) 端到端：依赖失败 → 子项被跳过，且不照跑 ----
        var savedQueue = AppState.Settings.Queue;
        var savedStrategy = AppState.Settings.FailStrategy;
        QueueService.IntervalOverride = TimeSpan.FromSeconds(1);
        AppState.Settings.FailStrategy = "Skip";
        try
        {
            AppState.Settings.Queue = new List<QueueEntry>
            {
                new() { ToolId = "probe-dag-parent", Enabled = true, DependsOn = new List<string>() },
                new()
                {
                    ToolId = "probe-dag-child", Enabled = true,
                    DependsOn = new List<string> { "probe-dag-parent" },
                    OnDependencyFail = QueueGraph.PolicySkip,
                },
            };

            var parent = Stub("probe-dag-parent", stub, "--seconds 1 --exit 3", retry: 0);
            var childProfile = Stub("probe-dag-child", stub, "--seconds 1 --exit 0");
            QueueService.RunProfilesAsync(new List<ToolProfile> { parent, childProfile }, QueueTrigger.Manual)
                .GetAwaiter().GetResult();

            var parentResult = QueueService.LastResults.FirstOrDefault(r => r.ToolId == "probe-dag-parent");
            var childResult = QueueService.LastResults.FirstOrDefault(r => r.ToolId == "probe-dag-child");
            Check("端到端/父项跑过（失败）",
                parentResult != null && parentResult.State is ToolRunState.Failed or ToolRunState.Timeout,
                parentResult?.State.ToString() ?? "null");
            Check("端到端/子项被跳过", childResult != null && childResult.Skipped,
                childResult == null ? "null" : $"skipped={childResult.Skipped}");
            Check("端到端/子项没真去跑", childResult?.State == ToolRunState.Idle,
                childResult?.State.ToString() ?? "null");
            Check("端到端/跳过理由说清依赖", childResult?.Reason.Contains("probe-dag-parent") == true,
                childResult?.Reason ?? "");

            // 父项成功 → 子项照跑
            RunJournal.ClearToday();
            AppState.Settings.Queue = new List<QueueEntry>
            {
                new() { ToolId = "probe-dag-ok", Enabled = true },
                new()
                {
                    ToolId = "probe-dag-after", Enabled = true,
                    DependsOn = new List<string> { "probe-dag-ok" },
                },
            };
            var okParent = Stub("probe-dag-ok", stub, "--seconds 1 --exit 0", retry: 0);
            var after = Stub("probe-dag-after", stub, "--seconds 1 --exit 0");
            QueueService.RunProfilesAsync(new List<ToolProfile> { okParent, after }, QueueTrigger.Manual)
                .GetAwaiter().GetResult();
            var okResult = QueueService.LastResults.FirstOrDefault(r => r.ToolId == "probe-dag-ok");
            var afterResult = QueueService.LastResults.FirstOrDefault(r => r.ToolId == "probe-dag-after");
            Check("端到端/父项成功", okResult?.State == ToolRunState.Done, okResult?.State.ToString() ?? "null");
            Check("端到端/父项成了子项照跑", afterResult != null && !afterResult.Skipped
                && afterResult.State == ToolRunState.Done,
                afterResult == null ? "null" : $"state={afterResult.State} skipped={afterResult.Skipped}");
        }
        finally
        {
            AppState.Settings.Queue = savedQueue;
            AppState.Settings.FailStrategy = savedStrategy;
            QueueService.IntervalOverride = null;
            RunJournal.ClearToday();
        }

        Console.WriteLine($"RESULT dag fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.9 / P2 配置组切片：清洗、回退全局、--startGroups 拼装、下标回写。
    /// 关键断言是「空列表＝沿用全局」，这条塌了老用户升级当天就会发现一个组都不跑。
    /// </summary>
    private static int RunGroups()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 1) 清洗 ----
        var messy = GroupSlicing.Sanitize(new[] { " 日常 ", "", "日常", " ", "活动" });
        Check("清洗去空白去重保序", messy.SequenceEqual(new[] { "日常", "活动" }), string.Join("|", messy));
        Check("清洗忽略大小写去重",
            GroupSlicing.Sanitize(new[] { "Daily", "daily" }).Count == 1);
        Check("空输入返回空列表", GroupSlicing.Sanitize(null).Count == 0 && GroupSlicing.Sanitize(Array.Empty<string>()).Count == 0);

        // ---- 2) 已知组过滤 ----
        var known = new[] { "日常", "活动" };
        var filtered = GroupSlicing.Sanitize(new[] { "日常", "已被删掉的组" }, known);
        Check("认不出的组被丢掉", filtered.SequenceEqual(new[] { "日常" }), string.Join("|", filtered));
        var noKnown = GroupSlicing.Sanitize(new[] { "随便写的" }, Array.Empty<string>());
        Check("没识别到配置组时不过滤", noKnown.Count == 1, string.Join("|", noKnown));
        Check("known=null 时不过滤", GroupSlicing.Sanitize(new[] { "x" }, null).Count == 1);

        // ---- 3) 回退全局（最重要的一条） ----
        var global = new[] { "全局A", "全局B" };
        var ownEmpty = GroupSlicing.Effective(new List<string>(), global, null);
        Check("空切片=沿用全局", ownEmpty.SequenceEqual(global), string.Join("|", ownEmpty));
        var ownNull = GroupSlicing.Effective(null, global, null);
        Check("null 切片=沿用全局", ownNull.SequenceEqual(global), string.Join("|", ownNull));
        var ownSet = GroupSlicing.Effective(new[] { "只跑这个" }, global, null);
        Check("写了切片就用切片", ownSet.SequenceEqual(new[] { "只跑这个" }), string.Join("|", ownSet));
        Check("两边都空就是空", GroupSlicing.Effective(null, Array.Empty<string>(), null).Count == 0);
        var dropped = GroupSlicing.Effective(new[] { "已删除" }, global, new[] { "全局A" });
        Check("切片全被过滤后回退全局",
            dropped.SequenceEqual(new[] { "全局A" }), string.Join("|", dropped));

        Check("UsesGlobal 认空列表", GroupSlicing.UsesGlobal(new List<string>()));
        Check("UsesGlobal 认 null", GroupSlicing.UsesGlobal(null));
        Check("UsesGlobal 对有效列表为假", !GroupSlicing.UsesGlobal(new[] { "日常" }));
        Check("UsesGlobal 对全空白为真", GroupSlicing.UsesGlobal(new[] { "  ", "" }));

        // ---- 4) 参数拼装 ----
        var arg = GroupSlicing.StartGroupsArgument(new[] { "日常", "活动" });
        Check("参数名是 --startGroups", arg.StartsWith("--startGroups ", StringComparison.Ordinal), arg);
        Check("参数带引号", arg.Contains("\"日常\"") && arg.Contains("\"活动\""), arg);
        Check("两个组之间用空格", arg == "--startGroups \"日常\" \"活动\"", arg);
        Check("无组时返回空串", GroupSlicing.StartGroupsArgument(null) == ""
            && GroupSlicing.StartGroupsArgument(Array.Empty<string>()) == "");
        Check("组名里的空格原样保留",
            GroupSlicing.StartGroupsArgument(new[] { "每日 任务" }).Contains("\"每日 任务\""));

        // ---- 5) 并集与描述 ----
        var union = GroupSlicing.Union(new IReadOnlyList<string>?[]
        {
            new[] { "日常", "活动" },
            new[] { "活动", "收尾" },
            null,
        });
        Check("并集去重保序", union.SequenceEqual(new[] { "日常", "活动", "收尾" }), string.Join("|", union));
        Check("描述/用全局", GroupSlicing.Describe(new List<string>()) == "用全局勾选的配置组");
        Check("描述/一个组", GroupSlicing.Describe(new[] { "日常" }) == "只跑 日常");
        Check("描述/两个组", GroupSlicing.Describe(new[] { "日常", "活动" }) == "只跑 日常、活动");
        var many = GroupSlicing.Describe(new[] { "a", "b", "c" });
        Check("描述/多个组带计数", many.Contains("等 3 个组"), many);

        // ---- 6) 下标回写 ----
        var entries = new List<QueueEntry>
        {
            new() { ToolId = "t1", Groups = new List<string>() },
            new() { ToolId = "t2", Groups = new List<string> { "旧值" } },
        };
        GroupSlicing.Apply(entries, new Dictionary<int, List<string>>
        {
            [0] = new List<string> { "日常", " 日常 " },
            [1] = new List<string>(),
        }, null);
        Check("回写/第 0 项写入并去重", entries[0].Groups.SequenceEqual(new[] { "日常" }), string.Join("|", entries[0].Groups));
        Check("回写/空列表清空第 1 项", entries[1].Groups.Count == 0);
        GroupSlicing.Apply(entries, new Dictionary<int, List<string>> { [99] = new List<string> { "越界" } }, null);
        Check("回写/越界下标被忽略", entries.Count == 2 && entries.All(e => !e.Groups.Contains("越界")));
        GroupSlicing.Apply(null, new Dictionary<int, List<string>>(), null);
        GroupSlicing.Apply(entries, null, null);
        Check("回写/null 参数不抛", true);

        // ---- 7) 默认值：老配置读进来必须等于以前的行为 ----
        var fresh = new QueueEntry { ToolId = "new" };
        Check("默认无切片", fresh.Groups != null && fresh.Groups.Count == 0);
        Check("默认切片=全局",
            GroupSlicing.Effective(fresh.Groups, new[] { "g1" }, null).SequenceEqual(new[] { "g1" }));

        Console.WriteLine($"RESULT groups fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    private static bool RunAddEmptyCheck()
    {
        var target = new Harvest { Mora = 7 };
        target.Add(null);
        target.Add(new Harvest());
        return target.Mora == 7;
    }

    /// <summary>用一组设置跑一遍晨报的补跑提示，返回结果（不改真实设置）。</summary>
    private static string RunCatchUpCheck(bool enabled, int mask = 0x7F)
    {
        var s = AppState.Settings;
        bool oldEnabled = s.ScheduleEnabled;
        int oldMask = s.ScheduleWeekdays;
        try
        {
            s.ScheduleEnabled = enabled;
            s.ScheduleWeekdays = mask;
            return MorningReportService.CatchUpNote();
        }
        finally
        {
            s.ScheduleEnabled = oldEnabled;
            s.ScheduleWeekdays = oldMask;
        }
    }

    /// <summary>
    /// v1.38：定时开着、今天该跑却还没跑时，晨报要开口说话。
    /// 原来是反向的「度假模式下不说话」——那条分支已经不存在了，
    /// 留着它只会永远通过，等于把一块地砖漆成绿色假装是草。
    /// </summary>
    private static bool RunCatchUpSpeaksCheck(int mask)
    {
        var s = AppState.Settings;
        bool oldEnabled = s.ScheduleEnabled;
        int oldMask = s.ScheduleWeekdays;
        try
        {
            s.ScheduleEnabled = true;
            s.ScheduleWeekdays = mask;
            return MorningReportService.CatchUpNote() != "";
        }
        finally
        {
            s.ScheduleEnabled = oldEnabled;
            s.ScheduleWeekdays = oldMask;
        }
    }

}
