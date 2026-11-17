using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 时间银行（v1.20 / 奇思妙想 #5）：就一个数字——这个月它替你跑了多久。
///
/// <para>
/// <b>为什么值得做</b>：这类工具的效果天然不可见。跑得好，说明你什么都没看见；
/// 跑得不好，你才看得见它。长期下来，唯一留在记忆里的印象就是「它老出问题」。
/// 一个诚实的累计时长能把这条曲线翻过来，而且它不需要任何新采集——
/// <see cref="GroupArchive"/> 里本来就有每天的起止。
/// </para>
/// <para>
/// <b>为什么只给月累计，不给「折合多少小时自由时间」</b>：后者要假设「你本来会手动做」，
/// 而大多数人本来就不做（不做就不亏）。数字一旦开始替用户想象，它就不再可信。
/// </para>
/// <para>纯函数：天与分钟的列表由调用方喂。</para>
/// </summary>
public static class TimeBank
{
    /// <summary>一个月里每天跑了多少分钟（0 表示那天没跑）。日期由调用方给，函数只做算术。</summary>
    public sealed record Day(DateTime Date, double Minutes);

    public sealed record Totals(double ThisMonthMinutes, double AllMinutes, int ActiveDays, int MonthActiveDays);

    /// <summary>把逐日数据分成「本月」与「全部」。<paramref name="now"/> 决定哪个月是本月。</summary>
    public static Totals Sum(IEnumerable<Day>? days, DateTime now)
    {
        var list = (days ?? Enumerable.Empty<Day>())
            .Where(d => d.Minutes > 0)
            .ToList();

        double all = list.Sum(d => d.Minutes);
        double month = list.Where(d => d.Date.Year == now.Year && d.Date.Month == now.Month)
                           .Sum(d => d.Minutes);

        return new Totals(
            month,
            all,
            list.Select(d => d.Date.Date).Distinct().Count(),
            list.Where(d => d.Date.Year == now.Year && d.Date.Month == now.Month)
                .Select(d => d.Date.Date).Distinct().Count());
    }

    /// <summary>
    /// 一句话。**没有数据时返回空串**——界面据此整块不出现，
    /// 而不是显示「本月 0 小时」让人以为坏了。
    /// </summary>
    public static string Describe(Totals? t, DateTime now)
    {
        if (t == null || t.ThisMonthMinutes <= 0) return "";

        string head = $"这个月它替你跑了 {Hours(t.ThisMonthMinutes)}";
        string tail = t.MonthActiveDays > 0 ? $"，{t.MonthActiveDays} 天" : "";
        // 跨月才有必要提累计：本月第一天看到「累计 900 小时」会莫名其妙。
        string all = t.AllMinutes > t.ThisMonthMinutes + 1
            ? $"。从装上到现在一共 {Hours(t.AllMinutes)}"
            : "";
        return head + tail + all + "。";
    }

    /// <summary>小时数：不到 1 小时给分钟，免得出现「0 小时」。</summary>
    public static string Hours(double minutes)
    {
        if (minutes <= 0) return "0 分钟";
        if (minutes < 60) return ((int)Math.Round(minutes)) + " 分钟";
        double hours = minutes / 60.0;
        // 十小时以上不必带小数——「137.4 小时」读起来像账单
        return hours >= 10 ? ((int)Math.Round(hours)) + " 小时" : hours.ToString("0.#") + " 小时";
    }
}
