using System;

namespace DragonButler;

/// <summary>
/// 「你走了就开跑」（v1.20 / 奇思妙想 #2）的判定输入采集。
///
/// <para>
/// 逻辑全在 <see cref="AwaySense"/>（纯函数、可断言）；这里只负责问系统两件事：
/// 键鼠闲置了多久、前台窗口是不是游戏。两处都容错：读不到就当「你在」，
/// 宁可错过一次自动开跑，也不要在你正玩的时候抢手柄。
/// </para>
/// </summary>
public static class AwayWatcher
{
    /// <summary>测试注入：非 null 时直接用它当闲置时长。</summary>
    public static TimeSpan? IdleOverride { get; set; }

    /// <summary>测试注入：非 null 时直接用它当前台是否是游戏。</summary>
    public static bool? ForegroundGameOverride { get; set; }

    private static AwaySense.Snapshot? _snapshot;

    /// <summary>最近一次判定。界面只读它，不主动采样。</summary>
    public static AwaySense.Snapshot? Snapshot => _snapshot;

    /// <summary>当前闲置时长。委托 <see cref="IdleGuard.IdleFor"/>（同一套 P/Invoke，不重复写）。</summary>
    public static TimeSpan Idle
    {
        get
        {
            if (IdleOverride.HasValue) return IdleOverride.Value;
            try { return IdleGuard.IdleFor; }
            catch { return TimeSpan.Zero; }
        }
    }

    /// <summary>
    /// 前台窗口是不是游戏相关的进程。判据用进程名包含关系，避免维护一份随时过期的进程名单。
    /// </summary>
    public static bool ForegroundIsGame()
    {
        if (ForegroundGameOverride.HasValue) return ForegroundGameOverride.Value;
        try
        {
            var name = ForegroundProcessName();
            if (string.IsNullOrEmpty(name)) return false;
            return name.Contains("yuanshen", StringComparison.OrdinalIgnoreCase)
                || name.Contains("genshin", StringComparison.OrdinalIgnoreCase)
                || name.Contains("starrail", StringComparison.OrdinalIgnoreCase)
                || name.Contains("zenless", StringComparison.OrdinalIgnoreCase)
                || name.Contains("BetterGenshin", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>采一次样、推进状态机。由 <see cref="Tick"/> 每 30 秒调一行。</summary>
    public static AwaySense.Snapshot Sample()
    {
        var idle = Idle;
        bool game = ForegroundIsGame();
        bool away = AwaySense.LooksAway(idle, game);
        _snapshot = AwaySense.Step(_snapshot, away, idle, game);
        return _snapshot;
    }

    /// <summary>清空（设置关掉、或者用户手动点了「现在就跑」之后）。</summary>
    public static void Reset() => _snapshot = null;

    private static string ForegroundProcessName()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "";
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == 0) return "";
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName ?? "";
        }
        catch { return ""; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
