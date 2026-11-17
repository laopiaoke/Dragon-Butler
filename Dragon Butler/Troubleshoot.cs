using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 它会在你还没问的时候就知道你要问什么（v1.20 / 奇思妙想 #9）。
///
/// <para>
/// <b>为什么不需要 AI</b>：一类软件一年只会出十几种毛病，而这十几种是<b>可穷举的</b>。
/// 用户看到「退出码 3221225477」时想问的永远是同一个问题：「这是什么、要紧吗、我现在该干什么」。
/// 三次都问同一句话，就该把答案预先写在那儿。
/// </para>
/// <para>
/// <b>为什么把「我不确定」也写进表里</b>：猜错比不猜更糟——用户按错的建议操作一次，
/// 下次就不信这个提示了。所以表里每条都只有「能站得住的结论」，认不出的老实说认不出。
/// </para>
/// <para>纯函数：只做「症状 → 解释」的映射，不读磁盘、不看日志文件、不联网。</para>
/// </summary>
public static class Troubleshoot
{
    /// <summary>一条可解释的症状。<see cref="Key"/> 稳定供断言；<see cref="Action"/> 是可点的命令 id。</summary>
    public sealed record Symptom(string Key, string What, string Why, string Todo, string? Action = null);

    /// <summary>
    /// 已知的退出码。值用<b>无符号十进制</b>存，注释里写十六进制——
    /// 用户从弹窗里看到的是十进制，而 Process.ExitCode 给的是补码负数，
    /// 两边按 uint 归一才不会对不上。
    /// </summary>
    private static readonly Dictionary<long, Symptom> ExitCodes = new()
    {
        // 0xC0000005 = 3221225477
        [3221225477] = new("exit.access-violation",
            "BetterGI 崩了，报的是「访问冲突」。",
            "多半是原工具自己的 bug 或某个脚本越界读写内存，和管家这边无关。",
            "先看原工具的日志和 Windows 事件查看器里的应用程序日志。这个代码单看定不了原因。",
            "view.log"),
        // 0xC0000135 = 3221225781
        [3221225781] = new("exit.dll-missing",
            "BetterGI 起不来，缺运行库。",
            "机器上没装它需要的 .NET 桌面运行时，或者装了但版本对不上。",
            "去原工具的发布页装一遍它要求的运行时，再点「测试启动」看还报不报。",
            "view.tools"),
        // 0xE0434352 = 3762504530
        [3762504530] = new("exit.clr-exception",
            "BetterGI 内部抛了个没人接的异常。",
            "原工具自己出了问题，通常是某个配置项或脚本让它走到了没处理的分支。",
            "把当时用的配置组换一个试试；能稳定复现就把原工具的日志发给它作者。",
            "view.groups"),
        [1] = new("exit.code-1",
            "程序返回了 1，这是「我自己主动退出，但没成功」的通用说法。",
            "原因要看日志最后几行，光凭退出码分不出来。",
            "打开原工具日志看结尾那几十行，或者去「打开日志」看这一次的完整记录。",
            "view.log"),
    };

    /// <summary>日志里的已知说法。判据是「包含」，顺序即优先级——越具体的排越前。</summary>
    private static readonly (string Needle, Symptom Hit)[] LogPhrases =
    {
        ("任务启动失败", new Symptom("log.start-failed",
            "BGI 自己说任务没能启动。",
            "通常是游戏窗口没找到、或者上一次的任务还没退干净。",
            "先手动开一次 BetterGI 看它能不能正常跑，再回来重试。",
            "view.tools")),
        ("树脂耗尽", new Symptom("log.resin-out",
            "树脂用完了，BGI 因此结束了这一轮。",
            "这不是故障，是正常收尾——原工具发现没事可做就停了。",
            "不用处理。想让它继续跑别的任务，把那个任务勾在配置组里。",
            null)),
        ("AccessViolation", new Symptom("log.access-violation",
            "日志里出现了「访问冲突」，BetterGI 崩过一次。",
            "原工具内部内存越界，和管家无关。",
            "看看是不是某个特定配置组才崩；是的话避开它，并把日志发给原工具作者。",
            "view.log")),
        ("未能找到", new Symptom("log.not-found",
            "某个东西没找到——可能是窗口、可能是文件。",
            "最常见的是游戏没在前台，或者脚本目录被挪过位置。",
            "确认游戏开着且没最小化，再确认 BetterGI 目录没被移动。",
            "view.tools")),
        ("连接", new Symptom("log.network",
            "有一次网络请求没成功。",
            "米游社接口、更新源或者代理不通都长这样。",
            "偶尔一次不用管——下一项会接着跑；一晚好几次就检查一下网络。",
            null)),
    };

    /// <summary>
    /// 按退出码解释。<paramref name="code"/> 为 null 或没收录时返回 null（不猜）。
    /// </summary>
    public static Symptom? FromExitCode(int? code)
    {
        if (code == null) return null;
        long key = unchecked((uint)code.Value);
        return ExitCodes.TryGetValue(key, out var s) ? s : null;
    }

    /// <summary>
    /// 按日志行解释。没命中返回 null——<b>宁可不说，也不说一句放之四海皆准的废话</b>。
    /// </summary>
    public static Symptom? FromLogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        foreach (var (needle, hit) in LogPhrases)
        {
            if (line.Contains(needle, StringComparison.OrdinalIgnoreCase)) return hit;
        }
        return null;
    }

    /// <summary>
    /// 综合判断：先看退出码（更确定），再看日志行。都没有就给一条「我不知道」的诚实条目，
    /// 而不是空手而归——用户至少要知道「这条信息我们也没看懂」。
    /// </summary>
    public static Symptom Explain(int? exitCode, string? lastErrorLine)
    {
        var byCode = FromExitCode(exitCode);
        if (byCode != null) return byCode;

        var byLine = FromLogLine(lastErrorLine);
        if (byLine != null) return byLine;

        if (exitCode != null)
        {
            // 负的退出码在界面上要显示成无符号十进制：用户在原工具和网上看到的
            // 都是 3221225477 这一套，写 -1073741819 只会让人更糊涂。
            long shown = unchecked((uint)exitCode.Value);
            return new Symptom("exit.unknown",
                $"程序返回了 {shown}，这个代码我们没收录。",
                "没有对应解释，硬猜容易误导你。",
                "打开原工具的日志看最后几十行，通常原因就写在退出前。",
                "view.log");
        }

        return new Symptom("unknown",
            "没留下足够的信息判断原因。",
            "这一项没有退出码，日志里也没有能认出来的句子。",
            "去「看今天」看这一项的完整记录，或者直接重跑一次——偶发的情况重跑就好了。",
            "view.today");
    }

    /// <summary>一句话概括，给通知正文用（不换行、不超过 60 字）。</summary>
    public static string Brief(Symptom? s)
    {
        if (s == null) return "";
        string text = s.What + " " + s.Todo;
        return text.Length <= 60 ? text : text.Substring(0, 59) + "…";
    }

    /// <summary>收录了多少种已知说法（探针用它防止表被清空）。</summary>
    public static int KnownCount => ExitCodes.Count + LogPhrases.Length;

    /// <summary>
    /// 从「原因」文本里捞退出码。台账存的是人话（「退出码 3221225477」），
    /// 没有单独字段——不改存储格式，只在这里读回来。
    /// </summary>
    public static int? ParseExitCode(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        int at = reason.IndexOf("退出码", StringComparison.Ordinal);
        if (at < 0) return null;
        int i = at + 3;
        while (i < reason.Length && reason[i] == ' ') i++;
        int start = i;
        while (i < reason.Length && char.IsDigit(reason[i])) i++;
        if (i == start) return null;
        string digits = reason.Substring(start, i - start);
        // 超过 int.MaxValue 的（如 3762504530）按 uint 折成补码，和 FromExitCode 的归一方式一致。
        if (long.TryParse(digits, out long big))
            return big <= int.MaxValue ? (int)big : unchecked((int)(uint)big);
        return null;
    }
}
