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
            if (signal.Kind != _profile.PrimarySignal)
            {
                LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} 忽略非主信号 {signal.Kind}（{signal.SourceId}）");
                return;
            }

            if (signal.Kind == CompletionSignalKind.ProcessExit0)
            {
                int code = signal.ExitCode ?? -1;
                if (code == 0) Move(ToolRunState.Done, "进程正常退出 exit=0");
                else Move(ToolRunState.Failed, "进程异常退出 exit=" + code);
                return;
            }

            if (signal.Kind == CompletionSignalKind.Duration)
            {
                Move(ToolRunState.Done, "已到达设定时长");
                return;
            }

            Move(ToolRunState.Resolving, "主信号 " + signal.Kind + " 到达，等待副信号互证");
            StartGraceTimer();
            return;
        }

        if (state == ToolRunState.Resolving)
        {
            if (signal.Kind == _profile.PrimarySignal && signal.Kind != CompletionSignalKind.ProcessExit0)
                return; // 主信号重复上报，忽略

            // 进程退出作为互证时，只有退出码 0 才算"完成"；非 0 可能是崩溃，
            // 继续等其它副信号，等不到就走宽限 → Halted（宁等勿进）
            if (signal.Kind == CompletionSignalKind.ProcessExit0 && (signal.ExitCode ?? -1) != 0)
            {
                LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} 忽略非 0 退出作为互证（exit={signal.ExitCode}）");
                return;
            }

            Move(ToolRunState.Done, "副信号 " + signal.Kind + " 互证完成");
            return;
        }

        // 其它状态到达的信号一律忽略（终态后不再变化）
    }

    private void StartMaxTimer()
    {
        int minutes = _profile.MaxDurationMinutes > 0 ? _profile.MaxDurationMinutes : 120;
        _maxTimer = new Timer(_ => Move(ToolRunState.Timeout, $"超过硬顶 {minutes} 分钟"), null,
            TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
    }

    private void StartGraceTimer()
    {
        int grace = _profile.PostExitGraceMinutes;
        if (grace <= 0)
        {
            Halt("主信号到达但宽限为 0，无法互证");
            return;
        }
        _graceTimer = new Timer(_ => Halt($"宽限 {grace} 分钟耗尽，副信号未到"), null,
            TimeSpan.FromMinutes(grace), Timeout.InfiniteTimeSpan);
    }

    private void Halt(string reason)
    {
        Move(ToolRunState.Halted, reason);
        try { _notify?.Invoke($"工具报告完成但未完全退出，已暂停等待处理（{_profile.DisplayName}）"); }
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
