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
using static CoordinatorProbe.Program;

namespace CoordinatorProbe;

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。

// v1.45.3 / #15：ToolGrid 的断言从 Program.ToolGrid.cs 里搬出来，按段拆进几个 partial 文件。
// 五十二个局部变量成了字段；每段成了一个 Step 方法。断言正文一字未改。
internal sealed partial class ToolGridProbe
{
    private void Step27()
    {
        // ---- 11p) v1.40 汇报页 + 滚动显隐导航 + 五页导航 ----
        pagerCs = ReadRepoSources("Dragon Butler/Pager.cs");
        durCs = ReadRepoSources("Dragon Butler/DurationLedger.cs");
        queueCs = ReadRepoSources("Dragon Butler/QueueService.cs");

        // ① 时长账本是独立一份，别指望 RunJournal 记得住
        Check("账本/有自己的一份文件", durCs.Contains("durations.json"));
        Check("账本/与日记本分开存", durCs.Contains("Paths.Config(\"durations.json\")"));
        Check("账本/留 60 天", durCs.Contains("KeepDays = 60"));
        Check("账本/探针能换路径", durCs.Contains("public static string? PathOverride"));
        Check("账本/记一次能累加", durCs.Contains("entry.Runs += 1;") && durCs.Contains("entry.Seconds += seconds;"));
        Check("账本/不到半秒也算一动", durCs.Contains("if (seconds <= 0) seconds = 1;"));
        Check("账本/最费时间的排最前", durCs.Contains("OrderByDescending(e => e.Seconds)"));
        Check("账本/没跑完的组不记", durCs.Contains("if (rec.Start == null || rec.End == null) continue;"));
        Check("账本/组名按开始的时刻挑", durCs.Contains("CompareOrdinal(rec.Start") );
        Check("账本/写入不炸掉队列", queueCs.Contains("记耗时失败（不影响这次运行）"));
        Check("账本/秒表与开始时刻同一个瞬间", queueCs.Contains("DateTime startedAt = DateTime.Now;"));
        Check("账本/先记结果再记时长", queueCs.IndexOf("RunJournal.Record(last);", StringComparison.Ordinal) <
                                        queueCs.IndexOf("DurationLedger.Record(", StringComparison.Ordinal));

        // ② 五页：汇报在日志与主页之间
        Check("分页/日报站最左、主页在它右边", Pager.Report == 0 && Pager.Home == 1);
        Check("分页/名字对得上四页", pagerCs.Contains("\"日报\""));

        // ③ 汇报页自己
        Check("汇报页/有自己的一页", mainXaml.Contains("x:Name=\"PageReport\""));
        Check("汇报页/有合计时长", mainXaml.Contains("x:Name=\"TbReportTotal\""));
        Check("汇报页/有最近七天的柱子", mainXaml.Contains("x:Name=\"ReportBars\""));
        Check("汇报页/每个工具一块", mainXaml.Contains("x:Name=\"ReportToolHost\""));
        Check("汇报页/按配置组一块", mainXaml.Contains("x:Name=\"ReportGroupHost\""));
        Check("汇报页/说清数是怎么来的", mainXaml.Contains("这个数是哪来的"));
        Check("汇报页/切过去才刷", mainCs.Contains("if (index == Pager.Report) RefreshReportPage();"));
        Check("汇报页/0 秒那根柱子也留印子", SliceBetween(mainCs, "private void RenderReportBars", "private void RenderReportTools").Contains("? 3 :"));
        Check("汇报页/占比条固定宽度不拉伸", mainCs.Contains("160 * frac"));
        Check("汇报页/59 秒不当成 0 分钟", SliceBetween(mainCs, "private static string Minutes", "if (h > 0)").Contains("(seconds + 30) / 60"));

        // ④ 滚动显隐导航
        Check("导航/胶囊能挪位置", mainXaml.Contains("x:Name=\"NavShellShift\""));
        // v1.41：设置页四张卡各自内嵌了 ScrollViewer（卡内滚动），整份 XAML 里的 ScrollChanged 从 5 处变成 9 处。
        // 判据改成「五个整页滚动条（名字以 Scroll 开头的那些）都挂了这个处理器」——按名字数，不数总数，
        // 否则以后每加一个卡内滚动条就要回来改一次这个数字。
        Check("导航/四个整页滚动条都盯着", System.Text.RegularExpressions.Regex.Matches(mainXaml, "<ScrollViewer x:Name=\"Scroll[A-Za-z]*\"[^>]*ScrollChanged=\"PageScrollChanged\"").Count >= 4);
        Check("导航/设置卡的滚动条也盯着", mainXaml.Split(new[] { "Style=\"{StaticResource SetCardBody}\"" }, StringSplitOptions.None).Length - 1 >= 4);
        Check("导航/往下滚就收起来", SliceBetween(mainCs, "private void PageScrollChanged", "SetNavHidden(shouldHide").Contains("VerticalChange > 2"));
        Check("导航/往上滚就放回来", SliceBetween(mainCs, "private void PageScrollChanged", "SetNavHidden(shouldHide").Contains("VerticalChange < -2"));
        Check("导航/滚到顶一定看得见", mainCs.Contains("e.VerticalOffset <= 0.5"));
        Check("导航/位移和透明度一起动", SliceBetween(mainCs, "private void SetNavHidden", "var ease").Contains("NavShell.Opacity = opacity"));
        Check("导航/收起来是 220ms", mainCs.Contains("FromMilliseconds(220)"));
        Check("导航/页面不再给胶囊留一条死边", !mainXaml.Contains("Margin=\"14,10,14,96\""));

        // ⑤ 账本行为：换到临时文件，别碰用户真实的 durations.json
        durSandbox = Path.Combine(Path.GetTempPath(), "dragon-duration-probe.json");
        durOld = DurationLedger.PathOverride;
        try
        {
            try { if (File.Exists(durSandbox)) File.Delete(durSandbox); } catch { }
            DurationLedger.PathOverride = durSandbox;
            DurationLedger.ClearForTest();

            var now = DateTime.Now;
            DurationLedger.Record("bettergi", "BetterGI", TimeSpan.FromMinutes(30), now.AddMinutes(-30));
            DurationLedger.Record("bettergi", "BetterGI", TimeSpan.FromMinutes(15), now.AddMinutes(-15));
            var day = DurationLedger.GetDay(now);
            Check("账本行为/同一个工具合并成一行", day.Count == 1, day.Count.ToString());
            Check("账本行为/时长累加", day.Count == 1 && day[0].Seconds == 2700, day.Count == 1 ? day[0].Seconds.ToString() : "n/a");
            Check("账本行为/次数累加", day.Count == 1 && day[0].Runs == 2, day.Count == 1 ? day[0].Runs.ToString() : "n/a");
            Check("账本行为/Total 是 TimeSpan", day.Count == 1 && Math.Abs(day[0].Total.TotalMinutes - 45) < 0.01);

            DurationLedger.Record("ok-ww", "ok-ww", TimeSpan.FromHours(2), now.AddHours(-2));
            var day2 = DurationLedger.GetDay(now);
            Check("账本行为/最费时间的排最前", day2.Count == 2 && day2[0].ToolId == "ok-ww", day2.Count > 0 ? day2[0].ToolId : "n/a");
            Check("账本行为/合计按秒相加", Math.Abs(DurationLedger.TotalOf(now).TotalMinutes - 165) < 0.01,
                DurationLedger.TotalOf(now).TotalMinutes.ToString());

            Check("账本行为/没跑过的那天是空的", DurationLedger.GetDay(now.AddDays(-400)).Count == 0);
            Check("账本行为/最近七天给七格", DurationLedger.Recent(7).Count == 7, DurationLedger.Recent(7).Count.ToString());
            Check("账本行为/最近七天最后一格是今天", DurationLedger.Recent(7)[6].Day.Date == now.Date);
            Check("账本行为/落盘了", File.Exists(durSandbox));

            // 重新载入：值要还在
            DurationLedger.Load();
            var day3 = DurationLedger.GetDay(now);
            Check("账本行为/重启后还记得", day3.Count == 2, day3.Count.ToString());
        }
        finally
        {
            DurationLedger.PathOverride = durOld;
            DurationLedger.ClearForTest();
            try { if (File.Exists(durSandbox)) File.Delete(durSandbox); } catch { }
        }

        // v1.40：窗口高度不再由「最高的那一页」决定。
        // 改回 SizeToContent 会把设置页那 1334 的高度当窗口高，每页都被拉长、短的那页留白。
        Check("窗口/大小写死在窗口上", mainXaml.Contains("Width=\"1080\" Height=\"720\""));
        Check("窗口/不再跟着内容长", !SliceBetween(mainXaml, "<Window ", ">").Contains("SizeToContent"));
        Check("窗口/每页各滚各的", mainXaml.Contains("VerticalScrollBarVisibility=\"Auto\""));
    }

    private void Step28()
    {
        // ---- 12) v1.23 修的三处，逐条盯住别改回去 ----
        // ① 右键「移除」必须真的删，不能只是打开设置窗（v1.22 就是这么错的）
        Check("接线/移除菜单存在且内置外置分路", mainCs.Contains("() => RemoveFromList(toolId)") && mainCs.Contains("RemoveToolCompletely(toolId)"));
        Check("接线/移除前会问一句", mainCs.Contains("从今晚清单移除？配置会保留，随时可以再加回来。"));
        // ② 每秒轮询必须走 SyncToolGrid，不能每次都 Children.Clear() 重画——
        //    否则长按拖起来的那一格一秒内就被换成新对象，拖拽必断
        Check("接线/轮询不重画格子", mainCs.Contains("SyncToolGrid();"));
        Check("接线/同步只在集合变了才重画", mainCs.Contains("_gridSignature"));
        // ③ 拖拽松手必须还原边框，否则「拖到外面松手」会留一圈亮边
        Check("接线/松手还原边框", mainCs.Contains("tile.BorderBrush = runningThis"));
    }

}
