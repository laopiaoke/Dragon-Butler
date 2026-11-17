using System;
using System.Collections.Generic;

namespace DragonButler;

/// <summary>
/// 它知道你什么时候不在（v1.20 / 奇思妙想 #2）。
///
/// <para>
/// <b>为什么不是定时</b>：定时回答的是「几点」，用户真正想问的是「我走了它会不会跑」。
/// 定 23:00 意味着你 22:30 出门就白等半小时，00:30 回来就正好撞上它跑到一半。
/// 而「你走了才跑」这句话，本身就包含了定时想表达的全部意图。
/// </para>
/// <para>
/// <b>为什么要连着看很多次才算</b>：看视频时键鼠可以半小时不动，读长文档也是。
/// 单次「闲置 20 分钟」不足以说明人不在，所以这里要求
/// <see cref="ConfirmTicks"/> 次连续判定，且中途任何一次活动都清零重数。
/// 判错的代价是「你还在它就开跑了」，比多等十分钟糟得多。
/// </para>
/// <para>纯函数：时间与闲置时长由调用方喂，本类不碰任何系统 API。</para>
/// </summary>
public static class AwaySense
{
    /// <summary>多久没动键鼠才可能是「走了」。默认 20 分钟——看一集番不会误判。</summary>
    public static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(20);

    /// <summary>连续多少次判定才认账。采样间隔 30 秒时约等于「持续 5 分钟都符合」。</summary>
    public const int ConfirmTicks = 10;

    /// <summary>判定结果，给界面与日志用。</summary>
    public enum Presence { Unknown, Present, Away }

    public sealed record Snapshot(Presence State, TimeSpan Idle, int Streak, string Reason);

    /// <summary>
    /// 单次采样是否「看起来没人」。<paramref name="foregroundIsGame"/> 为真时不算——
    /// 用户正在游戏里手动玩，这时候抢着起 BetterGI 是最糟的时机。
    /// </summary>
    public static bool LooksAway(TimeSpan idle, bool foregroundIsGame, TimeSpan? threshold = null)
    {
        var limit = threshold ?? IdleThreshold;
        if (limit <= TimeSpan.Zero) return false;
        if (foregroundIsGame) return false;
        return idle >= limit;
    }

    /// <summary>
    /// 推进状态机。<paramref name="prev"/> 是上一次的快照，<paramref name="looksAway"/> 是这次采样。
    /// <b>只有连续 <see cref="ConfirmTicks"/> 次才算真的走开</b>：中途一次活动立刻回到 Present。
    /// </summary>
    public static Snapshot Step(Snapshot? prev, bool looksAway, TimeSpan idle, bool foregroundIsGame)
    {
        int streak = looksAway ? (prev?.Streak ?? 0) + 1 : 0;

        if (!looksAway)
        {
            string why = foregroundIsGame
                ? "你正在游戏里，先不动"
                : "刚才还在动键鼠";
            return new Snapshot(Presence.Present, idle, 0, why);
        }

        if (streak < ConfirmTicks)
        {
            return new Snapshot(prev?.State == Presence.Away ? Presence.Present : (prev?.State ?? Presence.Unknown),
                idle, streak, $"看着像没人（{streak}/{ConfirmTicks}），再看看");
        }

        return new Snapshot(Presence.Away, idle, streak, "你好像走了");
    }

    /// <summary>
    /// 「现在开跑吗」。只在 <see cref="Presence.Away"/> 且队列没在跑时给真。
    /// <paramref name="alreadyRanToday"/> 为真时不再跑——一天只自动跑一次，
    /// 否则你半夜起来上个厕所，它就再跑一轮。
    /// </summary>
    public static bool ShouldStart(Snapshot? s, bool running, bool alreadyRanToday)
    {
        if (s == null || running || alreadyRanToday) return false;
        return s.State == Presence.Away;
    }

    /// <summary>一句话给日志。</summary>
    public static string Describe(Snapshot? s)
    {
        if (s == null) return "还没看过";
        string state = s.State switch
        {
            Presence.Away => "你不在",
            Presence.Present => "你在",
            _ => "还没看出来",
        };
        string idle = s.Idle > TimeSpan.Zero ? $"，已经 {Minutes(s.Idle)} 没动键鼠" : "";
        return state + idle;
    }

    private static string Minutes(TimeSpan span)
    {
        int m = (int)Math.Round(span.TotalMinutes);
        if (m < 1) return "不到 1 分钟";
        if (m < 60) return m + " 分钟";
        int h = m / 60, rest = m % 60;
        return rest == 0 ? h + " 小时" : $"{h} 小时 {rest} 分钟";
    }
}
