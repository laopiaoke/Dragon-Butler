using System;
using System.Collections.Generic;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 命令面板的数据与匹配逻辑（v1.8 / F15）。
///
/// 为什么值得做：这个软件的功能已经多到需要「找」了——
/// 定时、队列、每张工具卡、高级设置里的十几个开关、日志、周报、浮条、快照……
/// 用户记得「有个能复制令牌的地方」，但不知道该点开哪一层。
/// Ctrl+K 之后敲两个字就能到，这是把「导航成本」压到接近零的唯一办法。
///
/// 匹配保持朴素：子串 + 关键词前缀，不引拼音库、不做模糊打分。
/// 一个只有三十来条命令的面板，模糊匹配带来的收益远小于它引入的「为什么这条排前面」的困惑。
/// </summary>
public static class CommandRegistry
{
    /// <summary>一条命令：id 稳定（用于断言），标题给人看，关键字补充同义词。</summary>
    public sealed record Command(string Id, string Title, string Group, string Keywords)
    {
        /// <summary>匹配用的整串：标题 + 组 + 关键字，全部小写。</summary>
        public string Haystack { get; } = (Title + " " + Group + " " + Keywords).ToLowerInvariant();
    }

    /// <summary>
    /// 全部命令。手工维护的一张表——命令面板的价值全在这张表准不准，
    /// 让它跟着界面自动生成反而会埋进一堆用户根本不想搜的东西。
    /// </summary>
    public static IReadOnlyList<Command> All { get; } = new List<Command>
    {
        new("run.queue",      "一键开跑（整条队列）",   "运行", "开跑 启动 队列 start run"),
        new("run.continue",   "接着跑（今天没跑完的）", "运行", "继续 断点 resume continue"),
        new("run.stop",       "停止当前队列",           "运行", "停止 中断 stop"),
        new("run.single",     "单跑某个工具",           "运行", "单个 单项 single"),

        new("view.schedule",  "打开定时设置",           "导航", "定时 计划 schedule timer"),
        new("view.queue",     "打开队列",               "导航", "队列 queue 顺序"),
        new("view.tools",     "打开工具卡",             "导航", "工具 tools 卡片"),
        new("view.advanced",  "打开高级设置",           "导航", "高级 设置 settings advanced"),
        new("view.log",       "打开日志",               "导航", "日志 log 记录"),
        new("view.calendar",  "挂机日历",               "导航", "日历 calendar 历史 heatmap"),
        new("view.today",     "今日运行",               "导航", "今天 今日 耗时 归档 today today run"),
        new("view.depends",   "依赖编排",               "导航", "依赖 dag 顺序 depends"),
        new("view.groups",    "配置组切片",             "导航", "配置组 切片 groups slice"),

        new("tool.copyToken", "复制控制 API 令牌",      "控制", "令牌 token api 复制"),
        new("tool.apiToggle", "开关本机控制 API",       "控制", "api 控制 开关"),
        new("tool.lanToggle", "开关局域网面板",         "控制", "局域网 面板 手机 lan panel"),
        new("tool.snapshot",  "现在就到（抓一张画面）", "控制", "截图 快照 抓屏 snapshot capture"),

        new("ui.miniBar",     "开关迷你浮条",           "外观", "浮条 迷你 minibar 小窗"),
        new("ui.theme",       "切换深浅主题",           "外观", "主题 深色 浅色 theme dark"),
        new("ui.hotkey",      "设置全局热键",           "外观", "热键 快捷键 hotkey 全局"),

        new("sys.openFolder", "打开数据目录",           "系统", "目录 文件夹 日志 数据 folder"),
        new("sys.check",      "立即检查更新",           "系统", "更新 版本 update"),
        new("sys.about",      "关于与版本",             "系统", "关于 版本 about version"),
    };

    /// <summary>
    /// 按子串过滤。空查询返回全部（面板刚打开时该看到全部命令）。
    /// 排序规则：标题命中排在关键字命中之前——用户打的字更可能出现在标题里，
    /// 让「跑」优先命中带「跑」的标题，而不是靠关键字蹭进来的命令。
    /// </summary>
    /// <summary>
    /// 按当前已配置的工具，临时生成「单跑这个工具」的命令。
    /// 不写进 All：工具是用户自己加的，未配置的电脑上不该出现这些条目。
    /// </summary>
    public static List<Command> ToolCommands(IEnumerable<(string ToolId, string DisplayName)> tools)
    {
        var list = new List<Command>();
        foreach (var (id, name) in tools)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new Command("run.tool:" + id, "单跑：" + name, "运行", "单个 single run tool " + id));
        }
        return list;
    }

    public static List<Command> Search(string? query, int limit = 8) => Search(All, query, limit);

    /// <summary>
    /// 同一套匹配规则，但可以喂一份临时清单——
    /// 「单跑某个工具」那种按当前配置动态生成的候选就靠这个重载。
    /// </summary>
    public static List<Command> Search(IReadOnlyList<Command> source, string? query, int limit = 8)
    {
        var all = source;
        if (string.IsNullOrWhiteSpace(query))
            return all.Take(Math.Max(limit, 0)).ToList();

        string q = query.Trim().ToLowerInvariant();

        var titleHits = new List<Command>();
        var otherHits = new List<Command>();
        foreach (var cmd in all)
        {
            if (cmd.Title.ToLowerInvariant().Contains(q, StringComparison.Ordinal))
                titleHits.Add(cmd);
            else if (cmd.Haystack.Contains(q, StringComparison.Ordinal))
                otherHits.Add(cmd);
        }

        var result = new List<Command>(titleHits.Count + otherHits.Count);
        result.AddRange(titleHits);
        result.AddRange(otherHits);
        if (limit > 0 && result.Count > limit) result = result.Take(limit).ToList();
        return result;
    }
}
