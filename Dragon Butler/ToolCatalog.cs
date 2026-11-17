using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// v1.21：已知自动化软件的目录。
///
/// 存在的理由：以前只认 BetterGI / 三月七 / ok-ww 三个，用户装的是别的就无从下手。
/// AUTO-MAS 支持十来个脚本，我们不必全支持，但得让用户能看见「原来还有这些」并且一键填好。
///
/// 关于启动参数的可信度，这一条必须写清楚，因为它决定界面怎么标：
/// - <see cref="ArgConfidence.Documented"/>：在对方项目的文档或源码里见过，敢写成默认值。
/// - <see cref="ArgConfidence.Observed"/>：在我们自己或社区项目（比如 gacha-ops）的实战配置里见过，
///   但对方没写成文档，可能随版本变。界面标「见别人这么用」，不标「官方」。
/// - <see cref="ArgConfidence.Custom"/>：不知道，留给用户自己填。
///
/// 不做的事：不猜参数。猜错一个 `-e` 的语义，用户的游戏就被关掉了，而我们没法在界面上解释为什么。
/// 所以拿不准的一律 <see cref="ArgConfidence.Custom"/>，宁可让用户多打几个字。
/// </summary>
public enum ArgConfidence
{
    /// <summary>不知道，只能自己填。</summary>
    Custom = 0,

    /// <summary>在实战配置里见过，非官方文档。</summary>
    Observed = 1,

    /// <summary>对方文档或源码里明确写过。</summary>
    Documented = 2,
}

/// <summary>一条可选的启动参数。</summary>
public sealed class ArgChoice
{
    /// <summary>参数原文（可含空格与引号）。</summary>
    public string Value { get; set; } = "";

    /// <summary>给用户看的一句话。</summary>
    public string Hint { get; set; } = "";

    /// <summary>可信度。</summary>
    public ArgConfidence Confidence { get; set; } = ArgConfidence.Custom;
}

/// <summary>
/// 一个已知软件。这里只放「怎么找到它、怎么启动它、启动后长什么样」，
/// 不放用户的路径与选择（那些在 ToolProfile 里）。
/// </summary>
public sealed class KnownTool
{
    /// <summary>ToolId，与 ToolProfile.ToolId 对应。</summary>
    public string ToolId { get; set; } = "";

    /// <summary>显示名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>一眼能认的短名（网格格子里用，太长会换行）。</summary>
    public string ShortName { get; set; } = "";

    /// <summary>它代管哪个游戏，写人话。</summary>
    public string GameName { get; set; } = "";

    /// <summary>认这个工具时的 exe 文件名候选（小写，不含路径）。</summary>
    public string[] ExeNames { get; set; } = Array.Empty<string>();

    /// <summary>默认启动参数。</summary>
    public string DefaultArguments { get; set; } = "";

    /// <summary>工具自身进程名（不含 .exe）。</summary>
    public string ManagedProcessName { get; set; } = "";

    /// <summary>关联游戏进程名。</summary>
    public string[] GameProcessNames { get; set; } = Array.Empty<string>();

    /// <summary>可选参数清单（第一条通常是默认那条）。</summary>
    public List<ArgChoice> Args { get; set; } = new();

    /// <summary>默认超时（分钟）。</summary>
    public int MaxDurationMinutes { get; set; } = 120;

    /// <summary>这个工具的说明，一句话。</summary>
    public string Blurb { get; set; } = "";

    /// <summary>是内置三项之一（不允许删除）。</summary>
    public bool BuiltIn => ToolCatalog.IsBuiltIn(ToolId);

    /// <summary>参数下拉里显示的文字。</summary>
    public static string ArgText(ArgChoice? c)
    {
        if (c == null) return "";
        string mark = c.Confidence switch
        {
            ArgConfidence.Documented => "✓ ",
            ArgConfidence.Observed => "· ",
            _ => "",
        };
        string value = string.IsNullOrWhiteSpace(c.Value) ? "(不传参数)" : c.Value;
        return mark + value;
    }

    /// <summary>可信度的中文说法。</summary>
    public static string ConfidenceText(ArgConfidence c) => c switch
    {
        ArgConfidence.Documented => "这个参数在对方的文档里写着",
        ArgConfidence.Observed => "别人是这么用的，对方没写进文档，可能随版本变",
        _ => "这条要你自己确认",
    };
}

/// <summary>
/// 已知软件目录。
///
/// 加软件的判断标准（写在这里，免得以后随手加）：
/// ① 它确实在跑游戏或游戏相关的事；② 它有能双击启动的 exe；
/// ③ 我们知道怎么判断它跑完了，或者至少能靠进程退出判断。
/// 不满足 ③ 的也可以加，但要明确标出「只能靠进程退出」。
/// </summary>
public static class ToolCatalog
{
    private static readonly string[] BuiltIns = { "bettergi", "march7th", "ok-ww" };

    /// <summary>是否内置（内置的不允许删除，避免用户把 BGI 删了之后没法恢复）。</summary>
    public static bool IsBuiltIn(string? toolId)
        => BuiltIns.Any(b => string.Equals(b, toolId, StringComparison.OrdinalIgnoreCase));

    private static List<KnownTool>? _all;

    /// <summary>全部已知软件（每次返回同一份只读实例，调用方不要改）。</summary>
    public static IReadOnlyList<KnownTool> All
    {
        get { _all ??= CreateAll(); return _all; }
    }

    /// <summary>按 ToolId 找；找不到返回 null（不伪造一个空档案，调用方要自己处理）。</summary>
    public static KnownTool? Find(string? toolId)
        => string.IsNullOrWhiteSpace(toolId)
            ? null
            : All.FirstOrDefault(t => string.Equals(t.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    /// <summary>按 exe 文件名反查（只比文件名，不区分大小写）。认不出返回 null。</summary>
    public static KnownTool? FromExeName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string name = fileName.Trim();
        return All.FirstOrDefault(t => t.ExeNames.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>拿这个工具的建议默认参数；未知工具返回空串。</summary>
    public static string DefaultArgsOf(string? toolId) => Find(toolId)?.DefaultArguments ?? "";

    /// <summary>按目录里的 exe 列表猜是哪个工具；认不出返回 null。</summary>
    public static KnownTool? GuessFromFiles(IEnumerable<string>? exeFiles)
    {
        if (exeFiles == null) return null;
        foreach (string f in exeFiles)
        {
            var hit = FromExeName(System.IO.Path.GetFileName(f));
            if (hit != null) return hit;
        }
        return null;
    }

    private static List<KnownTool> CreateAll() => new()
    {
        new KnownTool
        {
            ToolId = "bettergi",
            DisplayName = "BetterGI",
            ShortName = "BetterGI",
            GameName = "原神",
            Blurb = "原神的一条龙、自动拾取、自动剧情都在里面，日常靠它。",
            ExeNames = new[] { "bettergi.exe" },
            DefaultArguments = "startOneDragon \"{组名}\"",
            ManagedProcessName = "BetterGI",
            GameProcessNames = new[] { "YuanShen", "GenshinImpact" },
            MaxDurationMinutes = 180,
            Args = new()
            {
                new ArgChoice { Value = "startOneDragon \"{组名}\"", Confidence = ArgConfidence.Documented, Hint = "跑指定的一条龙配置（{组名} 会换成你在工具设置里选的配置名）" },
                new ArgChoice { Value = "startGroups \"{组名}\"", Confidence = ArgConfidence.Observed, Hint = "只跑配置组，不走一条龙流程" },
                new ArgChoice { Value = "startOneDragon", Confidence = ArgConfidence.Observed, Hint = "不传配置名，用 BetterGI 里当前选中的那条" },
                new ArgChoice { Value = "", Confidence = ArgConfidence.Documented, Hint = "只把界面打开，什么都不跑" },
            },
        },
        new KnownTool
        {
            ToolId = "march7th",
            DisplayName = "三月七助手",
            ShortName = "三月七",
            GameName = "崩坏：星穹铁道",
            Blurb = "星铁的清体力、模拟宇宙、每日任务。",
            ExeNames = new[] { "march7thassistant.exe", "march7th launcher.exe", "march7th.exe" },
            DefaultArguments = "main -e",
            ManagedProcessName = "March7 Launcher",
            GameProcessNames = new[] { "StarRail" },
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "main -e", Confidence = ArgConfidence.Observed, Hint = "跑一遍主任务，跑完把游戏关掉" },
                new ArgChoice { Value = "main", Confidence = ArgConfidence.Observed, Hint = "跑一遍主任务，游戏留着" },
                new ArgChoice { Value = "", Confidence = ArgConfidence.Documented, Hint = "只打开启动器界面" },
            },
        },
        new KnownTool
        {
            ToolId = "ok-ww",
            DisplayName = "OK-WW",
            ShortName = "OK-WW",
            GameName = "鸣潮",
            Blurb = "鸣潮的日常、周本、刷声骸，基于 ok-script。",
            ExeNames = new[] { "ok-ww.exe", "okwu.exe" },
            DefaultArguments = "-t 1 -e",
            ManagedProcessName = "ok-ww",
            GameProcessNames = new[] { "Client-Win64-Shipping" },
            MaxDurationMinutes = 150,
            Args = new()
            {
                new ArgChoice { Value = "-t 1 -e", Confidence = ArgConfidence.Observed, Hint = "跑第 1 号任务，跑完退出程序" },
                new ArgChoice { Value = "-t 1", Confidence = ArgConfidence.Observed, Hint = "跑第 1 号任务，跑完不退出" },
                new ArgChoice { Value = "-e", Confidence = ArgConfidence.Observed, Hint = "用界面里当前选的任务，跑完退出" },
                new ArgChoice { Value = "", Confidence = ArgConfidence.Documented, Hint = "只打开图形界面" },
            },
        },
        new KnownTool
        {
            ToolId = "zzz-od",
            DisplayName = "绝区零一条龙",
            ShortName = "绝区零",
            GameName = "绝区零",
            Blurb = "ZZZ 的日常与空洞。AUTO-MAS 里的 ZzzOd 就是它。",
            ExeNames = new[] { "zzz-od.exe", "zzzod.exe", "zzz-one-dragon.exe" },
            DefaultArguments = "",
            ManagedProcessName = "zzz-od",
            GameProcessNames = new[] { "ZenlessZoneZero" },
            MaxDurationMinutes = 150,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "这个项目的命令行参数我们没查到可靠来源，先留空只启动界面；知道的话自己填" },
            },
        },
        new KnownTool
        {
            ToolId = "maaend",
            DisplayName = "MaaEnd",
            ShortName = "MaaEnd",
            GameName = "明日方舟：终末地",
            Blurb = "终末地的日常，基于 MaaFramework。",
            ExeNames = new[] { "maaend.exe" },
            DefaultArguments = "",
            ManagedProcessName = "MaaEnd",
            GameProcessNames = new[] { "Endfield" },
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "参数没查到可靠来源，先留空；MaaEnd 主要靠界面里配任务" },
            },
        },
        new KnownTool
        {
            ToolId = "ok-nte",
            DisplayName = "OK-NTE",
            ShortName = "OK-NTE",
            GameName = "无限暖暖",
            Blurb = "无限暖暖的日常，基于 ok-script。",
            ExeNames = new[] { "ok-nte.exe", "oknte.exe" },
            DefaultArguments = "",
            ManagedProcessName = "ok-nte",
            GameProcessNames = new[] { "InfinityNikki" },
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "参数没查到可靠来源；ok-script 系一般认 -t 任务号与 -e 跑完退出，可自己试" },
            },
        },
        new KnownTool
        {
            ToolId = "maa",
            DisplayName = "MAA",
            ShortName = "MAA",
            GameName = "明日方舟",
            Blurb = "明日方舟的刷图与基建。它有正式的命令行接口，但日常用界面更省事。",
            ExeNames = new[] { "maa.exe" },
            DefaultArguments = "",
            ManagedProcessName = "MAA",
            GameProcessNames = new[] { "arknights" },
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Documented, Hint = "日常用图形界面；命令行用法见 MAA 文档的 CLI 指南" },
            },
        },
        new KnownTool
        {
            ToolId = "whimbox",
            DisplayName = "奇想盒",
            ShortName = "奇想盒",
            GameName = "绝区零 / 多游戏",
            Blurb = "AUTO-MAS 里的 Whimbox，一盒子小工具。",
            ExeNames = new[] { "whimbox.exe" },
            DefaultArguments = "",
            ManagedProcessName = "Whimbox",
            GameProcessNames = Array.Empty<string>(),
            MaxDurationMinutes = 90,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "参数没查到可靠来源，先留空" },
            },
        },

        // ===== v1.22：补齐 AUTO-MAS 托管里出现、但这里还没有的那些 =====
        // 准则没变：exe 名与游戏名有依据才写，命令行参数查不到就留空并标「自己确认」。
        // 一个个问「点什么」比一次给齐更难用，所以宁可口径宽、候选多，也不要在引导页
        // 只列三个把人挡在外面。
        new KnownTool
        {
            ToolId = "baah",
            DisplayName = "BAAH",
            ShortName = "BAAH",
            GameName = "蔚蓝档案",
            Blurb = "蔚蓝档案的日常与总力战。AUTO-MAS 里的 BAAH 就是它。",
            ExeNames = new[] { "baah.exe" },
            DefaultArguments = "",
            ManagedProcessName = "BAAH",
            GameProcessNames = new[] { "BlueArchive" },
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "参数没查到可靠来源，先留空只启动界面；知道的话自己填" },
            },
        },
        new KnownTool
        {
            ToolId = "hsr",
            DisplayName = "HSR",
            ShortName = "HSR",
            GameName = "崩坏：星穹铁道",
            Blurb = "AUTO-MAS 自带的星铁脚本。如果你用的是三月七助手，选那个就行。",
            ExeNames = new[] { "hsr.exe" },
            DefaultArguments = "",
            ManagedProcessName = "HSR",
            GameProcessNames = new[] { "StarRail" },
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "参数没查到可靠来源，先留空只启动界面" },
            },
        },
        new KnownTool
        {
            ToolId = "m9a",
            DisplayName = "M9A",
            ShortName = "M9A",
            GameName = "重返未来：1999",
            Blurb = "重返未来 1999 的自动代理，基于 MaaFramework。",
            ExeNames = new[] { "m9a.exe" },
            DefaultArguments = "",
            ManagedProcessName = "M9A",
            GameProcessNames = new[] { "Reverse1999" },
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "参数没查到可靠来源，先留空；M9A 主要在界面里配任务" },
            },
        },
        new KnownTool
        {
            ToolId = "mss",
            DisplayName = "MSS",
            ShortName = "MSS",
            GameName = "星塔旅人",
            Blurb = "星塔旅人的自动代理（MaaStellaSora）。",
            ExeNames = new[] { "mss.exe" },
            DefaultArguments = "",
            ManagedProcessName = "MSS",
            GameProcessNames = new[] { "StellaSora" },
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "参数没查到可靠来源，先留空" },
            },
        },
        new KnownTool
        {
            ToolId = "src",
            DisplayName = "SRC",
            ShortName = "SRC",
            GameName = "崩坏：星穹铁道",
            Blurb = "StarRailCopilot，按日程自动排星铁。主程序固定叫 src.exe。",
            ExeNames = new[] { "src.exe" },
            DefaultArguments = "",
            ManagedProcessName = "src",
            GameProcessNames = new[] { "StarRail" },
            MaxDurationMinutes = 150,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "参数没查到可靠来源，先留空只启动界面" },
            },
        },
        new KnownTool
        {
            ToolId = "maafw",
            DisplayName = "MFW 项目",
            ShortName = "MFW",
            GameName = "任意 MaaFramework 项目",
            Blurb = "任意一个装了 interface.json 的 MaaFramework 项目目录，自己指定主程序。",
            ExeNames = new[] { "maafw.exe" },
            DefaultArguments = "",
            ManagedProcessName = "MFW",
            GameProcessNames = Array.Empty<string>(),
            MaxDurationMinutes = 150,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "这类项目没有统一入口，主程序和参数都按你手上的那个填" },
            },
        },
        new KnownTool
        {
            ToolId = "general",
            DisplayName = "通用脚本",
            ShortName = "通用",
            GameName = "任意",
            Blurb = "什么都行：填个 exe 加参数，就当成队列里的一项来跑。",
            ExeNames = new[] { "general.exe" },
            DefaultArguments = "",
            ManagedProcessName = "",
            GameProcessNames = Array.Empty<string>(),
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Custom, Hint = "参数由你自己定，程序会把它们原样传给那个 exe" },
            },
        },
        new KnownTool
        {
            ToolId = "hsr-march7th",
            DisplayName = "三月七（命令行）",
            ShortName = "三月七 CLI",
            GameName = "崩坏：星穹铁道",
            Blurb = "三月七助手另有免管理员权限的命令行入口 m7a.exe，适合挂在无人值守里。",
            ExeNames = new[] { "m7a.exe" },
            DefaultArguments = "",
            ManagedProcessName = "m7a",
            GameProcessNames = new[] { "StarRail" },
            MaxDurationMinutes = 120,
            Args = new()
            {
                new ArgChoice { Value = "", Confidence = ArgConfidence.Documented, Hint = "官方 README 写明 m7a.exe 是免管理员权限的命令行入口" },
            },
        },
    };

    /// <summary>把已知工具变成一份可用的 ToolProfile（用户点「添加」时用）。</summary>
    public static ToolProfile ToProfile(KnownTool tool)
    {
        var p = new ToolProfile
        {
            ToolId = tool.ToolId,
            DisplayName = tool.DisplayName,
            LaunchArguments = tool.DefaultArguments,
            ManagedProcessName = tool.ManagedProcessName,
            GameProcessNames = (string[])tool.GameProcessNames.Clone(),
            PrimarySignal = CompletionSignalKind.ProcessExit0,
            SecondarySignals = tool.GameProcessNames.Length > 0
                ? new[] { CompletionSignalKind.GameProcessExit }
                : Array.Empty<CompletionSignalKind>(),
            LogCompletionPattern = string.Equals(tool.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase)
                ? "一条龙和配置组任务结束"
                : null,
            MaxDurationMinutes = tool.MaxDurationMinutes > 0 ? tool.MaxDurationMinutes : 120,
        };
        if (string.Equals(tool.ToolId, "bettergi", StringComparison.OrdinalIgnoreCase))
        {
            // BGI 的完成判定比别的工具复杂（日志锚点 + 三路互证），照内置默认值抄一份，
            // 免得用户加回来的 BGI 变成「只能靠进程退出」的降级版。
            var bgi = ToolProfileDefaults.CreateAll().First(x => x.ToolId == "bettergi");
            p.PrimarySignal = bgi.PrimarySignal;
            p.SecondarySignals = (CompletionSignalKind[])bgi.SecondarySignals.Clone();
            p.PostExitGraceMinutes = bgi.PostExitGraceMinutes;
            foreach (var kv in bgi.CustomData) p.CustomData[kv.Key] = kv.Value;
        }
        return p;
    }
}
