using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DragonButler;

/// <summary>
/// 晨报（v1.5，默认关；v1.41 起改叫「日报」）：每天首次启动时，用一段话汇报昨天的情况。
///
/// <para>
/// 数据只有一份：<see cref="DailyReport.Build"/> 算出的 <see cref="DailyReportData"/>。
/// 界面那页日报读的也是它——同一份事实，两种排版。
/// 以前这里自己从 <see cref="GroupArchive"/> 数「几次运行、总耗时多少」，
/// 界面上按 <see cref="DurationLedger"/> 算，两边口径不同（组 vs 工具、是否含未结束的组），
/// 同一天的数字对不上时没人说得清哪个对。现在只剩下一个源头。
/// </para>
/// <para>
/// 这里的文字模板是给 IM 看的：分区头带 emoji（IM 里没有图标，纯文本要靠它分段），
/// 界面那版用图标替换。文案风格按 docs/VOICE.md：口语、克制、不堆感叹号。
/// </para>
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

            var yesterday = DateTime.Today.AddDays(-1);
            var data = DailyReport.Build(yesterday);
            string catchUp = CatchUpNote();
            if (data.IsEmpty && catchUp.Length == 0)
            {
                LogService.LogEvent("日报：昨日无运行记录，跳过播报");
                return;
            }

            string text = data.IsEmpty ? catchUp.Trim() : RenderText(data) + catchUp;
            LogService.LogEvent("[日报] " + text.Replace("\n", " / "));
            NotifyService.Show(NotifyLevel.Info, "龙管家 · 日报", text);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("日报生成失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把一份日报数据排成 IM 文本。分区顺序与界面一致：
    /// 异常与处理 → 配置组执行 → 每个工具 → 空分区合并 → 合计。
    ///
    /// <para>
    /// 只渲染结构化数据里有的东西，不做二次汇总——否则又会出现「界面说 3 小时、推送说 3 小时 12 分」。
    /// </para>
    /// </summary>
    public static string RenderText(DailyReportData data)
    {
        var sb = new StringBuilder();
        sb.Append("昨天（").Append(data.Date).Append("）跑了这些：\n");

        // ===== 异常与处理（放最前）=====
        if (data.Anomalies.Count == 0)
        {
            // 这句是任务书 M5-4 指定的原文，不要改写。
            sb.Append("\n✅ 异常与处理：")
              .Append(data.ToolCount)
              .Append(" 个配置组全部顺利，没有需要你操心的事。\n");
        }
        else
        {
            sb.Append("\n⚠️ 异常与处理：\n");
            foreach (var a in data.Anomalies)
            {
                sb.Append("· ").Append(a.Group).Append(" 在 ").Append(a.Stage)
                  .Append(" 卡住 → 管家已").Append(a.Action).Append('\n');
            }
        }

        // ===== 配置组执行（M5-1：耗时是普通文本，差异是人话）=====
        if (data.Groups.Count > 0)
        {
            sb.Append("\n📋 配置组执行：\n");
            foreach (var g in data.Groups)
            {
                sb.Append("· ").Append(g.Name).Append("　").Append(DailyReport.SayDuration(g.Seconds));
                if (g.DeltaSeconds != null)
                {
                    string delta = DailyReport.SayDelta(g.DeltaSeconds.Value, out _);
                    sb.Append("（较上次").Append(delta).Append('）');
                }
                sb.Append('\n');
            }
        }

        // ===== 每个工具 =====
        if (data.Tools.Count > 0)
        {
            sb.Append("\n🧰 每个工具：\n");
            foreach (var t in data.Tools)
            {
                sb.Append("· ").Append(string.IsNullOrWhiteSpace(t.Name) ? t.ToolId : t.Name)
                  .Append("　").Append(DailyReport.SayDuration(t.Seconds));
                if (t.Runs > 1) sb.Append("（").Append(t.Runs).Append(" 次）");
                sb.Append('\n');
            }
        }

        // ===== 空分区合并（M5-3）：两个以上才值得说一句 =====
        if (data.EmptySections.Count >= 2)
        {
            sb.Append("\n📦 ").Append(string.Join(" / ", data.EmptySections))
              .Append("：今天都没有记录。\n");
        }

        // ===== 合计 + 趋势（M5-5）=====
        sb.Append("\n合计耗时：").Append(DailyReport.SayDuration(data.TotalSeconds));
        string? trend = DailyReport.SayTrend(data.TotalSeconds, data.PreviousTotalSeconds);
        if (!string.IsNullOrEmpty(trend)) sb.Append(" · ").Append(trend);
        sb.Append('。');

        return sb.ToString();
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
            if ((s.ScheduleWeekdays & 0x7F) == 0) return "";

            // 昨天最后一个可能的计划点 + 补跑窗口之后一分钟 = 必然已经错过的时刻
            var yesterday = DateTime.Today.AddDays(-1);
            int window = Math.Clamp(s.ScheduleCatchUpMinutes, 0, SchedulePlanner.MaxCatchUpMinutes);
            var probe = yesterday.Date.AddHours(s.ScheduleHour).AddMinutes(s.ScheduleMinute)
                .AddMinutes(window).AddMinutes(1);
            if (probe >= DateTime.Now) probe = DateTime.Now; // 极端设置下别问到未来去

            var decision = SchedulePlanner.Decide(
                s.ScheduleEnabled, false,
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
            return $"\n昨天 {when} 的定时没跑成{why}。";
        }
        catch (Exception ex)
        {
            LogService.LogEvent("日报补跑提示生成失败：" + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// 耗时文案：3小时12分 / 12分 / 不到 1 分钟。
    ///
    /// <para>
    /// tidy：这条文案规则原来有两份——这里一份（吃 double 分钟、四舍五入），
    /// <c>QueueService</c> 里一份（吃 TimeSpan、直接截断）。两份的文案分支一字不差，
    /// 只有入参换算不同，属于同一条规则的复制。现在规则只在这里，
    /// <c>QueueService</c> 的通知与日志都调这一个。
    /// </para>
    /// </summary>
    public static string FormatDuration(TimeSpan t)
    {
        int h = (int)t.TotalHours;
        int m = t.Minutes;
        if (h > 0) return $"{h}小时{m}分";
        if (m > 0) return $"{m}分";
        return "不到 1 分钟";
    }

    /// <summary>
    /// 同上，入参是先四舍五入到整分钟的分钟数（周报的「平均每次」是小数，需要这一层）。
    /// 换算留在这里、文案规则留在上面，避免再长出第二份分支。
    /// </summary>
    public static string FormatDuration(double totalMinutes)
        => FormatDuration(TimeSpan.FromMinutes(Math.Round(totalMinutes)));
}
