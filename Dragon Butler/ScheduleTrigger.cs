using System;
using System.Linq;

namespace DragonButler;

/// <summary>应用常驻期间的后台触发，不依赖主窗是否存在。</summary>
internal static class ScheduleTrigger
{
    internal static void Check(DateTime now)
    {
        try
        {
            var s = AppState.Settings;
            if (s.ScheduleEnabled) CheckSchedule(s, now);
            else if (s.AwayStartEnabled) CheckAway(s, now);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("检查后台触发失败：" + ex);
        }
    }

    private static void CheckSchedule(AppSettings s, DateTime now)
    {
        DateTime? last = DateTime.TryParse(s.LastScheduleRunAt, out var at) ? at : null;
        var decision = SchedulePlanner.Decide(s.ScheduleEnabled, QueueService.IsRunning,
            s.ScheduleHour, s.ScheduleMinute, s.ScheduleWeekdays, s.ScheduleCatchUpMinutes, last, now);
        if (!decision.ShouldRun) return;

        // 先记计划点再触发；启动失败也不每 30 秒重复触发同一次计划。
        s.LastScheduleRunAt = decision.DueAt!.Value.ToString("yyyy-MM-dd HH:mm");
        AppState.SaveSettings();
        LogService.LogEvent("定时：" + decision.Reason + "，触发队列");
        _ = QueueService.StartAsync(QueueTrigger.Timer);
    }

    private static void CheckAway(AppSettings s, DateTime now)
    {
        var snap = AwayWatcher.Sample();
        bool ranToday = QueueService.DoneToolIdsToday().Count > 0
                        || DateTime.TryParse(s.LastRunAt, out var at) && at.Date == now.Date;
        if (!AwaySense.ShouldStart(snap, QueueService.IsRunning, ranToday)) return;

        DecisionLog.Add(DecisionSource.Schedule, "你走开，那就现在跑",
            $"连续 {AwaySense.ConfirmTicks} 次判定都没人动键鼠，已经安静 {AwaySense.Describe(snap).Split('，').Last()}");
        AppState.PublishLog("✓ 你好像走了，开始跑今天的队列");
        LogService.LogEvent("触发器：一走开就跑 —— " + AwaySense.Describe(snap));
        AwayWatcher.Reset();
        _ = QueueService.StartAsync(QueueTrigger.Timer);
    }
}
