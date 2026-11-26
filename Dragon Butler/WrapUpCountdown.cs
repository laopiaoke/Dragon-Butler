using System;

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
    private static bool _sleepInProgress;
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
            if (!QueueService.RequestSystemShutdown(WindowSeconds, out string detail))
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
            if (kind == WrapUpKind.Shutdown) QueueService.MarkShutdownPending(WindowSeconds);
            _timer?.Dispose();
            // 200ms 一跳：界面按秒读 RemainingSeconds，秒数看起来是连续的，
            // 而 60 秒归零那一下不会因为 Timer 抖动晚半秒才执行。
            _timer = new Timer(_ => Tick(), null, 200, 200);
        }

        LogService.LogEvent(kind == WrapUpKind.Shutdown
            ? $"收尾倒计时：{WindowSeconds} 秒后关机（shutdown /s /t {WindowSeconds} 已发出）"
            : $"收尾倒计时：{WindowSeconds} 秒后睡眠（进程内倒计时）");

        NotifyService.Show(NotifyLevel.Warning, "龙管家",
            $"队列完成，{WindowSeconds} 秒后{NameOf(kind)}。想反悔就在主页点「取消」，也可以点「立即执行」。");

        RaiseChanged();
        return true;
    }

    /// <summary>取消这次收尾；系统未确认撤销关机时，保留倒计时。</summary>
    public static bool Cancel() => Cancel(forNewRun: false);

    internal static bool TryCancelForNewRun() => Cancel(forNewRun: true);

    private static bool Cancel(bool forNewRun)
    {
        WrapUpKind kind;
        lock (Gate)
        {
            if (_sleepInProgress)
            {
                if (forNewRun) NotifyService.Show(NotifyLevel.Warning, "龙管家", "电脑正在执行睡眠，这次没有开跑。");
                return false;
            }
            if (!_active)
            {
                // 界面倒计时归零时，系统仍可能在处理关机。
                return forNewRun && (!QueueService.IsShutdownPending || QueueService.CancelShutdown());
            }
            kind = _kind;
            if (kind == WrapUpKind.Shutdown && !QueueService.CancelShutdown()) return false;
            CloseWindow($"收尾已取消（{NameOf(kind)}）");
        }

        if (forNewRun)
            DecisionLog.Add(DecisionSource.System, "收尾自动取消：队列又跑起来了",
                $"新的运行要开始，上一轮的{NameOf(kind)}已取消。");
        else
            DecisionLog.Add(DecisionSource.System, $"收尾已取消（{NameOf(kind)}）",
                $"你取消了队列跑完后的{NameOf(kind)}。");
        if (kind == WrapUpKind.Sleep)
            NotifyService.Show(NotifyLevel.Info, "龙管家", "已取消睡眠，电脑继续运行。");
        RaiseChanged();
        return true;
    }

    /// <summary>不等了，现在就执行。</summary>
    public static bool RunNow()
    {
        WrapUpKind kind;
        string detail = "";
        bool accepted = true;
        lock (Gate)
        {
            if (!_active || QueueService.IsRunning) return false;
            kind = _kind;
            if (kind == WrapUpKind.Shutdown)
            {
                if (!QueueService.CancelShutdown()) return false;
                accepted = QueueService.RequestSystemShutdown(0, out detail);
                if (accepted) QueueService.MarkShutdownPending(0);
            }
            else
            {
                _sleepInProgress = true;
            }
            CloseWindow(accepted ? $"用户确认立即{NameOf(kind)}" : $"立即关机失败：{detail}");
        }

        if (!accepted)
        {
            LogService.LogEvent("立即关机失败：" + detail);
            NotifyService.Show(NotifyLevel.Warning, "龙管家", $"没能立即关机（{detail}）。电脑还开着。");
            RaiseChanged();
            return false;
        }

        DecisionLog.Add(DecisionSource.System, $"用户确认立即{NameOf(kind)}",
            "队列跑完之后你点了「立即执行」，没有等满 60 秒。");
        if (kind == WrapUpKind.Sleep) accepted = DoSleep();
        RaiseChanged();
        return accepted;
    }

    private static void Tick()
    {
        bool expired;
        WrapUpKind kind;
        lock (Gate)
        {
            if (!_active) return;
            var remain = (_deadline - DateTime.Now).TotalSeconds;
            expired = remain <= 0;
            kind = _kind;
            if (expired)
            {
                _sleepInProgress = kind == WrapUpKind.Sleep;
                CloseWindow($"倒计时结束自动{NameOf(kind)}");
            }
            else
            {
                int left = (int)Math.Ceiling(remain);
                if (left != _lastNotifiedSecond)
                    _lastNotifiedSecond = left;
                else
                    return;
            }
        }

        if (!expired)
        {
            try { Changed?.Invoke(); } catch { }
            return;
        }

        LogService.LogEvent($"收尾倒计时：60 秒走完，执行{NameOf(kind)}");
        DecisionLog.Add(DecisionSource.System, $"倒计时结束自动{NameOf(kind)}",
            $"队列跑完之后等了 60 秒，没有人取消，就按设置里的收尾动作{NameOf(kind)}了。");
        if (kind == WrapUpKind.Sleep) DoSleep();
        RaiseChanged();
    }

    // 调用时持有 Gate，取消和到期只能有一方拿走这次动作。
    private static void CloseWindow(string reason)
    {
        _active = false;
        _lastEndReason = reason;
        _timer?.Dispose();
        _timer = null;
    }

    private static bool DoSleep()
    {
        try
        {
            if (AfterRunActionService.TrySleep(out string detail))
            {
                LogService.LogEvent("已进入睡眠");
                return true;
            }
            LogService.LogEvent("睡眠失败：" + detail);
            NotifyService.Show(NotifyLevel.Warning, "龙管家", "要睡眠的时候系统没答应，电脑还醒着。");
            DecisionLog.Add(DecisionSource.System, "睡眠没成功",
                $"系统拒绝进入睡眠（{detail}）。电脑还醒着。");
            return false;
        }
        finally
        {
            lock (Gate) _sleepInProgress = false;
        }
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
        try { QueueService.NotifyShutdownStateChanged(); } catch { }
    }
}
