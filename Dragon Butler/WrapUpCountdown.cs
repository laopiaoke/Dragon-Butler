using System;
using System.Diagnostics;

namespace DragonButler;

/// <summary>收尾动作的种类。</summary>
public enum WrapUpKind
{
    /// <summary>睡眠（Windows 没有「延迟睡眠 + 可中止」的命令，所以在本进程里倒计时）。</summary>
    Sleep = 0,

    /// <summary>关机（发出 shutdown /s /t 60，由系统自己倒计时，进程被杀也照样关）。</summary>
    Shutdown = 1,
}

/// <summary>
/// v1.44 / 修复 #4（D19）：收尾动作的 60 秒反悔窗口。
///
/// <para>
/// 存在的理由：睡眠和关机都是不可逆的。以前队列一跑完就直接执行，
/// 人看到「队列完成」的时候电脑已经开始关了——想反悔的窗口是零。
/// 现在给 60 秒，看得见、能取消、也能立刻执行。
/// </para>
/// <para>
/// 两条路径的实现不一样，因为它们的安全性相反：
/// 关机用 <c>shutdown /s /t 60</c>，倒计时在系统里跑——本程序崩了、被杀了，
/// 关机照样会发生，不会出现「以为在倒计时、其实早就不算了」；
/// 睡眠没有对应命令，只能在进程内数 60 秒——本程序死了，倒计时自然失效，
/// 机器保持唤醒。<b>失效方向必须是安全的</b>：宁可少睡一次，不能误睡。
/// </para>
/// </summary>
public static class WrapUpCountdown
{
    /// <summary>反悔窗口。任务书明确说不做配置项——这个数字不该出现在设置里。</summary>
    public const int WindowSeconds = 60;

    private static readonly object Gate = new();
    private static Timer? _timer;
    private static WrapUpKind _kind;
    private static DateTime _deadline;
    private static bool _active;
    private static string? _lastEndReason;
    // 上一次通知界面时剩下几秒。定时器 200ms 一跳，但界面按秒刷新就够——
    // 不记这个的话每秒会把整页重算五次，而已有的 uiRefreshTimer 也在跑。
    private static int _lastNotifiedSecond = -1;

    /// <summary>倒计时状态变化（开始 / 取消 / 执行 / 每秒刷新）。界面订阅它刷新提示条。</summary>
    public static event Action? Changed;

    /// <summary>现在是不是正处在反悔窗口里。</summary>
    public static bool IsActive
    {
        get { lock (Gate) return _active; }
    }

    /// <summary>正在倒计时的是哪一种收尾动作；没在倒计时就是 null。</summary>
    public static WrapUpKind? ActiveKind
    {
        get { lock (Gate) return _active ? _kind : null; }
    }

    /// <summary>还剩几秒（向上取整，最小 0）。没在倒计时就是 0。</summary>
    public static int RemainingSeconds
    {
        get
        {
            lock (Gate)
            {
                if (!_active) return 0;
                double left = (_deadline - DateTime.Now).TotalSeconds;
                return left <= 0 ? 0 : (int)Math.Ceiling(left);
            }
        }
    }

    /// <summary>上一次收尾是怎么结束的（给界面和日志用）；null 表示还没结束过。</summary>
    public static string? LastEndReason
    {
        get { lock (Gate) return _lastEndReason; }
    }

    /// <summary>「60 秒后关机」里的「关机」/「睡眠」。</summary>
    public static string NameOf(WrapUpKind kind) => kind == WrapUpKind.Sleep ? "睡眠" : "关机";

    /// <summary>
    /// 打开反悔窗口。已经在倒计时就不重复开（免得一个队列收尾弹出两个窗口）。
    /// </summary>
    /// <returns>窗口是否已经打开。</returns>
    public static bool Start(WrapUpKind kind)
    {
        lock (Gate)
        {
            if (_active)
            {
                LogService.LogEvent($"收尾倒计时：已经在倒计时 {NameOf(_kind)}，忽略这次 {NameOf(kind)}");
                return false;
            }
        }

        // 关机走系统级倒计时：命令先发出去，系统自己数 60 秒。
        // 必须等它退出再按退出码判断——Process.Start 返回非空只说明进程起来了，
        // 不代表系统接受了这次关机请求。没接受就压根没有倒计时，
        // 这时候开一个「60 秒后关机」的提示条就是骗人。
        if (kind == WrapUpKind.Shutdown)
        {
            if (!QueueService.RequestSystemShutdown(out string detail))
            {
                LogService.LogEvent($"关机失败：{detail}");
                NotifyService.Show(NotifyLevel.Warning, "龙管家", $"该关机了，但没能排上（{detail}）。电脑还开着。");
                DecisionLog.Add(DecisionSource.System, "关机没能排上",
                    $"系统没有接受这次关机请求（{detail}）。电脑还开着，今晚不会自己关。");
                return false;
            }
        }

        lock (Gate)
        {
            _kind = kind;
            _active = true;
            _lastEndReason = null;
            _deadline = DateTime.Now.AddSeconds(WindowSeconds);
            _lastNotifiedSecond = WindowSeconds;
            _timer?.Dispose();
            // 200ms 一跳：界面按秒读 RemainingSeconds，秒数看起来是连续的，
            // 而 60 秒归零那一下不会因为 Timer 抖动晚半秒才执行。
            _timer = new Timer(_ => Tick(), null, 200, 200);
        }

        LogService.LogEvent(kind == WrapUpKind.Shutdown
            ? $"收尾倒计时：{WindowSeconds} 秒后关机（shutdown /s /t {WindowSeconds} 已发出）"
            : $"收尾倒计时：{WindowSeconds} 秒后睡眠（进程内倒计时）");

        if (kind == WrapUpKind.Shutdown)
        {
            // 系统的倒计时已经跑起来了，界面得知道有这么一个待关机状态。
            QueueService.MarkShutdownPending();
        }

        NotifyService.Show(NotifyLevel.Warning, "龙管家",
            $"队列完成，{WindowSeconds} 秒后{NameOf(kind)}。想反悔就在主页点「取消」，也可以点「立即执行」。");

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 取消这次收尾。关机要真的把系统倒计时撤掉才算取消成功。
    /// </summary>
    /// <returns>取消是否成功。失败时窗口保持打开——不能让界面显示「已取消」而系统照样关机。</returns>
    public static bool Cancel()
    {
        WrapUpKind kind;
        lock (Gate)
        {
            if (!_active) return false;
            kind = _kind;
        }

        if (kind == WrapUpKind.Shutdown && !QueueService.CancelShutdown())
        {
            // 撤销失败：状态留在窗口里，用户还能再试一次。
            LogService.LogEvent("收尾倒计时：撤销系统关机失败，窗口保持打开");
            NotifyService.Show(NotifyLevel.Warning, "龙管家",
                "没能撤掉关机计划。倒计时可能还在，请再点一次，或者手动运行 shutdown /a。");
            return false;
        }

        CloseWindow($"收尾已取消（{NameOf(kind)}）");
        DecisionLog.Add(DecisionSource.System, $"收尾已取消（{NameOf(kind)}）",
            $"队列跑完之后本来要{NameOf(kind)}，你在反悔窗口里点了取消，电脑继续开着。");
        NotifyService.Show(NotifyLevel.Info, "龙管家", $"已取消{NameOf(kind)}，电脑继续运行。");
        RaiseChanged();
        return true;
    }

    /// <summary>不等了，现在就执行。</summary>
    /// <returns>是否真的执行了。</returns>
    public static bool RunNow()
    {
        WrapUpKind kind;
        lock (Gate)
        {
            if (!_active) return false;
            kind = _kind;
        }

        CloseWindow($"用户确认立即{NameOf(kind)}");
        LogService.LogEvent($"收尾倒计时：用户点了立即执行（{NameOf(kind)}）");
        DecisionLog.Add(DecisionSource.System, $"用户确认立即{NameOf(kind)}",
            "队列跑完之后你点了「立即执行」，没有等满 60 秒。");

        if (kind == WrapUpKind.Shutdown)
        {
            // 系统那边还挂着 60 秒，先撤掉再立刻关，否则会变成「60 秒后才关」。
            QueueService.CancelShutdown();
            QueueService.ExecuteShutdownNow();
        }
        else
        {
            DoSleep();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// v1.44 / 修复 #4：倒计时窗口里队列又动起来了 → 自动取消。
    ///
    /// <para>
    /// 理论上不该发生（收尾动作是队列结束后才执行的），但「理论上」不是保证：
    /// 定时计划可能刚好到点、用户也可能在倒计时里点了「接着跑」。
    /// 真遇上就取消收尾——<b>宁可少睡不可误睡</b>。
    /// </para>
    /// </summary>
    internal static void AbortIfQueueActive()
    {
        if (!IsActive) return;
        if (!QueueService.IsRunning) return;
        WrapUpKind? kind = ActiveKind;
        if (kind == null) return;
        if (!Cancel()) return;
        DecisionLog.Add(DecisionSource.System, "收尾自动取消：队列又跑起来了",
            $"倒计时还没走完，队列又开始了新的运行，{NameOf(kind.Value)}已取消。");
    }

    /// <summary>队列开始新一轮时清掉残留窗口（防御性，正常路径走 AbortIfQueueActive）。</summary>
    internal static void ResetForNewRun()
    {
        if (!IsActive) return;
        // 队列已经在跑（定时撞上、或者人点了「接着跑」）→ 走带留痕的那条路，
        // 让「今日」里能看见「为什么昨晚没关」；否则只是把窗口收掉。
        if (QueueService.IsRunning)
        {
            AbortIfQueueActive();
            return;
        }
        LogService.LogEvent("收尾倒计时：新一轮队列开始，放弃上次的收尾动作");
        CloseWindow("新一轮队列开始，放弃收尾");
        RaiseChanged();
    }

    private static void Tick()
    {
        bool expired;
        WrapUpKind kind;
        lock (Gate)
        {
            if (!_active) return;
            expired = DateTime.Now >= _deadline;
            kind = _kind;
        }

        if (!expired)
        {
            int left;
            lock (Gate)
            {
                double remain = (_deadline - DateTime.Now).TotalSeconds;
                left = remain <= 0 ? 0 : (int)Math.Ceiling(remain);
            }
            if (left != _lastNotifiedSecond)
            {
                _lastNotifiedSecond = left;
                // 只通知界面，不惊动 QueueService：StateChanged 会带出一整轮重算，
                // 每秒为了一行倒计时抖动五次界面的钱不值。
                try { Changed?.Invoke(); } catch { }
            }
            return;
        }

        CloseWindow($"倒计时结束自动{NameOf(kind)}");
        LogService.LogEvent($"收尾倒计时：60 秒走完，执行{NameOf(kind)}");
        DecisionLog.Add(DecisionSource.System, $"倒计时结束自动{NameOf(kind)}",
            $"队列跑完之后等了 60 秒，没有人取消，就按设置里的收尾动作{NameOf(kind)}了。");

        if (kind == WrapUpKind.Sleep) DoSleep();
        // 关机路径不需要再发一次命令：shutdown /s /t 60 的倒计时本来就在系统里跑，
        // 这里只是把界面上的窗口收掉。
        RaiseChanged();
    }

    private static void CloseWindow(string reason)
    {
        lock (Gate)
        {
            _active = false;
            _lastEndReason = reason;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static void DoSleep()
    {
        try
        {
            LogService.LogEvent("跑完动作：执行睡眠");
            // v1.43 / 任务二：SetSuspendState 是布尔返回值，失败必须说出来。
            // 原来只写「执行睡眠」就完事，日志里看起来像睡成功了，
            // 而人第二天早上发现电脑还亮着，却没有一行能查的记录。
            if (!AfterRunActionService.TrySleep(out string detail))
            {
                LogService.LogEvent("睡眠失败：" + detail);
                NotifyService.Show(NotifyLevel.Warning, "龙管家", "要睡眠的时候系统没答应，电脑还醒着。");
                DecisionLog.Add(DecisionSource.System, "睡眠没成功",
                    $"系统拒绝进入睡眠（{detail}）。电脑还醒着。");
            }
            else
            {
                LogService.LogEvent("已进入睡眠");
            }
        }
        catch (Exception ex)
        {
            LogService.LogEvent("睡眠失败：" + ex.Message);
        }
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
        try { QueueService.NotifyShutdownStateChanged(); } catch { }
    }
}
