using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>自动决定的来源。<see cref="Describe"/> 用它决定说「谁」做的。</summary>
public enum DecisionSource
{
    /// <summary>队列自己（依赖、前置检查、失败策略、间隔）。</summary>
    Queue,
    /// <summary>守护（崩溃重启、挂起、内存、会话）。</summary>
    Sentinel,
    /// <summary>定时（计划点、补跑、度假、空闲等待）。</summary>
    Schedule,
    /// <summary>日志规则（关键词触发的动作）。</summary>
    Rule,
    /// <summary>首屏/其他一次性判断。</summary>
    System,
}

/// <summary>
/// 一条自动决定。**Why 是给人看的一句话**，不是日志行——
/// 日志行写「队列：X 依赖未满足（等 Y）」，这里写「X 在等 Y，Y 上次没跑成」。
/// </summary>
public sealed record Decision(DateTime At, DecisionSource Source, string Title, string Why, string? Detail = null);

/// <summary>
/// 自动决定台账（v1.20 / 奇思妙想 #4）：每个自动决定留一句「为什么」。
///
/// <para>
/// <b>为什么要有这个</b>：程序替用户做决定的地方已经很多（跳过一项、停下队列、
/// 自动重启、推迟到空闲、不去补跑），但理由只写在日志里，而日志是给排查问题的人看的，
/// 不是给「刚睡醒想问一句到底怎么回事」的人看的。用户看不见理由，就只能猜；
/// 猜错一次，下次就不敢让它自己跑了。
/// </para>
/// <para>
/// <b>三条边界</b>：
/// ① <b>只在内存里</b>——不落盘、不上传、不跨进程。它是「刚才发生了什么」，
///    不是审计日志；要长期留痕的走 <see cref="LogService"/>。
/// ② <b>有上限</b>——默认 200 条环形缓冲。理由：这是给人扫一眼的清单，
///    给两千条等于没给；翻旧账请用「今日」和日志。
/// ③ <b>只记「本来可以不这样」的决定</b>——正常跑完不记。清单里塞满「正常开始/正常结束」，
///    真正需要解释的那三条就被淹了。
/// </para>
/// <para>纯静态 + 纯函数格式化，可断言；时间由调用方传，方便测试。</para>
/// </summary>
public static class DecisionLog
{
    /// <summary>环形缓冲上限。见上面第②条。</summary>
    public const int Capacity = 200;

    private static readonly object Gate = new();
    private static readonly LinkedList<Decision> Items = new();

    /// <summary>记一条。重复调用同一件事（比如每秒轮询）由调用方自己节流。</summary>
    public static void Add(DecisionSource source, string? title, string? why, string? detail = null)
        => AddAt(DateTime.Now, source, title, why, detail);

    /// <summary>同上，时刻由调用方给（测试用）。</summary>
    public static void AddAt(DateTime at, DecisionSource source, string? title, string? why, string? detail = null)
    {
        title = (title ?? "").Trim();
        why = (why ?? "").Trim();
        if (title.Length == 0 && why.Length == 0) return;

        lock (Gate)
        {
            Items.AddLast(new Decision(at, source, title, why, detail));
            while (Items.Count > Capacity) Items.RemoveFirst();
        }
    }

    /// <summary>最近的若干条，最新的在前。n &lt;= 0 返回空。</summary>
    public static List<Decision> Recent(int n)
    {
        if (n <= 0) return new List<Decision>();
        lock (Gate)
        {
            return Items.Reverse().Take(n).ToList();
        }
    }

    /// <summary>今天的（按传入的「今天」判定，便于测试）。最新的在前。</summary>
    public static List<Decision> Today(DateTime now)
    {
        lock (Gate)
        {
            return Items.Where(d => d.At.Date == now.Date).Reverse().ToList();
        }
    }

    /// <summary>一共记了多少条（不超过 <see cref="Capacity"/>）。</summary>
    public static int Count
    {
        get { lock (Gate) { return Items.Count; } }
    }

    /// <summary>清空。换天时调一次即可，不必强制。</summary>
    public static void Clear()
    {
        lock (Gate) { Items.Clear(); }
    }

    /// <summary>「队列」「守护」这种，用在窗口里做来源标签。</summary>
    public static string SourceName(DecisionSource source) => source switch
    {
        DecisionSource.Queue => "队列",
        DecisionSource.Sentinel => "守护",
        DecisionSource.Schedule => "定时",
        DecisionSource.Rule => "规则",
        DecisionSource.System => "程序",
        _ => "程序",
    };

    /// <summary>一行完整的话：<c>「队列 · 跳过三月七助手 —— 它依赖的 BetterGI 这次没跑成」</c>。</summary>
    public static string Describe(Decision? d)
    {
        if (d == null) return "";
        string head = SourceName(d.Source);
        string title = d.Title.Length > 0 ? d.Title : "做了个决定";
        string why = d.Why.Length > 0 ? d.Why : "没留下理由";
        return $"{head} · {title} —— {why}";
    }

    /// <summary>
    /// 给「今日」/主卡用的一段总结。没有记录时返回空串——
    /// 界面据此决定整块不出现，而不是显示「暂无数据」占一行。
    /// </summary>
    public static string Summary(DateTime now, int maxLines = 3)
    {
        var list = Today(now);
        if (list.Count == 0) return "";
        var lines = list.Take(Math.Max(1, maxLines)).Select(Describe);
        return string.Join("\n", lines);
    }
}
