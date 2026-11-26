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
    /// v1.20 / 奇思妙想 #9：先替你把话说清楚。
    ///
    /// 这块的底线是 <b>「不知道就说不知道」</b>：认不出的退出码必须给一条诚实的
    /// 「我们没收录」，而不是套一句放之四海皆准的话。猜错一次，用户下次不信这个提示。
    /// </summary>
    private static int RunTroubleshoot()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // ---- 退出码：负数补码要认得出来 ----
        var av = Troubleshoot.FromExitCode(unchecked((int)3221225477u));
        Check("退出码/访问冲突按补码认得出", av != null && av.Key == "exit.access-violation", av?.Key ?? "null");
        Check("退出码/说清和管家无关", av != null && av.Why.Contains("无关"), av?.Why ?? "");
        Check("退出码/给的是下一步不是结论", av != null && av.Todo.Contains("事件查看器"), av?.Todo ?? "");
        Check("退出码/带可点的命令", av?.Action == "view.log", av?.Action ?? "null");

        Check("退出码/缺运行库", Troubleshoot.FromExitCode(unchecked((int)3221225781u))?.Key == "exit.dll-missing");
        Check("退出码/CLR 异常", Troubleshoot.FromExitCode(unchecked((int)3762504530u))?.Key == "exit.clr-exception");
        Check("退出码/退出码 1", Troubleshoot.FromExitCode(1)?.Key == "exit.code-1");
        Check("退出码/0 不解释（成功了不该出现在这里）", Troubleshoot.FromExitCode(0) == null);
        Check("退出码/没收录返回 null", Troubleshoot.FromExitCode(12345) == null);
        Check("退出码/null 返回 null", Troubleshoot.FromExitCode(null) == null);

        // ---- 日志行 ----
        Check("日志/任务启动失败", Troubleshoot.FromLogLine("[ERR] 任务启动失败")?.Key == "log.start-failed");
        Check("日志/树脂耗尽不算故障", Troubleshoot.FromLogLine("树脂耗尽，任务结束")?.Key == "log.resin-out");
        Check("日志/大小写不敏感", Troubleshoot.FromLogLine("accessviolation exception")?.Key == "log.access-violation");
        Check("日志/没命中返回 null", Troubleshoot.FromLogLine("一切都好") == null);
        Check("日志/空与 null 返回 null",
            Troubleshoot.FromLogLine("") == null && Troubleshoot.FromLogLine(null) == null);
        Check("日志/树脂耗尽不给命令（没什么可做）", Troubleshoot.FromLogLine("树脂耗尽")?.Action == null);

        // ---- Explain：优先级与兜底 ----
        Check("综合/退出码优先于日志", Troubleshoot.Explain(1, "任务启动失败").Key == "exit.code-1");
        Check("综合/没退出码时用日志", Troubleshoot.Explain(null, "任务启动失败").Key == "log.start-failed");

        var unknownCode = Troubleshoot.Explain(9999, null);
        Check("综合/没收录的码说实话", unknownCode.Key == "exit.unknown");
        Check("综合/没收录也给出这个码本身", unknownCode.What.Contains("9999"), unknownCode.What);
        Check("综合/没收录时给行动", unknownCode.Todo.Length > 0 && unknownCode.Action == "view.log");

        var nothing = Troubleshoot.Explain(null, null);
        Check("综合/什么都没有时诚实", nothing.Key == "unknown" && nothing.Action == "view.today");
        Check("综合/兜底不编原因", !nothing.Why.Contains("可能"), nothing.Why);

        // ---- 负数在文案里要显示成无符号十进制 ----
        var negShow = Troubleshoot.Explain(unchecked((int)3221225477u) + 1, null);
        Check("文案/负数折成无符号十进制", negShow.What.Contains("3221225478"), negShow.What);
        Check("文案/不出现负号", !negShow.What.Contains("-"), negShow.What);

        // ---- ParseExitCode：从人话里捞 ----
        Check("解析/标准句式", Troubleshoot.ParseExitCode("异常退出，退出码 3221225477") == unchecked((int)3221225477u));
        Check("解析/小码", Troubleshoot.ParseExitCode("退出码 1") == 1);
        Check("解析/多个空格", Troubleshoot.ParseExitCode("退出码   42") == 42);
        Check("解析/没有退出码", Troubleshoot.ParseExitCode("超时") == null);
        Check("解析/空与 null", Troubleshoot.ParseExitCode("") == null && Troubleshoot.ParseExitCode(null) == null);
        Check("解析/超过 int 的折成补码", Troubleshoot.ParseExitCode("退出码 3762504530") == unchecked((int)3762504530u));

        // ---- Brief ----
        Check("简述/不超 60 字", Troubleshoot.Brief(av).Length <= 60, Troubleshoot.Brief(av).Length.ToString());
        Check("简述/null 返回空", Troubleshoot.Brief(null) == "");

        // ---- 表不能被清空 ----
        Check("收录/至少八条", Troubleshoot.KnownCount >= 8, Troubleshoot.KnownCount.ToString());

        // ---- 界面接线 ----
        string today = ReadRepoSources("Dragon Butler/TodayWindow.xaml.cs");
        string evidence = ReadRepoSources("Dragon Butler/RunEvidence.cs");
        Check("接线/今日窗口解释失败项", today.Contains("Troubleshoot.Explain("), "TodayWindow.xaml.cs");
        Check("接线/认不出时不加废话", today.Contains("\"unknown\" or \"exit.unknown\""), "TodayWindow.xaml.cs");
        Check("接线/台账原因里捞退出码", today.Contains("Troubleshoot.ParseExitCode"), "TodayWindow.xaml.cs");
        Check("接线/原因里退出码显示成无符号", evidence.Contains("unchecked((uint)code)"), "RunEvidence.cs");

        Console.WriteLine($"RESULT troubleshoot fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
