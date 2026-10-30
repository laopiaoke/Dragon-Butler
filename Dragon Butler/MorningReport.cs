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
            if (records.Count == 0)
            {
                LogService.LogEvent("晨报：昨日无运行记录，跳过播报");
                return;
            }

            string text = BuildSummary(records);
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
