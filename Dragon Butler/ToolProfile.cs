using System;
using System.Collections.Generic;

namespace DragonButler;

/// <summary>
/// 主完成信号类型（v1.3 ToolProfile）。
/// </summary>
public enum CompletionSignalKind
{
    /// <summary>工具自己上报的本地 webhook 事件。</summary>
    Webhook = 0,

    /// <summary>进程退出且退出码为 0。</summary>
    ProcessExit0 = 1,

    /// <summary>关联游戏进程退出。</summary>
    GameProcessExit = 2,

    /// <summary>日志中出现约定的完成标记。</summary>
    LogPattern = 3,

    /// <summary>到达设定时长即视为完成（自定义工具的“到达时长”模式）。</summary>
    Duration = 4,
}

/// <summary>
/// 信号置信级别：高=工具明确上报，中=进程/日志推断，低=仅在无更强信号时作参考。
/// </summary>
public enum SignalConfidence
{
    /// <summary>低：辅助判断，不能单独作为完成依据。</summary>
    Low = 0,

    /// <summary>中：进程/日志推断。</summary>
    Medium = 1,

    /// <summary>高：工具主动上报（如 webhook）。</summary>
    High = 2,
}

/// <summary>
/// 单个上游工具的档案：把"启动参数、进程名、完成信号、超时"等工具专属知识收拢在这里。
/// 字段与 v1.3 任务书第二部分第 1 节一一对应，不增不改。
/// </summary>
public sealed class ToolProfile
{
    /// <summary>唯一标识（如 bettergi / march7th / ok-ww）。</summary>
    public string ToolId { get; set; } = "";

    /// <summary>界面显示名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>用户配置的 exe 完整路径。</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>启动参数模板，支持 {组名} 占位符。</summary>
    public string LaunchArguments { get; set; } = "";

    /// <summary>工具自身进程名（不含 .exe）。</summary>
    public string ManagedProcessName { get; set; } = "";

    /// <summary>关联游戏进程名（副信号 + 前置检查）。</summary>
    public string[] GameProcessNames { get; set; } = Array.Empty<string>();

    /// <summary>主完成信号。</summary>
    public CompletionSignalKind PrimarySignal { get; set; } = CompletionSignalKind.ProcessExit0;

    /// <summary>互证副信号。</summary>
    public CompletionSignalKind[] SecondarySignals { get; set; } = Array.Empty<CompletionSignalKind>();

    /// <summary>日志哨兵正则（可空）。</summary>
    public string? LogCompletionPattern { get; set; }

    /// <summary>超时硬顶（分钟）。</summary>
    public int MaxDurationMinutes { get; set; } = 120;

    /// <summary>主信号到达后，等待副信号/退出的宽限（分钟）。</summary>
    public int PostExitGraceMinutes { get; set; }

    /// <summary>失败重试次数（本阶段仅存字段，执行路径不启用）。</summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>工具专属数据（BGI 配置组列表、Webhook 端口等）。</summary>
    public Dictionary<string, string> CustomData { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>深拷贝一份，避免运行期修改污染注册表缓存。</summary>
    public ToolProfile Clone()
    {
        var copy = (ToolProfile)MemberwiseClone();
        copy.GameProcessNames = (string[])GameProcessNames.Clone();
        copy.SecondarySignals = (CompletionSignalKind[])SecondarySignals.Clone();
        copy.CustomData = new Dictionary<string, string>(CustomData, StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}

/// <summary>v1.3 内置的三个工具档案（本阶段只建数据，仅 bettergi 接 UI）。</summary>
public static class ToolProfileDefaults
{
    /// <summary>内置档案清单（每次返回新实例，避免调用方改到默认值）。</summary>
    public static ToolProfile[] CreateAll() => new[]
    {
        new ToolProfile
        {
            ToolId = "bettergi",
            DisplayName = "BetterGI",
            LaunchArguments = "startOneDragon \"{组名}\"",
            ManagedProcessName = "BetterGI",
            GameProcessNames = new[] { "YuanShen", "GenshinImpact" },
            // 勘误 F：BGI 主信号改为日志锚点；副信号三选一互证即可
            // v1.3.1：追加“进程退出”——BGI 跑完不关游戏且没配 Webhook 时，靠它避免悬置
            PrimarySignal = CompletionSignalKind.LogPattern,
            SecondarySignals = new[]
            {
                CompletionSignalKind.GameProcessExit,
                CompletionSignalKind.Webhook,
                CompletionSignalKind.ProcessExit0,
            },
            // 主锚点（真实日志已验证）：严禁裸匹配"任务结束"，BGI 中途会多次打印
            LogCompletionPattern = "一条龙和配置组任务结束",
            MaxDurationMinutes = 180,
            PostExitGraceMinutes = 5,
            RetryCount = 1,
            CustomData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["webhookPort"] = "18235",
                ["dragonConfigName"] = "",
                ["selectedGroups"] = "",
                ["candidatePaths"] = "",   // 待人工核对：未确证官方默认安装位置，留空不臆造
                ["extraLogPatterns"] = "", // 备用锚点（| 分隔），默认为空
                ["secondarySchema"] = "2", // 副信号结构版本（v1.3.1 起为 2：含 ProcessExit0）
            },
        },
        new ToolProfile
        {
            ToolId = "march7th",
            DisplayName = "三月七助手",
            LaunchArguments = "main -e",
            ManagedProcessName = "March7 Launcher",
            GameProcessNames = new[] { "StarRail" },
            PrimarySignal = CompletionSignalKind.ProcessExit0,
            SecondarySignals = new[] { CompletionSignalKind.GameProcessExit },
            LogCompletionPattern = null,
            MaxDurationMinutes = 120,
            PostExitGraceMinutes = 0,
            RetryCount = 1,
            CustomData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidatePaths"] = "",   // 待人工核对
                ["extraLogPatterns"] = "",
            },
        },
        new ToolProfile
        {
            ToolId = "ok-ww",
            DisplayName = "ok-ww",
            LaunchArguments = "-t 1 -e",
            ManagedProcessName = "ok-ww",
            GameProcessNames = new[] { "Client-Win64-Shipping" },
            PrimarySignal = CompletionSignalKind.ProcessExit0,
            SecondarySignals = new[] { CompletionSignalKind.GameProcessExit },
            LogCompletionPattern = null,
            MaxDurationMinutes = 150,
            PostExitGraceMinutes = 0,
            RetryCount = 1,
            CustomData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidatePaths"] = "",   // 待人工核对
                ["extraLogPatterns"] = "",
            },
        },
    };
}
