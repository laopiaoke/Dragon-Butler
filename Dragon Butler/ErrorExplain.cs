using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 把异常翻成人话（v1.28）。
///
/// <para>
/// 崩溃转储里写的是 <c>System.UnauthorizedAccessException: 对路径"C:\Program Files\..."的访问被拒绝。
///   在 System.IO.__Error.WinIOError(Int32 errorCode, String maybeFullPath)</c>——
/// 这对写代码的人有用，对坐在电脑前想看「今晚为什么没跑」的人等于什么都没说。
/// </para>
/// <para>
/// 这里只做一件事：给出「刚才发生了什么 / 大概是因为什么 / 你可以怎么做」三句话。
/// 判断依据是异常类型 + 消息里的关键词，都写在规则表里，能改能加，不联网不猜。
/// 翻不出来的时候给一段通用的、诚实的说明，而不是硬套一个错的结论。
/// </para>
/// </summary>
public static class ErrorExplain
{
    /// <summary>一条翻译结果：一句话结论 + 可能的原因 + 能做的事。</summary>
    public sealed record Explained(string What, string Why, string Todo)
    {
        /// <summary>三句话拼成一段，界面上直接显示这个。</summary>
        public string Text =>
            string.IsNullOrWhiteSpace(Why) ? What : What + "\n" + Why + (string.IsNullOrWhiteSpace(Todo) ? "" : "\n" + Todo);
    }

    private sealed record Rule(
        string Match,          // 匹配异常类型全名或消息里的关键词（不区分大小写）
        string What,
        string Why,
        string Todo,
        int Weight);           // 数字越大越优先：类型名比关键词更可信

    /// <summary>
    /// 规则表。顺序不重要，靠 Weight 决定谁赢：
    /// 类型名（10+）永远压过关键词（1–5），否则「权限」这两个字会把所有异常都吸走。
    /// </summary>
    private static readonly Rule[] Rules =
    {
        new("UnauthorizedAccessException",
            "有个文件没写成，系统说没权限。",
            "程序想去动一个它够不着的地方——常见的是 C:\\Program Files 下面，那里要管理员权限才能改。",
            "把管家挪到桌面或 D 盘这种普通目录再试一次；或者右键程序选「以管理员身份运行」。",
            12),
        new("DirectoryNotFoundException",
            "有个文件夹不见了。",
            "要读写的目录被删掉或改名了——通常是把管家挪了位置、或者清理软件顺手删了它旁边的目录。",
            "把管家整个文件夹放回原处（别只挪 exe），或者重新选一次日志/配置目录。",
            11),
        new("FileNotFoundException",
            "有个文件找不到了。",
            "它要启动或读取的那个文件不在原来的位置——最常见的是工具的 exe 被更新、改名或者卸载了。",
            "去「设置 → 托管配置」里点开那个工具，重新选一次程序位置。",
            11),
        new("IOException",
            "读写文件的时候断掉了。",
            "文件被别的程序占着（比如日志正被记事本打开），或者磁盘满了、断线了。",
            "关掉可能占着这个文件的程序再试；顺手看一眼磁盘还剩多少空间。",
            9),
        new("Win32Exception",
            "系统拒绝了这次操作。",
            "要么程序需要管理员权限，要么它要启动的那个 exe 已经被删除或损坏。",
            "先确认那个程序自己能正常打开；能打开的话，再用管理员身份运行管家。",
            9),
        new("System.ComponentModel.Win32Exception",
            "系统拒绝了这次操作。",
            "要么程序需要管理员权限，要么它要启动的那个 exe 已经被删除或损坏。",
            "先确认那个程序自己能正常打开；能打开的话，再用管理员身份运行管家。",
            10),
        new("TimeoutException",
            "等太久，放弃了。",
            "对方一直没回应——可能是游戏卡在加载、网络断了，或者那个工具自己挂了但进程还在。",
            "去「运行」页看看卡在哪个工具，把它单独跑一次试试。",
            8),
        new("SocketException",
            "网络没连上。",
            "端口被占用、被防火墙拦了，或者本机根本没联网。",
            "换一个端口（设置 → 高级 → 事件推送），或者先确认这台机器能上网。",
            8),
        new("HttpRequestException",
            "发出去的消息没送到。",
            "通知渠道的地址填错了、机器人被移出群了，或者对方服务暂时不可用。",
            "去「设置 → 通知」用「发一条试试」验一下这个渠道。",
            8),
        new("JsonException",
            "有个配置文件读不懂。",
            "文件被手工改过、少了个括号，或者被别的版本写成了另一个格式。",
            "把那个文件改名备份，让管家重新生成一份；改过的话对照原始格式检查一遍。",
            10),
        new("NullReferenceException",
            "程序内部踩空了。",
            "这是管家自己的问题，不是你配错了——某个该有东西的地方是空的。",
            "把这条消息连同日志目录一起反馈给作者；重开一次通常能绕过。",
            7),
        new("InvalidOperationException",
            "当前状态下做不了这件事。",
            "多半是两件事撞在一起了——比如队列正在跑的时候又点了开始。",
            "等当前这一轮结束再操作；如果一直这样，重开一次管家。",
            6),
        new("OutOfMemoryException",
            "内存不够了。",
            "同时开着的东西太多，或者某个工具吃掉了大量内存。",
            "关掉一些不用的程序；长期如此的话，去设置里打开「收进托盘时销毁界面」。",
            8),
        new("ArgumentException",
            "传进去的参数不对。",
            "某个路径或参数是空的、或者格式不是它期待的样子。",
            "检查那个工具的参数（设置 → 托管配置 → 点开那个工具），清空重填一次。",
            5),

        // —— 关键词：类型说不清的时候，靠消息里的字面线索 ——
        new("访问被拒绝",
            "有个文件没写成，系统说没权限。",
            "程序想去动一个它够不着的地方，或者那个文件正被别的程序占着。",
            "换一个普通目录，或者用管理员身份运行一次。",
            6),
        new("未能找到文件",
            "有个文件找不到了。",
            "要启动的程序不在原来的位置——工具更新或卸载之后路径就失效了。",
            "去「设置 → 托管配置」里重新选一次那个工具的程序位置。",
            6),
        new("拒绝访问",
            "有个文件没写成，系统说没权限。",
            "程序想去动一个它够不着的地方，或者那个文件正被别的程序占着。",
            "换一个普通目录，或者用管理员身份运行一次。",
            6),
        new("端口",
            "网络端口被占用了。",
            "另一个程序（或者管家的另一个实例）已经在用这个端口。",
            "在设置里换一个端口，或者先关掉占着它的程序。",
            4),
        new("bettergi",
            "BetterGI 那边出了问题。",
            "它没能正常启动、中途退出，或者配置文件的格式和管家认识的不一样。",
            "先单独打开 BetterGI 确认它自己能用，再回来看「设置 → 托管配置」里的路径对不对。",
            4),
        new("超时",
            "等太久，放弃了。",
            "那个工具一直没给出「我跑完了」的信号——可能卡在加载，也可能自己挂了。",
            "去「运行」页看卡在哪个工具，把它单独跑一次。",
            4),
        new("磁盘",
            "写不进去了。",
            "磁盘满了，或者那个盘掉线了。",
            "清一点空间再试。",
            4),
        new("网络",
            "网络没连上。",
            "断网了，或者被防火墙拦了。",
            "确认这台机器能上网，再看一眼通知渠道的地址。",
            3),
    };

    /// <summary>兜底：翻不出来时说的话。宁可说「我不知道」，也不硬套一个错的结论。</summary>
    private static readonly Explained Unknown = new(
        "出了个没见过的错。",
        "这个错误的特征不在已知清单里，所以没法替你判断原因——硬猜一个只会把你带偏。",
        "日志目录里有完整的记录，把它发给作者能更快定位。");

    /// <summary>
    /// 翻译一段异常文本（可以是 <c>Exception.ToString()</c> 的完整堆栈）。
    /// 传 null 或空串返回兜底说明。
    /// </summary>
    public static Explained Explain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Unknown;

        string text = raw;
        Rule? best = null;
        foreach (var rule in Rules)
        {
            if (text.IndexOf(rule.Match, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (best == null || rule.Weight > best.Weight) best = rule;
        }

        return best == null ? Unknown : new Explained(best.What, best.Why, best.Todo);
    }

    /// <summary>
    /// 从一段堆栈里挑出最像「根因」的那一行（异常类型 + 消息），给日志列表当标题用。
    /// 挑不到就返回第一行非空内容。
    /// </summary>
    public static string Headline(string? raw, int max = 90)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "（没有留下异常内容）";

        var lines = raw.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return "（没有留下异常内容）";

        // 「异常：System.XxxException: 消息」这种行最好用；其次是任何带 Exception 的行
        string pick = lines.FirstOrDefault(l => l.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                                                && !l.StartsWith("在 ", StringComparison.Ordinal))
                      ?? lines.FirstOrDefault(l => l.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                      ?? lines[0];

        // 去掉「异常：」这类前缀，也去掉命名空间，只留类型名 + 消息
        int colon = pick.IndexOf('：');
        if (colon >= 0 && colon <= 4) pick = pick.Substring(colon + 1).Trim();
        pick = pick.Replace("System.", "").Replace("Exception:", "：");

        return pick.Length <= max ? pick : pick.Substring(0, max).TrimEnd() + "…";
    }

    /// <summary>
    /// 给崩溃分类，决定列表里显示什么颜色：
    /// 「管家自己踩空」和「你的环境不对」是两件不同的事，用户该做的事也不一样。
    /// </summary>
    public static bool IsOurFault(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Contains("NullReferenceException", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("InvalidOperationException", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("IndexOutOfRangeException", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("ArgumentOutOfRangeException", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("KeyNotFoundException", StringComparison.OrdinalIgnoreCase);
    }
}
