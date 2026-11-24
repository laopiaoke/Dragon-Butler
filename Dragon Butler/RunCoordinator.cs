using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DragonButler;

/// <summary>单次工具执行的状态。</summary>
public enum ToolRunState
{
    /// <summary>空闲，尚未开始。</summary>
    Idle = 0,

    /// <summary>正在拉起进程。</summary>
    Launching = 1,

    /// <summary>进程已启动，等待主信号。</summary>
    Running = 2,

    /// <summary>主信号已到，正在等副信号互证。</summary>
    Resolving = 3,

    /// <summary>终态：确认完成。</summary>
    Done = 4,

    /// <summary>终态：启动失败或异常退出。</summary>
    Failed = 5,

    /// <summary>终态：超过硬顶时长。</summary>
    Timeout = 6,

    /// <summary>终态：悬置（无法确定，已通知，绝不自动推进）。</summary>
    Halted = 7,

    /// <summary>终态：疑似挂起（进程存活但长时间无 CPU/日志活动），队列暂停等人。</summary>
    Stalled = 8,
}

/// <summary>
/// 仲裁状态机：把"启动 → 运行 → 主信号 → 副信号互证"收成一条固定路径。
///
/// 铁律【宁等勿进】：任何无法确定的分支，出口只能是 <see cref="ToolRunState.Halted"/> + 通知，
/// 禁止猜测性推进（例如主信号到了但进程没退、副信号也没来，就停在这里等人）。
/// </summary>
public sealed class RunCoordinator : IDisposable
{
    private readonly ToolProfile _profile;
    private readonly string _runId;
    private readonly Action<string>? _notify;
    private readonly List<ICompletionSignalSource> _secondary = new();
    private readonly object _gate = new();

    private Timer? _maxTimer;
    private Timer? _graceTimer;
    private ICompletionSignalSource? _primary;
    private ToolRunState _state = ToolRunState.Idle;

    // ===== v1.43 / 任务一：把「已经知道的事实」锁存下来，别让后来的信号把它翻转 =====
    //
    // 这三个字段存在的理由，是原来那段 OnSignal 只读当前状态、不记事实：
    // 非零退出被忽略之后什么都没留下，于是下一个副信号（游戏退出 / Webhook）就把
    // 状态推进了 Done。判定看的是「到过什么信号」，而事实是「进程用几退出的、
    // 主信号到底来没来」。事实必须攒住，否则谁最后到谁说话。
    private bool _primaryArrived;
    private bool _secondarySeen;
    private string? _exitFailure;

    /// <summary>状态迁移回调：(旧状态, 新状态, 原因)。</summary>
    public event Action<ToolRunState, ToolRunState, string>? Transition;

    /// <summary>创建一个仲裁器（不会自动启动）。</summary>
    /// <param name="profile">本次执行的工具档案。</param>
    /// <param name="notify">需要提醒用户时的回调（Halted 用）。</param>
    public RunCoordinator(ToolProfile profile, Action<string>? notify = null)
    {
        _profile = profile;
        _notify = notify;
        _runId = Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>当前状态。</summary>
    public ToolRunState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>本次运行编号（日志用）。</summary>
    public string RunId => _runId;

    /// <summary>是否已进入终态。</summary>
    public bool IsFinished => State is ToolRunState.Done or ToolRunState.Failed
        or ToolRunState.Timeout or ToolRunState.Halted or ToolRunState.Stalled;

    /// <summary>注册主信号源（同一时刻只允许一个）。</summary>
    public void SetPrimary(ICompletionSignalSource source)
    {
        _primary = source;
        source.SignalRaised += OnSignal;
    }

    /// <summary>注册副信号源。</summary>
    public void AddSecondary(ICompletionSignalSource source)
    {
        _secondary.Add(source);
        source.SignalRaised += OnSignal;
    }

    /// <summary>进入 Launching。</summary>
    public void BeginLaunch() => Move(ToolRunState.Launching, "开始启动 " + _profile.DisplayName);

    /// <summary>进程启动成功：Launching → Running，并启动超时计时。</summary>
    public void LaunchSucceeded(Process process, ToolRunContext ctx)
    {
        Move(ToolRunState.Running, "进程已启动 pid=" + SafePid(process));
        _primary?.Start(ctx);
        foreach (var s in _secondary) s.Start(ctx);
        StartMaxTimer();
    }

    /// <summary>进程启动失败：Launching → Failed。</summary>
    public void LaunchFailed(string reason) => Move(ToolRunState.Failed, "启动失败：" + reason);

    /// <summary>
    /// 疑似挂起：Running → Stalled。队列会因此停止；是否杀进程由 QueueService 按设置决定。
    /// </summary>
    public void MarkStalled(string reason)
    {
        if (State != ToolRunState.Running) return;
        Move(ToolRunState.Stalled, reason);
        LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} 疑似挂起，已通知队列处理");
    }

    /// <summary>手动中止（用户点停止）：Running/Resolving → Failed（不计完成）。</summary>
    public void Abort(string reason = "用户中止")
    {
        if (IsFinished) return;
        Move(ToolRunState.Failed, reason);
    }

    private void OnSignal(CompletionSignal signal)
    {
        ToolRunState state;
        lock (_gate) state = _state;

        if (state == ToolRunState.Running)
        {
            // v1.43 / 任务一：进程退出是客观事实，跟「它是不是主信号」无关。
            //
            // 以前这里先判 `signal.Kind != _profile.PrimarySignal` 就整体 return，
            // 于是主信号是 LogPattern / Webhook 的工具一旦崩溃退出，那条 ProcessExit0
            // 被当成「非主信号」扔掉，队列只能干等到 120 分钟硬顶才判超时。
            // 一个人半夜看到的是「跑了两个小时超时」，其实三分钟前进程就死了。
            if (signal.Kind == CompletionSignalKind.ProcessExit0)
            {
                int code = signal.ExitCode ?? -1;
                if (code != 0)
                {
                    LatchExitFailure($"进程异常退出 exit={code}");
                    Move(ToolRunState.Failed, "进程异常退出 exit=" + code);
                    return;
                }

                _secondarySeen = true;
                if (signal.Kind == _profile.PrimarySignal)
                {
                    Move(ToolRunState.Done, "进程正常退出 exit=0");
                    return;
                }

                // 副信号语义下 exit=0 只说明「进程正常结束了」，不证明任务跑成了：
                // 主信号是日志锚点 / 时长 / Webhook 的工具，退出码 0 也可能什么都没做。
                // 按【宁等勿进】，转 Resolving 等主信号；宽限到了就走 Halted。
                Move(ToolRunState.Resolving, "进程已退出 exit=0，等待主信号互证");
                StartGraceTimer();
                return;
            }

            if (signal.Kind != _profile.PrimarySignal)
            {
                LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} 忽略非主信号 {signal.Kind}（{signal.SourceId}）");
                _secondarySeen = true;
                return;
            }

            if (signal.Kind == CompletionSignalKind.Duration)
            {
                _primaryArrived = true;
                Move(ToolRunState.Done, "已到达设定时长");
                return;
            }

            _primaryArrived = true;
            Move(ToolRunState.Resolving, "主信号 " + signal.Kind + " 到达，等待副信号互证");
            StartGraceTimer();
            return;
        }

        if (state == ToolRunState.Resolving)
        {
            // 【宁等勿进】：一旦锁存了失败证据，后面的信号只能补充诊断，不许授权完成。
            // 原来是「忽略非 0 退出、紧接着就用别的副信号翻 Done」——等于把崩溃当成功。
            if (_exitFailure != null)
            {
                LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} 已有失败证据（{_exitFailure}），忽略后续信号 {signal.Kind}（{signal.SourceId}）");
                return;
            }

            if (signal.Kind == CompletionSignalKind.ProcessExit0)
            {
                int code = signal.ExitCode ?? -1;
                if (code != 0)
                {
                    LatchExitFailure($"进程异常退出 exit={code}");
                    Move(ToolRunState.Failed, "进程异常退出 exit=" + code);
                    return;
                }
                _secondarySeen = true;
                LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} 记下进程零退出作为互证");
            }
            else if (signal.Kind == _profile.PrimarySignal)
            {
                if (_primaryArrived) return; // 主信号重复上报
                _primaryArrived = true;
            }
            else
            {
                _secondarySeen = true;
            }

            // 完成需要「主信号 + 至少一个副信号」两份独立证据。
            // 只有其中一份时继续等——宽限计时器在跑，到点会 Halt，不会静默成功。
            if (_primaryArrived && _secondarySeen)
            {
                Move(ToolRunState.Done, "主信号与副信号互证完成");
                return;
            }

            LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} 收到 {signal.Kind}（{signal.SourceId}），证据还不齐：主信号={_primaryArrived} 副信号={_secondarySeen}");
            return;
        }

        // 其它状态到达的信号一律忽略（终态后不再变化）
    }

    /// <summary>
    /// v1.43 / 任务一：锁存「进程非零退出」这个事实。
    ///
    /// <para>
    /// 单独拆一个方法是因为它有两个用途：一是立刻判失败，二是万一以后有人
    /// 想加「失败后等宽限再申诉」之类的逻辑，读这一个字段就知道该不该放行。
    /// 散在几处赋值迟早会有人漏掉一处，而漏掉的那处正是「崩溃被当成功」。
    /// </para>
    /// </summary>
    private void LatchExitFailure(string reason)
    {
        _exitFailure = reason;
        LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} 锁存失败证据：{reason}");
    }

    private void StartMaxTimer()
    {
        int minutes = _profile.MaxDurationMinutes > 0 ? _profile.MaxDurationMinutes : 120;
        _maxTimer = new Timer(_ => Move(ToolRunState.Timeout, $"超过硬顶 {minutes} 分钟"), null,
            TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
    }

    private void StartGraceTimer()
    {
        // 只有一份宽限计时。进程先退、主信号后到时都会调到这里，
        // 不做这个保护就会留下一个没人管的旧 Timer：它照样到点开火，
        // 把还在等证据的这一次提前判成 Halted。
        try { _graceTimer?.Dispose(); } catch { }
        _graceTimer = null;

        int grace = _profile.PostExitGraceMinutes;
        if (grace <= 0)
        {
            Halt("主信号到达但宽限为 0，无法互证");
            return;
        }
        _graceTimer = new Timer(_ => Halt(GraceExpiredReason(grace)), null,
            TimeSpan.FromMinutes(grace), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// v1.43 / 任务一：宽限到期时把「到底缺什么」说清楚。
    ///
    /// <para>
    /// 一律写「副信号未到」会误导：进程可能早就 exit=0 了，缺的是主信号。
    /// 排障的人看到「进程未退出」会去查进程，其实是日志锚点没命中。
    /// </para>
    /// </summary>
    private string GraceExpiredReason(int minutes)
    {
        if (_exitFailure != null) return $"宽限 {minutes} 分钟耗尽，此前已发现{_exitFailure}";
        if (_secondarySeen && !_primaryArrived) return $"宽限 {minutes} 分钟耗尽，进程已退出但主信号未到";
        if (_primaryArrived && !_secondarySeen) return $"宽限 {minutes} 分钟耗尽，主信号已到但进程未退出";
        return $"宽限 {minutes} 分钟耗尽，主信号与副信号都未到";
    }

    private void Halt(string reason)
    {
        Move(ToolRunState.Halted, reason);
        // 文案过去固定写「报告完成但未完全退出」——可真实的悬置也可能是
        // 「进程早退了、完成信号一直没来」。把 reason 带出去，人才知道该去查什么。
        try { _notify?.Invoke($"「{_profile.DisplayName}」这次没法确认结果，已暂停等你处理：{reason}"); }
        catch { }
    }

    private void Move(ToolRunState to, string reason)
    {
        ToolRunState from;
        lock (_gate)
        {
            if (_state == to) return;
            if (_state is ToolRunState.Done or ToolRunState.Failed or ToolRunState.Timeout
                or ToolRunState.Halted or ToolRunState.Stalled) return;
            from = _state;
            _state = to;
        }

        // 埋点：时间戳由 LogService 统一加，这里带上工具名与迁移方向
        LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} {from}→{to}（{reason}）");
        try { Transition?.Invoke(from, to, reason); } catch { }

        if (to is ToolRunState.Done or ToolRunState.Failed or ToolRunState.Timeout
            or ToolRunState.Halted or ToolRunState.Stalled)
            StopTimers();
    }

    private void StopTimers()
    {
        try { _maxTimer?.Dispose(); } catch { }
        try { _graceTimer?.Dispose(); } catch { }
        _maxTimer = null;
        _graceTimer = null;
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    /// <summary>停止全部信号源与计时器。</summary>
    public void Dispose()
    {
        try { _primary?.Stop(); } catch { }
        foreach (var s in _secondary) { try { s.Stop(); } catch { } }
        StopTimers();
    }
}
