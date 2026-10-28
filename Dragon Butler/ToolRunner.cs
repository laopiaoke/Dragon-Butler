using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DragonButler;

/// <summary>
/// 通用工具执行器：按 <see cref="ToolProfile"/> 启动/结束进程、拼装参数。
/// 不含任何 BGI 专属知识（进程名、参数、信号全部来自 profile）。
/// </summary>
public static class ToolRunner
{
    /// <summary>
    /// 按模板拼装启动参数：把 <c>{组名}</c> 替换为实际值；为空时连引号一起去掉。
    /// </summary>
    public static string BuildArguments(ToolProfile profile, string? groupName)
    {
        string template = profile.LaunchArguments ?? "";
        if (!template.Contains("{组名}", StringComparison.Ordinal)) return template.Trim();

        if (string.IsNullOrWhiteSpace(groupName))
            return template.Replace(" \"{组名}\"", "", StringComparison.Ordinal)
                           .Replace("{组名}", "", StringComparison.Ordinal)
                           .Trim();

        return template.Replace("{组名}", groupName, StringComparison.Ordinal).Trim();
    }

    /// <summary>该 profile 对应的 exe 是否已配置且存在。</summary>
    public static bool HasExecutable(ToolProfile profile)
        => !string.IsNullOrWhiteSpace(profile.ExecutablePath) && File.Exists(profile.ExecutablePath);

    /// <summary>本会话内该工具的进程（SessionId 过滤）。</summary>
    public static Process[] GetSessionProcesses(ToolProfile profile)
        => string.IsNullOrWhiteSpace(profile.ManagedProcessName)
            ? Array.Empty<Process>()
            : ProcessTools.GetSessionProcesses(profile.ManagedProcessName);

    /// <summary>该工具是否正在运行。</summary>
    public static bool IsRunning(ToolProfile profile) => GetSessionProcesses(profile).Length > 0;

    /// <summary>结束本会话内该工具的全部进程（Kill + 最多等 3 秒）。</summary>
    public static void KillAll(ToolProfile profile)
    {
        foreach (var p in GetSessionProcesses(profile))
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch { }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>启动进程（调用方负责异常处理与日志）；失败抛异常。</summary>
    public static Process? StartProcess(ToolProfile profile, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = profile.ExecutablePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(profile.ExecutablePath) ?? "",
            UseShellExecute = true,
            Verb = "runas",
        };
        return Process.Start(psi);
    }

    /// <summary>
    /// 装配一次带仲裁的执行（Phase 2 排队引擎的入口；Phase 1 由替身测试驱动）。
    /// 调用方拿到协调器后自行调用 <see cref="RunCoordinator.BeginLaunch"/> 等。
    /// </summary>
    public static RunCoordinator CreateCoordinator(ToolProfile profile, Action<string>? notify = null)
    {
        var coord = new RunCoordinator(profile, notify);
        coord.SetPrimary(CreatePrimarySource(profile));
        foreach (var kind in profile.SecondarySignals.Distinct())
            coord.AddSecondary(CreateSource(kind));
        return coord;
    }

    /// <summary>按信号类别创建信号源实例。</summary>
    public static ICompletionSignalSource CreateSource(CompletionSignalKind kind) => kind switch
    {
        CompletionSignalKind.Webhook => new WebhookSignalSource(),
        CompletionSignalKind.GameProcessExit => new GameProcessExitSignalSource(),
        CompletionSignalKind.LogPattern => new LogPatternSignalSource(),
        _ => new ProcessExitSignalSource(),
    };

    private static ICompletionSignalSource CreatePrimarySource(ToolProfile profile)
        => CreateSource(profile.PrimarySignal);
}
