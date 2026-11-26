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
    /// v1.45 / M12-6：把替身路径定成绝对路径。
    ///
    /// <para>
    /// 顺序是：已经是绝对路径就用它；否则先按当前目录拼，拼不出来再试探针自己的
    /// 输出目录往上找 <c>tests/StubTool/...</c>——本地手跑时人常常就在仓库根目录，
    /// 而 CI 里跑的时候工作目录是探针自己的 bin。两边都能对上，参数怎么传都不影响结果。
    /// </para>
    /// </summary>
    private static string ResolveStub(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);

        var tries = new List<string>
        {
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", path)),
        };
        foreach (string t in tries) if (File.Exists(t)) return t;
        // 都找不到就把第一个结果交出去：后面那句 STUB-NOT-FOUND 会打出完整路径，
        // 比抛一个 InvalidOperationException 好查。
        return tries[0];
    }

    /// <summary>
    /// v1.45.3 / #15：CI 点名的组。金丝雀拿它核对——少注册一个入口，
    /// 以前只会静默少跑那一条，现在当场红。
    /// </summary>
    private static readonly string[] CiGroups =
    {
        "schedule", "resume", "bundle", "explain", "retry", "api", "weekly",
        "hotkey", "progress", "command", "dag", "groups", "bgiupdate", "harvest",
        "mcp", "uirules", "signals", "afterrun", "migrate", "wrapup", "backup", "m12fix",
    };

    /// <summary>
    /// 入口总数。新增一个组时这里会红一次——这是故意的：拆分之后「忘了把分支加回去」
    /// 不会报错，只会少跑，而少跑的那条恰恰可能是最危险的那条。
    /// </summary>
    private const int ExpectedModeCount = 84;

    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "help";
        // v1.45 / M12-6：替身路径一律换成绝对路径。
        //
        // 原来用的是调用者给什么就存什么。CI 里传的是绝对路径，本地手跑常常传
        // `tests\StubTool\bin\...\StubTool.exe` 这种相对路径——它可以被 File.Exists
        // 认出来（当前目录对得上），但写进 ToolProfile.ExecutablePath 之后，
        // 启动用的是「程序目录」而不是 shell 的当前目录，于是同一个文件就找不到了：
        // 日志里报「系统找不到指定的文件」，探针把这一项判成 Failed，
        // 连带「端到端/父项成功」也红——看起来像代码回归，其实只是路径没定死。
        string rawStub = args.Length > 1
            ? args[1]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "StubTool", "bin", "Release", "net8.0", "StubTool.exe"));
        string stub = ResolveStub(rawStub);

        if (!File.Exists(stub) && mode is not ("help" or "clipdialog" or "ui" or "live"))
        {
            Console.WriteLine("STUB-NOT-FOUND " + stub);
            return 2;
        }

        var modes = new Dictionary<string, Func<int>>(StringComparer.Ordinal)
        {
            ["done"] = () => Run("done", stub, CompletionSignalKind.ProcessExit0, "--seconds 2 --exit 0", 5, 0),
            ["failed"] = () => Run("failed", stub, CompletionSignalKind.ProcessExit0, "--seconds 2 --exit 3", 5, 0),
            ["timeout"] = () => Run("timeout", stub, CompletionSignalKind.ProcessExit0, "--hang", 1, 0),
            ["halted"] = () => RunHalted(stub),
            ["signals"] = () => RunSignalArbiter(),
            ["wrapup"] = () => RunWrapUp(),
            ["m12fix"] = () => RunM12Fix(),
            ["haltedlog"] = () => Run("haltedlog", stub, CompletionSignalKind.LogPattern, "--hang", 5, 1),
            ["logpattern"] = () => RunLogPattern(stub),
            ["args"] = () => RunArgs(),
            ["migrate"] = () => Migrate(),
            ["clipboard"] = () => RunClipboard(stub),
            ["cliptry"] = () => RunClipTry(),
            ["clipdialog"] = () => RunClipDialog(),
            ["queue"] = () => RunQueue(stub, "queue"),
            ["queuefail"] = () => RunQueue(stub, "queuefail"),
            ["queueabort"] = () => RunQueue(stub, "queueabort"),
            ["queuehalted"] = () => RunQueueHalted(stub),
            ["bgisecondary"] = () => RunBgiSecondary(stub),
            ["anchor"] = () => RunAnchor(stub),
            ["timeoutanchor"] = () => RunTimeoutAnchor(stub),
            ["residual"] = () => RunResidual(stub),
            ["conflict"] = () => RunConflict(stub),
            ["afterrun"] = () => RunAfterRun(stub),
            ["template"] = () => RunTemplate(),
            ["bgiwrite"] = () => RunBgiWrite(),
            ["completion"] = () => RunCompletion(),
            ["presets"] = () => RunPresets(),
            ["drop"] = () => RunDrop(),
            ["update"] = () => RunUpdate(),
            ["backup"] = () => RunBackup(),
            ["preflight"] = () => RunPreflight(),
            ["recap"] = () => RunRecap(),
            ["decision"] = () => RunDecision(),
            ["baseline"] = () => RunBaseline(),
            ["timebank"] = () => RunTimeBank(),
            ["away"] = () => RunAway(),
            ["handover"] = () => RunHandover(),
            ["troubleshoot"] = () => RunTroubleshoot(),
            ["tidy"] = () => RunTidy(),
            ["suggest"] = () => RunSuggest(),
            ["idle"] = () => RunIdle(),
            ["discover"] = () => RunDiscover(stub),
            ["custom"] = () => RunCustom(stub),
            ["duration"] = () => RunDuration(stub),
            ["stall"] = () => RunStall(stub, false),
            ["stallkill"] = () => RunStall(stub, true),
            ["keepawake"] = () => RunKeepAwake(),
            ["screensaver"] = () => RunScreenSaver(),
            ["nightscreen"] = () => RunNightScreen(),
            ["icon"] = () => RunIcon(stub),
            ["gdiicons"] = () => RunGdiIcons(stub),
            ["weekplan"] = () => RunWeekPlan(stub),
            ["customsig-log"] = () => RunCustomSignal(stub, "log"),
            ["customsig-file"] = () => RunCustomSignal(stub, "file"),
            ["customsig-title"] = () => RunCustomSignal(stub, "title"),
            ["iconoverride"] = () => RunIconOverride(stub),
            ["morning"] = () => RunMorning(),
            ["schedule"] = () => RunSchedule(),
            ["resume"] = () => RunResume(stub),
            ["explain"] = () => RunExplain(),
            ["retry"] = () => RunRetry(),
            ["bundle"] = () => RunBundle(),
            ["weekly"] = () => RunWeekly(),
            ["api"] = () => RunApi(),
            ["hotkey"] = () => RunHotkey(),
            ["progress"] = () => RunProgress(),
            ["command"] = () => RunCommand(),
            ["live"] = () => RunLive(),
            ["logrules"] = () => RunLogRules(),
            ["favorites"] = () => RunFavorites(),
            ["dag"] = () => RunDag(stub),
            ["groups"] = () => RunGroups(),
            ["bgiupdate"] = () => RunBgiVersion(),
            ["harvest"] = () => RunHarvest(),
            ["mcp"] = () => RunMcp(),
            ["uirules"] = () => RunUiRules(),
            ["ui"] = () => RenderProbe.RunUi(args.Length > 1 ? args[1] : "all"),
            ["mem"] = () => MemoryProbe.Run(),
            ["trim"] = () => MemoryProbe.RunTrim(),
            ["toolgrid"] = () => RunToolGrid(),
            ["toolcatalog"] = () => RunToolCatalog(),
            ["pager"] = () => RunPager(),
            ["notice"] = () => RunNotice(),
        };

        // v1.45.3 / #15：金丝雀。拆分之后「漏注册一个组」不会报错，只会静默少跑——
        // 这一行把它变成硬失败：CI 点名的每一组都必须在表里。
        string[] missing = CiGroups.Where(g => !modes.ContainsKey(g)).ToArray();
        if (missing.Length > 0)
        {
            Console.WriteLine("CANARY-FAIL 组没注册：" + string.Join("、", missing));
            return 1;
        }

        if (!modes.TryGetValue(mode, out var entry)) return Help();
        int code = entry();

        // 跑完之后再核一遍：撤掉一个组的分支同样要在这里红。
        missing = CiGroups.Where(g => !modes.ContainsKey(g)).ToArray();
        bool countOk = modes.Count == ExpectedModeCount;
        Console.WriteLine($"CANARY groups={modes.Count}/{ExpectedModeCount} ci={CiGroups.Length} missing={missing.Length}");
        if (missing.Length > 0 || !countOk)
        {
            if (missing.Length > 0) Console.WriteLine("CANARY-FAIL 组没注册：" + string.Join("、", missing));
            if (!countOk) Console.WriteLine($"CANARY-FAIL 入口数变了：应 {ExpectedModeCount}，实际 {modes.Count}（加组就把常量一起改）");
            return 1;
        }
        return code;
    }

}
