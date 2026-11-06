using System;

namespace DragonButler;

/// <summary>
/// 任务栏进度与托盘文案（v1.8 / F10）。
///
/// 一个在游戏里全屏挂着的人，看不到主界面。任务栏图标上的那条进度
/// 是 Windows 自带的、零成本的可视反馈：不用切窗口就知道「跑到第几个了」。
///
/// 这里全部是纯函数，Probe 直接断言文案与比例，
/// 不去碰真实的 TaskbarItemInfo（那需要桌面会话）。
/// </summary>
public static class QueueProgress
{
    /// <summary>任务栏进度条的比例，0..1。总数非法时返回 0。</summary>
    public static double Fraction(int done, int total)
    {
        if (total <= 0) return 0;
        if (done <= 0) return 0;
        if (done >= total) return 1;
        return (double)done / total;
    }

    /// <summary>
    /// 一行字：`3/5 · BetterGI`。total 为 0 时给「准备中」，
    /// toolName 为空时只给计数（避免出现 "3/5 · " 这种尾巴）。
    /// </summary>
    public static string Describe(int done, int total, string? toolName)
    {
        if (total <= 0) return "准备中";
        int d = Math.Clamp(done, 0, total);
        string head = $"{d}/{total}";
        return string.IsNullOrWhiteSpace(toolName) ? head : head + " · " + toolName!.Trim();
    }

    /// <summary>托盘悬停文字：Windows 托盘 tooltip 上限 63 字，这里留足余量。</summary>
    public static string TrayText(bool running, int done, int total, string? toolName, string? idleText = null)
    {
        if (!running) return string.IsNullOrWhiteSpace(idleText) ? "一条龙管家" : idleText!.Trim();
        string body = Describe(done, total, toolName);
        string text = "一条龙管家 · " + body;
        return text.Length <= 63 ? text : text.Substring(0, 62) + "…";
    }
}
