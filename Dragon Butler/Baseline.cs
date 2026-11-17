using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 基线（v1.20 / 奇思妙想 #3）：它记得住「正常」长什么样。
///
/// <para>
/// <b>和阈值不是一回事</b>：阈值是「超过 85% 就报」——同一个数对一台机器太松、对另一台太紧，
/// 而用户根本不知道自己的机器「正常」是多少。基线是「你自己过去两周什么样」，
/// 于是同一句话在任何机器上都成立：「今天的内存比你平时高两成」。
/// </para>
/// <para>
/// <b>只用中位数，不用平均值</b>：一次 Excel 算大表能把平均值拉高到再也报不出来；
/// 中位数只关心「一半的日子比这低」。这与 <see cref="GroupArchive.MedianMinutes"/> 同一立场。
/// </para>
/// <para>
/// <b>样本少于 <see cref="MinSamples"/> 就闭嘴</b>：跑了两天就说「你平时怎样」是在编。
/// 宁可三天不说话，也不给一个看起来很像回事的错数字。
/// </para>
/// <para>纯函数：样本由调用方喂，不读磁盘、不看时钟。</para>
/// </summary>
public static class Baseline
{
    /// <summary>至少要有几天样本才评价。理由见类注释。</summary>
    public const int MinSamples = 5;

    /// <summary>默认回看天数。两周够覆盖「工作日/周末」两种节奏，又不至于把三个月前的旧习惯算进来。</summary>
    public const int DefaultDays = 14;

    /// <summary>偏离多少才算「不正常」。20% —— 小于这个数多半是天气。</summary>
    public const double NoticeRatio = 0.20;

    /// <summary>偏离多少要提醒。50% —— 到这个程度基本能确定有事。</summary>
    public const double WarnRatio = 0.50;

    public enum Level { Ok, Notice, Warn }

    /// <summary>一次判断的结果。Text 为空 = 没话说。</summary>
    public sealed record Verdict(Level Level, double Baseline, double Actual, double Ratio, string Text);

    /// <summary>
    /// 中位数。空样本返回 null；偶数个取中间两个的平均（与存档那边的口径一致）。
    /// </summary>
    public static double? Median(IEnumerable<double>? samples)
    {
        if (samples == null) return null;
        // 0 是有效样本：「那天一项都没失败」「那天没跑」。
        // 负数、NaN、无穷才是脏数据。调用方负责别把「没跑」当成 0 喂进来
        // （<see cref="BaselineFacts"/> 里 skip 掉没跑的日子就是这么做的）。
        var list = samples.Where(v => v >= 0 && !double.IsNaN(v) && !double.IsInfinity(v))
                          .OrderBy(v => v)
                          .ToList();
        if (list.Count == 0) return null;
        int mid = list.Count / 2;
        return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2.0;
    }

    /// <summary>
    /// 拿今天的实测值跟自己的基线比。
    /// <paramref name="unit"/> 是单位后缀（如 <c>"%</c>、<c>"MB"</c>、<c>"分钟"</c>），
    /// 只影响文案，不影响判定。
    /// </summary>
    public static Verdict Judge(IEnumerable<double>? samples, double? actual, string unit = "", string subject = "它")
    {
        var median = Median(samples);
        // 样本数要数「有效的天」，不能只数大于 0 的——
        // 「过去 6 天每天都是 0 项失败」是 6 天样本，不是 0 天。
        int count = ValidCount(samples);

        if (median == null || count < MinSamples || actual == null || actual < 0)
            return new Verdict(Level.Ok, median ?? 0, actual ?? 0, 1.0, "");

        // 平时一直是 0，今天不是 0 —— 这是最该报的一种情况（「以前从没出过问题」），
        // 用比值算不出来（除以 0），所以单独处理。反之今天 0 项、平时 0 项，没什么可说。
        if (median.Value <= 0)
        {
            if (actual.Value <= 0) return new Verdict(Level.Ok, 0, actual.Value, 1.0, "");
            string first = $"{subject}：今天 {actual.Value:0.#}{unit}，平时一次都没有。";
            return new Verdict(Level.Warn, 0, actual.Value, double.PositiveInfinity, first);
        }

        double ratio = actual.Value / median.Value;
        double diff = ratio - 1.0;

        // 只报「比平时多」。低于基线一律不说话——两个理由：
        // ① 「今天没跑成的项比平时少一半」是好消息，用警告色显示是错的；
        // ② 「今天只用了 8 分钟」可能只是跑得少，未必值得夸。
        // 基线的用途是「提醒你可能出事了」，不是做体检报告。
        if (diff < NoticeRatio)
            return new Verdict(Level.Ok, median.Value, actual.Value, ratio, "");

        var level = diff >= WarnRatio ? Level.Warn : Level.Notice;
        string pct = ((int)Math.Round(diff * 100)).ToString();
        string text = $"{subject}：今天 {actual.Value:0.#}{unit}，比你平时（{median.Value:0.#}{unit}）高 {pct}%。";

        return new Verdict(level, median.Value, actual.Value, ratio, text);
    }

    /// <summary>
    /// 从一串按天排好的样本里，判断「最近这一天」是否是异常日。
    /// <paramref name="recent"/> 里最后一个算今天，前面的算历史（不含今天）。
    /// 同一天出现在历史里也不算——调用方负责切分，这里不再猜。
    /// </summary>
    public static Verdict JudgeLast(IReadOnlyList<double>? history, IReadOnlyList<double>? recent, string unit = "", string subject = "它")
        => Judge(history, recent is { Count: > 0 } ? recent[^1] : null, unit, subject);

    /// <summary>一句话概括基线本身，给界面标题用。样本不足时明说不够，不含糊。</summary>
    public static string Describe(IEnumerable<double>? samples, int days = DefaultDays, string unit = "")
    {
        int count = ValidCount(samples);
        var median = Median(samples);
        if (median == null || count < MinSamples)
            return $"样本还不够（{count}/{MinSamples} 天），攒够之前不评价。";
        return $"按最近 {days} 天算，平时 {median.Value:0.#}{unit}（{count} 天样本）。";
    }

    /// <summary>有效样本数：非负、非 NaN、非无穷。0 是有效值（「那天没出问题」）。</summary>
    private static int ValidCount(IEnumerable<double>? samples)
        => samples?.Count(v => v >= 0 && !double.IsNaN(v) && !double.IsInfinity(v)) ?? 0;
}
