using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>体检结论的严重程度。顺序即大小，<see cref="Block"/> 最重。</summary>
public enum PreflightLevel
{
    /// <summary>没问题，不用管。</summary>
    Ok = 0,
    /// <summary>不挡路，但值得知道（比如定时没开）。</summary>
    Notice = 1,
    /// <summary>能跑，但很可能不是你想要的（比如通知渠道没配，晚上收不到消息）。</summary>
    Warn = 2,
    /// <summary>真跑不起来，或者跑起来也不是你要的东西。</summary>
    Block = 3,
}

/// <summary>一条检查结果。</summary>
/// <param name="Key">稳定的标识，探针按它断言，不按文案。</param>
/// <param name="Title">一句话结论，界面上最显眼那行。</param>
/// <param name="Level">严重程度。</param>
/// <param name="Detail">为什么、会怎样。说人话，不解释实现。</param>
/// <param name="FixText">按钮文字；空 = 这条没有一键修法。</param>
/// <param name="FixCommand">点了发给 <c>RunCommand</c> 的命令 id；空 = 没有。</param>
public sealed record PreflightItem(
    string Key,
    string Title,
    PreflightLevel Level,
    string Detail,
    string FixText,
    string FixCommand);

/// <summary>一次体检的完整结果。</summary>
public sealed record PreflightReport(
    IReadOnlyList<PreflightItem> Items,
    PreflightLevel Worst,
    bool CanRun,
    string Headline);

/// <summary>体检要用的全部原始事实。由调用方去读，本类不碰磁盘、不看时钟。</summary>
public sealed record PreflightInput(
    bool BgiConfigured,
    bool BgiExists,
    bool BgiRunning,
    int EnabledTools,
    int ReadyTools,
    int GroupCount,
    string? DragonConfigName,
    IReadOnlyList<string>? DragonConfigs,
    string? NotifyMode,
    string? NotifyEndpoint,
    IReadOnlyList<string>? MissingPresetGroups,
    bool PresetMissing,
    string? LastRunAt,
    bool LastRunOk,
    string? LastRunReason,
    bool ScheduleEnabled,
    bool Writable);

/// <summary>
/// v1.19：开跑前体检。
///
/// 要解决的问题不是「功能不够」，而是「填了一堆、点了没跑、不知道错在哪」。
/// 新用户卡住的地方几乎都不是功能缺失，而是某个前置条件没满足，而界面只在
/// 真跑起来之后才告诉他。
///
/// 三条约束：
/// 1. <b>只读</b>：这个类不改设置、不写文件、不发请求。修法都是跳去对应界面，由用户自己按。
/// 2. <b>纯函数</b>：输入全是原始事实，输出全是文案。判定能在探针里逐条断言。
/// 3. <b>只在真挡路时才拦</b>：<see cref="PreflightLevel.Block"/> 才阻止开跑，
///    其余一律只提示——半夜弹个窗说「你没开定时」比不弹更烦人。
/// </summary>
public static class Preflight
{
    /// <summary>
    /// 排序权重：越重越大，配合 <c>OrderByDescending</c> 就是「重的排前面」。
    /// 直接用枚举值——<see cref="PreflightLevel"/> 的顺序本身就是为了当权重才这样定义的，
    /// 再绕一层减法只会把方向弄反（v1.19 真犯过这个错，探针逮到）。
    /// </summary>
    public static int Rank(PreflightLevel level) => (int)level;

    /// <summary>跑一遍全部检查。</summary>
    public static PreflightReport Build(PreflightInput input)
    {
        var items = new List<PreflightItem>
        {
            CheckBgi(input),
            CheckTools(input),
            CheckGroups(input),
            CheckDragonConfig(input),
            CheckPreset(input),
            CheckNotify(input),
            CheckWritable(input),
            CheckRunning(input),
            CheckLastRun(input),
            CheckSchedule(input),
        };

        // 重的排前面：用户先看到的应该是「跑不了」，而不是「定时没开」。
        var ordered = items
            .OrderByDescending(i => Rank(i.Level))
            .ToList();

        var worst = items.Count == 0 ? PreflightLevel.Ok : items.Max(i => i.Level);
        return new PreflightReport(ordered, worst, worst != PreflightLevel.Block, Headline(ordered, worst));
    }

    private static string Headline(IReadOnlyList<PreflightItem> items, PreflightLevel worst)
    {
        int block = items.Count(i => i.Level == PreflightLevel.Block);
        int warn = items.Count(i => i.Level == PreflightLevel.Warn);
        if (block > 0) return $"还差 {block} 件才能开跑";
        if (warn > 0) return $"可以跑，但有 {warn} 件事建议先看一眼";
        return "检查过了，可以开跑";
    }

    private static PreflightItem CheckBgi(PreflightInput i)
    {
        if (!i.BgiConfigured)
        {
            return new PreflightItem("bgi.missing", "还没告诉我在哪", PreflightLevel.Block,
                "找不到 BetterGI 的 exe，就没法拉起它。选一次就行，之后都记得。",
                "选择 BetterGI.exe", "pick.bettergi");
        }
        if (!i.BgiExists)
        {
            return new PreflightItem("bgi.gone", "BetterGI 的路径失效了", PreflightLevel.Block,
                "记着的那个位置已经没有 BetterGI.exe 了——多半是挪了目录，或者更新时装到了别处。重新选一次就好。",
                "重新选择", "pick.bettergi");
        }
        return new PreflightItem("bgi.ok", "BetterGI 路径正常", PreflightLevel.Ok, "", "", "");
    }

    private static PreflightItem CheckTools(PreflightInput i)
    {
        if (i.EnabledTools == 0)
        {
            return new PreflightItem("tools.none", "队列里一个工具都没开", PreflightLevel.Block,
                "没勾任何工具，点了开跑也只会立刻结束。",
                "去勾一个", "view.tools");
        }
        if (i.ReadyTools == 0)
        {
            return new PreflightItem("tools.notready", "勾了的工具都没有路径", PreflightLevel.Block,
                $"勾了 {i.EnabledTools} 个，但一个都没填 exe 位置——拉不起来。",
                "去填路径", "view.tools");
        }
        if (i.ReadyTools < i.EnabledTools)
        {
            return new PreflightItem("tools.partial", "有工具会跑不了", PreflightLevel.Warn,
                $"勾了 {i.EnabledTools} 个，其中 {i.EnabledTools - i.ReadyTools} 个没填路径，轮到它时会跳过。",
                "去补上", "view.tools");
        }
        return new PreflightItem("tools.ok", "要跑的工具都在", PreflightLevel.Ok, "", "", "");
    }

    private static PreflightItem CheckGroups(PreflightInput i)
    {
        if (i.GroupCount > 0)
            return new PreflightItem("groups.ok", $"认出 {i.GroupCount} 个配置组", PreflightLevel.Ok, "", "", "");

        return new PreflightItem("groups.none", "一个配置组都没认出来", PreflightLevel.Warn,
            "这不会挡住开跑，但 BGI 只会按它自己当前选中的流程走。"
            + "多半是 BetterGI 还没成功跑过一次——它跑一次之后才会生成配置组文件。",
            "", "");
    }

    private static PreflightItem CheckDragonConfig(PreflightInput i)
    {
        // 留空是合法状态：意思是「用 BGI 当前选中的那份」，我们无从判断对错。
        if (string.IsNullOrWhiteSpace(i.DragonConfigName))
            return new PreflightItem("dragon.empty", "用 BetterGI 当前选中的一条龙配置", PreflightLevel.Ok, "", "", "");

        var known = i.DragonConfigs ?? Array.Empty<string>();
        bool exists = known.Any(n => string.Equals(n, i.DragonConfigName!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            return new PreflightItem("dragon.missing", $"找不到一条龙配置「{i.DragonConfigName}」", PreflightLevel.Warn,
                "这个名字在当前 BetterGI 目录里没有对应的配置文件。开跑时 BGI 可能会报错，也可能什么都没发生。",
                "去工具设置看", "view.tools");
        }
        return new PreflightItem("dragon.ok", $"一条龙配置「{i.DragonConfigName}」在", PreflightLevel.Ok, "", "", "");
    }

    private static PreflightItem CheckPreset(PreflightInput i)
    {
        if (i.PresetMissing)
        {
            return new PreflightItem("preset.missing", "选中的那套组合已经不在了", PreflightLevel.Warn,
                "组合被删了，这次会回退到全局勾选——跑的东西可能和你想的不一样。",
                "重选一套", "view.presets");
        }
        var missing = i.MissingPresetGroups ?? Array.Empty<string>();
        if (missing.Count > 0)
        {
            return new PreflightItem("preset.groups", $"组合里有 {missing.Count} 个组已经不在了", PreflightLevel.Warn,
                $"没有的组：{string.Join("、", missing.Take(5))}。BGI 那边认不出这些名字时会跳过。",
                "看看这套组合", "view.presets");
        }
        return new PreflightItem("preset.ok", "配置组合正常", PreflightLevel.Ok, "", "", "");
    }

    private static PreflightItem CheckNotify(PreflightInput i)
    {
        bool local = IsLocalChannel(i.NotifyMode);
        if (local)
            return new PreflightItem("notify.local", "跑完提醒在这台电脑上弹", PreflightLevel.Ok, "", "", "");

        if (!NotifyReady(i.NotifyMode, i.NotifyEndpoint))
        {
            return new PreflightItem("notify.none", "选了远程提醒，但没填地址", PreflightLevel.Warn,
                "跑完的消息发不出去。挂机的时候这是你唯一能看到结果的地方，建议先测一条。",
                "去配通知", "view.notify");
        }
        return new PreflightItem("notify.ok", "远程提醒已配置", PreflightLevel.Ok, "", "", "");
    }

    private static PreflightItem CheckWritable(PreflightInput i)
    {
        if (i.Writable) return new PreflightItem("disk.ok", "数据目录可写", PreflightLevel.Ok, "", "", "");
        return new PreflightItem("disk.readonly", "程序旁边写不了文件", PreflightLevel.Block,
            "程序放在一个只读的位置（Program Files、光盘、只读共享盘都不行）。"
            + "台账、日志、配置都存不下来，跑完也不会留下任何记录。把它挪到普通文件夹里再打开。",
            "", "");
    }

    private static PreflightItem CheckRunning(PreflightInput i)
    {
        if (!i.BgiRunning) return new PreflightItem("run.idle", "BetterGI 当前没在跑", PreflightLevel.Ok, "", "", "");
        return new PreflightItem("run.busy", "BetterGI 现在是开着的", PreflightLevel.Notice,
            "开跑前我会先把它关掉再重新拉起——它自己启动的流程和带参数启动不是一回事。"
            + "如果你正手动用它做什么，先自己停一下。",
            "", "");
    }

    private static PreflightItem CheckLastRun(PreflightInput i)
    {
        if (string.IsNullOrWhiteSpace(i.LastRunAt))
            return new PreflightItem("last.none", "还没跑过", PreflightLevel.Ok, "", "", "");

        if (i.LastRunOk)
        {
            return new PreflightItem("last.ok", $"上次（{i.LastRunAt}）是成功的", PreflightLevel.Ok, "", "", "");
        }

        string why = string.IsNullOrWhiteSpace(i.LastRunReason) ? "" : $"原因：{i.LastRunReason}。";
        return new PreflightItem("last.fail", $"上次（{i.LastRunAt}）没跑成", PreflightLevel.Notice,
            why + "如果不是偶发，先解决它再跑，不然这一轮大概率一样。",
            "看今天", "view.today");
    }

    private static PreflightItem CheckSchedule(PreflightInput i)
    {
        if (i.ScheduleEnabled)
            return new PreflightItem("schedule.on", "定时开着", PreflightLevel.Ok, "", "", "");
        return new PreflightItem("schedule.off", "定时没开", PreflightLevel.Notice,
            "不开也能跑，只是每天得自己点一次。想让它自己跑就去定个时间。",
            "定个时间", "view.schedule");
    }

    // ===== 通知渠道 =====

    /// <summary>本地渠道（不依赖网络与地址）。</summary>
    public static bool IsLocalChannel(string? mode)
        => mode is null or "" or "Toast" or "Balloon";

    /// <summary>
    /// 从设置里取当前渠道要用的地址/凭据。多个字段拼起来（Telegram 要 token+chat，
    /// 邮件要 host+收件人），返回空串表示这个渠道不需要或没配。
    /// </summary>
    public static string NotifyEndpointOf(string? mode, AppSettings? s)
    {
        if (s == null) return "";
        return mode switch
        {
            "Wecom" => s.NotifyWecomUrl ?? "",
            "Feishu" => s.NotifyFeishuUrl ?? "",
            "DingTalk" => s.NotifyDingUrl ?? "",
            "Telegram" => Join(s.NotifyTelegramToken, s.NotifyTelegramChat),
            "Bark" => s.NotifyBarkUrl ?? "",
            "Email" => Join(s.NotifyEmailHost, s.NotifyEmailTo),
            "Xxtui" => s.NotifyXxtuiUrl ?? "",
            "Webhook" => s.NotifyWebhookUrl ?? "",
            "WebSocket" => s.NotifyWebSocketUrl ?? "",
            _ => "",
        };
    }

    private static string Join(string? a, string? b) => (a ?? "").Trim() + "|" + (b ?? "").Trim();

    /// <summary>
    /// 这个渠道配好了吗。本地渠道永远算配好；远程渠道要求的每一段都不能是空的。
    /// <paramref name="endpoint"/> 由 <see cref="NotifyEndpointOf"/> 取得。
    /// </summary>
    public static bool NotifyReady(string? mode, string? endpoint)
    {
        if (IsLocalChannel(mode)) return true;
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        var parts = endpoint.Split('|');
        if (parts.Length <= 1) return endpoint.Trim().Length > 0;
        return parts.All(p => p.Trim().Length > 0);
    }
}
