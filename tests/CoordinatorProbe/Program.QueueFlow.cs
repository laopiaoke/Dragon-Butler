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
    /// v1.15：配置组合（多套「一条龙配置名 + 组名」反复用）。
    /// 借的是 AUTO-MAS 的多配置思路，但只保留「记住几套组合、开跑时挑一套」——
    /// 不做注入、不做自动切号，所以断言的重点全在「不会把用户的东西弄丢/混错」。
    /// </summary>
    /// <summary>
    /// v1.16 / 拖放识别。核心断言只有一句：**认不出就不猜**。
    /// 把 BetterGI 的路径写给别人，用户下次点开会得到一个起不来的工具，
    /// 而他并不知道是自己拖错了——这比「什么都没发生」糟得多。
    /// </summary>
    private static int RunDrop()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // 假磁盘：路径 → 是否存在；目录 → 里面有哪些文件
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\G\BetterGI\BetterGI.exe",
            @"C:\M\March7thAssistant.exe",
            @"C:\W\ok-ww.exe",
            @"C:\X\random.exe",
            @"C:\G\BetterGI",
            @"C:\W",
            @"C:\Empty",
        };
        var dirs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\G\BetterGI"] = new[] { @"C:\G\BetterGI\BetterGI.exe", @"C:\G\BetterGI\readme.txt" },
            [@"C:\W"] = new[] { @"C:\W\ok-ww.exe" },
            [@"C:\Empty"] = Array.Empty<string>(),
        };

        bool Exists(string p) => files.Contains(p);
        bool IsDir(string p) => dirs.ContainsKey(p);
        IEnumerable<string> ListDir(string p) => dirs.TryGetValue(p, out var v) ? v : Array.Empty<string>();

        (string ToolId, string Path)? Run(string? p) => DropAssign.Resolve(p, Exists, IsDir(p ?? ""), ListDir);

        // 1) 文件名 → 工具
        Check("文件名/BetterGI", DropAssign.ToolOf("BetterGI.exe") == "bettergi");
        Check("文件名/大小写不敏感", DropAssign.ToolOf("bettergi.EXE") == "bettergi");
        Check("文件名/三月七", DropAssign.ToolOf(@"C:\M\March7thAssistant.exe") == "march7th");
        Check("文件名/ok-ww", DropAssign.ToolOf("ok-ww.exe") == "ok-ww");
        Check("文件名/不认识返回 null", DropAssign.ToolOf("random.exe") == null);
        Check("文件名/空安全", DropAssign.ToolOf("") == null && DropAssign.ToolOf(null) == null);

        // 2) exe 直接拖进来
        var a = Run(@"C:\G\BetterGI\BetterGI.exe");
        Check("exe/认得出来", a?.ToolId == "bettergi");
        Check("exe/路径原样带回", a?.Path == @"C:\G\BetterGI\BetterGI.exe");
        Check("exe/带引号也能处理", Run("\"C:\\G\\BetterGI\\BetterGI.exe\"")?.ToolId == "bettergi");
        Check("exe/认不出就是 null", Run(@"C:\X\random.exe") == null);
        Check("exe/不存在就 null", Run(@"C:\Nope\BetterGI.exe") == null);
        Check("exe/空路径 null", Run("") == null && Run(null) == null);

        // 3) 目录拖进来：按目录名猜，再按里面的 exe 猜
        var d1 = Run(@"C:\G\BetterGI");
        Check("目录/按目录名认出", d1?.ToolId == "bettergi");
        Check("目录/路径补成里面的 exe", d1?.Path == @"C:\G\BetterGI\BetterGI.exe");

        var d2 = Run(@"C:\W");
        Check("目录/目录名不像工具名，靠里面的 exe", d2?.ToolId == "ok-ww");
        Check("目录/靠 exe 时路径也正确", d2?.Path == @"C:\W\ok-ww.exe");

        Check("目录/空的返回 null", Run(@"C:\Empty") == null);
        Check("目录/不存在的返回 null", Run(@"C:\NoSuchDir") == null);

        // 4) 拒绝语分三种，因为处理方式完全不同
        Check("拒绝/快捷方式", DropAssign.Refusal(@"C:\x\BetterGI.lnk").Contains("快捷方式"));
        Check("拒绝/认不出的 exe", DropAssign.Refusal(@"C:\x\foo.exe").Contains("不认识"));
        Check("拒绝/空目录", DropAssign.Refusal(@"C:\x\somedir").Contains("文件夹"));
        Check("拒绝/非 exe 文件", DropAssign.Refusal(@"C:\x\a.txt").Contains("只认 exe"));
        Check("拒绝/null 不抛", DropAssign.Refusal(null).Length > 0);

        // 5) 显示名
        Check("显示名/BetterGI", DropAssign.DisplayName("bettergi") == "BetterGI");
        Check("显示名/未知原样", DropAssign.DisplayName("foo") == "foo");

        // 6) 界面接线：AllowDrop 必须真的挂在窗口上，否则引导语是假的
        string xaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("界面/窗口开了 AllowDrop", xaml.Contains("AllowDrop=\"True\""));
        Check("界面/接了 DragOver", xaml.Contains("DragOver=\"OnWindowDragOver\""));
        Check("界面/接了 Drop", xaml.Contains("Drop=\"OnWindowDrop\""));

        string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("界面/认不出时给拒绝语", code.Contains("DropAssign.Refusal"));
        Check("界面/走统一的 ApplyToolPath", code.Contains("ApplyToolPath(toolId, target)"));

        // 7) 引导语说的事必须存在（v1.15 写过「拖进窗口」但没有实现）
        Check("引导语/三步里提到拖", code.Contains("拖进窗口"));

        Console.WriteLine($"RESULT drop fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// v1.16：检查更新。这里一条网络都不发——只验「给的输入能不能得到该有的话」。
    /// 真联网的那条路留给手动点，探针不该依赖 GitHub 在线。
    /// </summary>
    private static int RunUpdate()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        // 1) Parse：几种写法都要认
        Check("解析/v1.15", UpdateCheck.Parse("v1.15") == (1, 15, 0, false));
        Check("解析/1.15.0", UpdateCheck.Parse("1.15.0") == (1, 15, 0, false));
        Check("解析/大写 V", UpdateCheck.Parse("V1.15.0") == (1, 15, 0, false));
        Check("解析/预发布", UpdateCheck.Parse("v1.16.0-beta") == (1, 16, 0, true));
        Check("解析/只有主版本", UpdateCheck.Parse("2") == (2, 0, 0, false));
        Check("解析/多余后缀忽略", UpdateCheck.Parse("v1.15.0+build7") == (1, 15, 0, false));
        Check("解析/空返回 null", UpdateCheck.Parse("") == null && UpdateCheck.Parse(null) == null);
        Check("解析/乱写返回 null", UpdateCheck.Parse("abc") == null);

        // 2) Compare：字符串比较会把 1.9 判成比 1.10 新，这里必须按段比
        Check("比较/1.16 > 1.15", UpdateCheck.Compare("v1.16", "1.15.0") == 1);
        Check("比较/1.9 < 1.10", UpdateCheck.Compare("v1.9", "1.10.0") == -1);
        Check("比较/一样返回 0", UpdateCheck.Compare("v1.15", "1.15.0") == 0);
        Check("比较/本地更新返回 -1", UpdateCheck.Compare("v1.14", "1.15.0") == -1);
        Check("比较/预发布小于正式", UpdateCheck.Compare("v1.15.0-beta", "1.15.0") == -1);
        Check("比较/正式大于预发布", UpdateCheck.Compare("v1.15.0", "1.15.0-beta") == 1);
        Check("比较/patch 参与", UpdateCheck.Compare("v1.15.1", "1.15.0") == 1);
        Check("比较/认不出返回 null", UpdateCheck.Compare("abc", "1.15.0") == null);

        // 3) Describe：每种情况都要落到「所以我现在干什么」
        var noNet = UpdateCheck.Describe(null, "1.15.0", false, "连不上 GitHub");
        Check("话术/网络失败不报错", !noNet.Ok && !noNet.HasNewer);
        Check("话术/网络失败说清不影响用", noNet.Message.Contains("不影响使用"));

        var bad = UpdateCheck.Describe("abc", "1.15.0", true, null);
        Check("话术/版本号认不出就说认不出", bad.Message.Contains("不认得") && !bad.HasNewer);

        var newer = UpdateCheck.Describe("v1.16", "1.15.0", true, null);
        Check("话术/有新版标 HasNewer", newer.HasNewer);
        Check("话术/有新版带上两个版本号", newer.Message.Contains("v1.16") && newer.Message.Contains("1.15.0"));
        Check("话术/有新版给链接", newer.Url == UpdateCheck.ReleasesUrl);

        var same = UpdateCheck.Describe("v1.15", "1.15.0", true, null);
        Check("话术/最新版不骚扰", !same.HasNewer && same.Message.Contains("最新的"));

        var ahead = UpdateCheck.Describe("v1.14", "1.15.0", true, null);
        Check("话术/开发版要说明白", !ahead.HasNewer && ahead.Message.Contains("开发版"));

        // 4) 边界：这个功能不该替用户做任何事
        string src = ReadRepoSources("Dragon Butler/UpdateCheck.cs");
        Check("边界/不下载", !src.Contains("DownloadFile") && !src.Contains("WebClient"));
        Check("边界/不替换自身", !src.Contains("File.Replace") && !src.Contains("File.Copy"));
        Check("边界/不重启", !src.Contains("Restart") && !src.Contains("Start("));
        Check("边界/不发本机信息", src.Contains("Dragon-Butler-UpdateCheck"));
        Check("边界/URL 写死仓库", src.Contains("laopiaoke/Dragon-Butler"));

        // 5) 接线：命令面板那句「会自动提示」必须真的有代码在提示
        string code = ReadRepoSources("Dragon Butler/MainWindow.xaml.cs");
        Check("接线/启动时查一次", code.Contains("StartBackgroundUpdateCheck()"));
        Check("接线/命令面板走真检查", code.Contains("CheckUpdateManualAsync()"));
        Check("接线/有新版本才落日志", code.Contains("发现新版本"));
        Check("接线/有新版挂在首屏", code.Contains("TbUpdateHint"));

        string xaml = ReadRepoSources("Dragon Butler/MainWindow.xaml");
        Check("界面/提示默认隐藏", xaml.Contains("x:Name=\"TbUpdateHint\"") && xaml.Contains("Visibility=\"Collapsed\""));
        Check("界面/点了开浏览器", xaml.Contains("MouseLeftButtonUp=\"TbUpdateHint_MouseLeftButtonUp\""));

        Console.WriteLine($"RESULT update fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
