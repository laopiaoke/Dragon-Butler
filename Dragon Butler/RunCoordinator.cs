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

    // 两种证据与状态用同一把锁，副信号先到也能互证，失败终态不会被后来的信号改写。
    private bool _primaryArrived;
    private bool _secondarySeen;

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
    public bool IsFinished => IsTerminal(State);

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
    public void Abort(string reason = "用户中止") => Move(ToolRunState.Failed, reason);

    private void OnSignal(CompletionSignal signal)
    {
        ToolRunState from, to;
        string reason;
        lock (_gate)
        {
            from = _state;
            if (from is not (ToolRunState.Running or ToolRunState.Resolving)) return;

            bool primary = signal.Kind == _profile.PrimarySignal;
            bool exited = signal.Kind == CompletionSignalKind.ProcessExit0;
            if (exited && signal.ExitCode != 0)
            {
                to = ToolRunState.Failed;
                reason = $"进程异常退出 exit={signal.ExitCode ?? -1}";
            }
            else if (primary && (exited || signal.Kind == CompletionSignalKind.Duration))
            {
                to = ToolRunState.Done;
                reason = exited ? "进程正常退出 exit=0" : "已到达设定时长";
            }
            else
            {
                if (primary) _primaryArrived = true;
                else _secondarySeen = true;

                if (_primaryArrived && _secondarySeen)
                {
                    to = ToolRunState.Done;
                    reason = "主信号与副信号互证完成";
                }
                else if (from == ToolRunState.Running && (primary || exited))
                {
                    // 游戏退出和 Webhook 先到只记证据；只有主信号或进程零退出才开始宽限。
                    if (_profile.PostExitGraceMinutes <= 0)
                    {
                        to = ToolRunState.Halted;
                        reason = $"宽限为 0，{MissingEvidence()}";
                    }
                    else
                    {
                        to = ToolRunState.Resolving;
                        reason = primary
                            ? $"主信号 {signal.Kind} 到达，等待副信号互证"
                            : "进程已退出 exit=0，等待主信号互证";
                    }
                }
                else
                {
                    return;
                }
            }

            SetState(to);
        }

        PublishTransition(from, to, reason);
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
        _graceTimer = new Timer(_ => GraceExpired(grace), null,
            TimeSpan.FromMinutes(grace), Timeout.InfiniteTimeSpan);
    }

    private void GraceExpired(int minutes)
    {
        string reason;
        lock (_gate)
        {
            if (_state != ToolRunState.Resolving) return;
            reason = $"宽限 {minutes} 分钟耗尽，{MissingEvidence()}";
        }
        Move(ToolRunState.Halted, reason);
    }

    private string MissingEvidence() => _primaryArrived ? "副信号未到" : "进程已退出但主信号未到";

    private void Move(ToolRunState to, string reason)
    {
        ToolRunState from;
        lock (_gate)
        {
            if (_state == to || IsTerminal(_state)) return;
            from = _state;
            SetState(to);
        }
        PublishTransition(from, to, reason);
    }

    private static bool IsTerminal(ToolRunState state) => state is ToolRunState.Done or ToolRunState.Failed
        or ToolRunState.Timeout or ToolRunState.Halted or ToolRunState.Stalled;

    private void SetState(ToolRunState to)
    {
        _state = to;
        if (IsTerminal(to)) StopTimers();
        else if (to == ToolRunState.Running) StartMaxTimer();
        else if (to == ToolRunState.Resolving) StartGraceTimer();
    }

    private void PublishTransition(ToolRunState from, ToolRunState to, string reason)
    {
        LogService.LogEvent($"[run:{_runId}] {_profile.ToolId} {from}→{to}（{reason}）");
        try { Transition?.Invoke(from, to, reason); } catch { }
        if (to == ToolRunState.Halted)
        {
            try { _notify?.Invoke($"「{_profile.DisplayName}」这次没法确认结果，已暂停等你处理：{reason}"); }
            catch { }
        }
    }

    private void StopTimers()
    {
        _maxTimer?.Dispose();
        _graceTimer?.Dispose();
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
        lock (_gate) StopTimers();
    }
}
