using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 交接单（v1.20 / 奇思妙想 #6）：出门前告诉你「这三天会发生什么」。
///
/// <para>
/// <b>为什么不是把设置念一遍</b>：设置页回答「我配了什么」，用户出门前想知道的是
/// 「我离开之后它会做什么、什么情况下会停手」。这两件事差得很远——
/// 一个「已开启自动重启」的勾，读成人话是「它崩了会自己再拉起来，最多三次」。
/// </para>
/// <para>
/// <b>为什么把「兜不住」写在最前面</b>：<see cref="Preflight"/> 是出发前查缺，
/// 这里是反过来——先说清它做不到什么，再说它会做什么。反过来写就成了宣传单。
/// </para>
/// <para>纯函数：输入是已经算好的事实，不读磁盘、不看时钟。</para>
/// </summary>
public static class Handover
{
    public enum Severity { Risk, Plan, Fine }

    /// <summary>一条交接说明。<see cref="Key"/> 稳定供断言，<see cref="Text"/> 是给人看的人话。</summary>
    public sealed record Item(string Key, Severity Level, string Text);

    public sealed record Input(
        int Days,
        bool ScheduleEnabled,
        bool AwayStartEnabled,
        string? NextRunText,
        bool NotifyLocalOnly,
        bool NotifyReady,
        int EnabledTools,
        int ReadyTools,
        bool AutoRestartBgi,
        int RestartsToday,
        bool StopWhenFailed,
        bool AfterRunDestructive,
        bool BgiPathOk,
        int GroupCount,
        int FreeDiskMb);

    public sealed record Sheet(IReadOnlyList<Item> Items, string Headline, bool HasRisk);

    public static Sheet Build(Input input)
    {
        var items = new List<Item>();
        int days = Math.Max(1, input.Days);

        // ===== 先说兜不住的事 =====
        if (!input.BgiPathOk)
            items.Add(new("bgi", Severity.Risk, "BetterGI 的路径不通，这几天一次都不会跑。"));

        if (input.EnabledTools == 0)
            items.Add(new("tools", Severity.Risk, "队列里没有启用的工具，这几天什么都不会发生。"));

        if (input.EnabledTools > 0 && input.ReadyTools < input.EnabledTools)
            items.Add(new("tools.partial", Severity.Risk,
                $"有 {input.EnabledTools - input.ReadyTools} 个工具没填路径，轮到它们时会跳过。"));

        if (input.NotifyLocalOnly)
            items.Add(new("notify", Severity.Risk, "提醒只走本机弹窗。你人不在，就等于没有提醒。"));
        else if (!input.NotifyReady)
            items.Add(new("notify.broken", Severity.Risk, "选了远程提醒但没配齐，出事你收不到消息。"));

        if (!input.ScheduleEnabled && !input.AwayStartEnabled)
            items.Add(new("trigger", Severity.Risk, "没有任何触发方式，它不会自己开始。"));

        if (input.GroupCount == 0)
            items.Add(new("groups", Severity.Risk, "还没扫到配置组。多半是 BetterGI 一次都没跑过。"));

        if (input.FreeDiskMb >= 0 && input.FreeDiskMb < 2048)
            items.Add(new("disk", Severity.Risk,
                $"程序所在磁盘只剩 {input.FreeDiskMb} MB。日志和现场包可能写不下。"));

        // ===== 再说会发生什么 =====
        if (input.ScheduleEnabled && !string.IsNullOrWhiteSpace(input.NextRunText))
            items.Add(new("plan.schedule", Severity.Plan, $"每天 {input.NextRunText} 自己开跑。"));
        else if (input.AwayStartEnabled)
            items.Add(new("plan.away", Severity.Plan, "你走开 20 分钟后开跑（连续安静才认账）。"));

        if (input.EnabledTools > 0 && input.ReadyTools > 0)
        {
            string many = input.EnabledTools == 1 ? "1 个工具" : $"{input.EnabledTools} 个工具";
            items.Add(new("plan.tools", Severity.Plan,
                input.StopWhenFailed
                    ? $"按顺序跑 {many}，中间有一个失败就整队停下。"
                    : $"按顺序跑 {many}，中间有一个失败会继续往下跑。"));
        }

        if (input.AutoRestartBgi)
        {
            // 没有次数上限这件事必须说出来：反复崩溃时它会反复重启，
            // 用户在外面看不到「它已经崩了十七次」。
            if (input.RestartsToday > 0)
                items.Add(new("plan.restart", Severity.Plan,
                    $"BetterGI 崩了会自动再拉起来（今天已经重启过 {input.RestartsToday} 次，没有次数上限）。"));
            else
                items.Add(new("plan.restart", Severity.Plan, "BetterGI 崩了会自动再拉起来。" +
                    "没有次数上限——反复崩溃时它会反复重启。"));
        }
        else
            items.Add(new("plan.norestart", Severity.Plan, "BetterGI 崩了不会自动重启——那次之后就停在那儿。"));

        if (input.AfterRunDestructive)
            items.Add(new("plan.afterrun", Severity.Plan,
                "跑完之后会自动关机或睡眠。你人在外面时，它可以替你关掉这台机器。"));

        // ===== 兜底一句：怎么知道出事了 =====
        if (!input.NotifyLocalOnly && input.NotifyReady)
            items.Add(new("fine.notify", Severity.Fine, "出问题会推到你手机上，回来能看到完整记录。"));

        string headline = BuildHeadline(items, days);
        return new Sheet(items, headline, items.Any(i => i.Level == Severity.Risk));
    }

    private static string BuildHeadline(List<Item> items, int days)
    {
        int risks = items.Count(i => i.Level == Severity.Risk);
        if (risks == 0) return $"这 {days} 天基本能自己过。";
        return risks == 1
            ? $"有一件事会让这 {days} 天白跑。"
            : $"有 {risks} 件事会让这 {days} 天白跑。";
    }

    /// <summary>纯文本版（复制到手机或发给家人用）。</summary>
    public static string Report(Sheet sheet, Input input)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"离开 {input.Days} 天 · 一条龙管家");
        sb.AppendLine(sheet.Headline);
        sb.AppendLine();
        foreach (var i in sheet.Items)
        {
            string mark = i.Level switch
            {
                Severity.Risk => "×",
                Severity.Plan => "·",
                _ => "✓",
            };
            sb.AppendLine($"{mark} {i.Text}");
        }
        return sb.ToString().TrimEnd();
    }
}
