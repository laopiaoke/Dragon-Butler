using System;

namespace DragonButler;

/// <summary>
/// 一次运行里的「现场证据」收集处（v1.6 / F4）。
///
/// 为什么需要它：<see cref="RunCoordinator"/> 只知道状态迁移，<see cref="LogWatcher"/> 只知道日志行，
/// 而通知要回答的是「为什么失败」。这三件事以前各说各话，通知里就只剩一句
/// 「详情见软件日志」——人不在电脑前时等于没收到。这里把运行期间零散的证据
/// （最后一条错误日志行、退出码、锚点是否命中）攒在一起，收尾时拼成一句话。
///
/// 线程模型：写入来自信号源的定时器线程，读取在队列线程；字段都是单个引用/值类型，
/// 用 volatile 语义（lock 太重，且这里丢一两行不影响结论）。
/// </summary>
public static class RunEvidence
{
    /// <summary>单行证据最多留这么多字——通知正文是要在手机通知栏里看的。</summary>
    public const int MaxLineChars = 120;

    private static string _lastErrorLine = "";
    private static string _lastAnchorLine = "";

    /// <summary>本次运行最后一条命中错误关键字的日志原文（没有则为空）。</summary>
    public static string LastErrorLine => _lastErrorLine;

    /// <summary>本次运行命中的完成锚点原文（没有则为空）。</summary>
    public static string LastAnchorLine => _lastAnchorLine;

    /// <summary>每次 RunOnceAsync 开始前清空，避免上一轮的错误串到这一轮。</summary>
    public static void Reset()
    {
        _lastErrorLine = "";
        _lastAnchorLine = "";
    }

    /// <summary>记一条错误日志行。</summary>
    public static void NoteErrorLine(string? line)
    {
        string t = Compact(line);
        if (t.Length > 0) _lastErrorLine = t;
    }

    /// <summary>记一条完成锚点原文。</summary>
    public static void NoteAnchorLine(string? line)
    {
        string t = Compact(line);
        if (t.Length > 0) _lastAnchorLine = t;
    }

    /// <summary>压成一行并截断：去掉换行与控制字符，超长保留尾部（尾部通常才是结论）。</summary>
    public static string Compact(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");
        if (s.Length <= MaxLineChars) return s;
        return "…" + s[^MaxLineChars..];
    }

    /// <summary>
    /// 拼「为什么没成」——通知正文用的那一句。
    /// 传入的 reason 已经是状态机/队列给出的人话，这里只负责补上可核对的细节。
    /// </summary>
    /// <param name="reason">状态机给出的原因（如「超过硬顶 120 分钟」）。</param>
    /// <param name="exitCode">进程退出码；没退出或取不到给 null。</param>
    /// <param name="errorLine">本次最后一条错误日志行；没有给空。</param>
    public static string Describe(string reason, int? exitCode, string? errorLine)
    {
        string head = string.IsNullOrWhiteSpace(reason) ? "未完成" : reason.Trim();
        var parts = new System.Collections.Generic.List<string> { head };

        if (exitCode is int code) parts.Add(code == 0 ? "退出码 0" : $"退出码 {code}");

        string line = Compact(errorLine);
        if (line.Length > 0) parts.Add("最后一条错误：" + line);

        return string.Join("；", parts);
    }
}
