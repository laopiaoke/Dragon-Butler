using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 晨报（v1.5，默认关）：每天首次启动时，用一句话汇报昨天队列的情况。
/// 数据源只有 <see cref="GroupArchive"/>（零新增采集）：一处配置组记录 = 一次运行，
/// 全部有结束时间 = 全部成功，耗时按开始/结束时间累加。无运行日不播报。
/// </summary>
public static class MorningReportService
{
    /// <summary>
    /// 启动时调用一次：开启且今天还没播报过、昨天有记录 → 播报。
    /// 同一天只判断一次（无论有没有数据都记下日期），避免反复打扰。
    /// </summary>
    public static void MaybeShow()
    {
        try
        {
            var s = AppState.Settings;
            if (!s.MorningReportEnabled) return;

            string today = DateTime.Today.ToString("yyyy-MM-dd");
            if (string.Equals(s.MorningReportDate, today, StringComparison.Ordinal)) return;

            s.MorningReportDate = today;
            AppState.SaveSettings();

            var records = GroupArchive.GetDay(DateTime.Today.AddDays(-1));
            string catchUp = CatchUpNote();
            if (records.Count == 0 && catchUp.Length == 0)
            {
                LogService.LogEvent("晨报：昨日无运行记录，跳过播报");
                return;
            }

            string text = records.Count == 0 ? catchUp : BuildSummary(records) + catchUp;
            LogService.LogEvent("[晨报] " + text);
            NotifyService.Show(NotifyLevel.Info, "龙管家 · 晨报", text);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("晨报生成失败：" + ex.Message);
        }
    }

    /// <summary>把昨天的组归档拼成一句话（"昨日队列：2 次运行，全部成功，总耗时 3小时12分"）。</summary>
    public static string BuildSummary(IReadOnlyList<GroupRecord> records)
    {
        int runs = records.Count;
        int finished = records.Count(r => !string.IsNullOrWhiteSpace(r.End));
        string state = finished >= runs ? "全部成功" : $"{runs - finished} 项未完成";

        double minutes = 0;
        foreach (var r in records)
        {
            var span = DurationOf(r);
            if (span.HasValue) minutes += span.Value.TotalMinutes;
        }

        return $"昨日队列：{runs} 次运行，{state}，总耗时 {FormatDuration(minutes)}";
    }

    /// <summary>
    /// v1.10 / G5：把「昨天该跑却没跑」说出来。
    ///
    /// <para>
    /// 定时补跑（v1.6 F1）只在开机的前提下成立：08:00 的计划点，10:00 才开机就已经超出补跑窗口，
    /// 于是「今天什么都没跑」。以前这件事是静默的——用户第二天打开软件，什么提示也没有，
    /// 只看到「上次执行」停在两天前。这里补上那一句话。
    /// </para>
    /// <para>
    /// 判据完全复用 <see cref="SchedulePlanner"/>：把「现在」换成「昨天任意一个必然错过的时刻」去问它，
    /// 得到的原因就是当时没跑的原因。不新增任何状态字段，也不需要在错过的那一刻记日志
    /// （那一刻电脑很可能根本没开）。
    /// </para>
    /// </summary>
    public static string CatchUpNote()
    {
        try
        {
            var s = AppState.Settings;
            if (!s.ScheduleEnabled) return "";
            if (s.VacationMode) return "";
            if ((s.ScheduleWeekdays & 0x7F) == 0) return "";

            // 昨天最后一个可能的计划点 + 补跑窗口之后一分钟 = 必然已经错过的时刻
            var yesterday = DateTime.Today.AddDays(-1);
            int window = Math.Clamp(s.ScheduleCatchUpMinutes, 0, SchedulePlanner.MaxCatchUpMinutes);
            var probe = yesterday.Date.AddHours(s.ScheduleHour).AddMinutes(s.ScheduleMinute)
                .AddMinutes(window).AddMinutes(1);
            if (probe >= DateTime.Now) probe = DateTime.Now; // 极端设置下别问到未来去

            var decision = SchedulePlanner.Decide(
                s.ScheduleEnabled, s.VacationMode, false,
                s.ScheduleHour, s.ScheduleMinute, s.ScheduleWeekdays,
                s.ScheduleCatchUpMinutes, null, probe);

            // 只有「昨天那个点确实该跑、但因为过了窗口而没跑」才有话说
            if (decision.ShouldRun || decision.DueAt == null) return "";
            if (decision.DueAt.Value.Date != yesterday) return "";
            if (!decision.Reason.StartsWith("上一个计划点", StringComparison.Ordinal)
                && !decision.Reason.StartsWith("已过计划时间", StringComparison.Ordinal)) return "";

            string when = $"{s.ScheduleHour:00}:{s.ScheduleMinute:00}";
            string why = window == 0
                ? "（补跑是关着的，错过那一分钟就不再补）"
                : $"（补跑窗口 {window} 分钟内没跑起来）";
            return $" 昨天 {when} 的定时没跑成{why}。";
        }
        catch (Exception ex)
        {
            LogService.LogEvent("晨报补跑提示生成失败：" + ex.Message);
            return "";
        }
    }

    private static TimeSpan? DurationOf(GroupRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.Start) || string.IsNullOrWhiteSpace(r.End)) return null;
        if (!TimeSpan.TryParse(r.Start, out var start) || !TimeSpan.TryParse(r.End, out var end)) return null;
        var span = end - start;
        if (span < TimeSpan.Zero) span += TimeSpan.FromDays(1); // 跨零点
        return span;
    }

    /// <summary>耗时文案：3小时12分 / 12分 / 不到 1 分钟。</summary>
    public static string FormatDuration(double totalMinutes)
    {
        int minutes = (int)Math.Round(totalMinutes);
        int h = minutes / 60;
        int m = minutes % 60;
        if (h > 0) return $"{h}小时{m}分";
        if (m > 0) return $"{m}分";
        return "不到 1 分钟";
    }
}
