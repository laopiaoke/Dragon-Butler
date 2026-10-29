using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DragonButler;

/// <summary>进程退出信号源：工具主进程退出时抛信号，携带退出码（由仲裁器判定 Done/Failed）。</summary>
public sealed class ProcessExitSignalSource : ICompletionSignalSource
{
    /// <inheritdoc />
    public string SourceId => "process-exit";

    /// <inheritdoc />
    public event Action<CompletionSignal>? SignalRaised;

    private Process? _proc;

    /// <inheritdoc />
    public void Start(ToolRunContext ctx)
    {
        _proc = ctx.Process;
        if (_proc == null) return;
        try
        {
            _proc.EnableRaisingEvents = true;
            _proc.Exited += OnExited;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("进程退出信号源启动失败：" + ex.Message);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        int code = -1;
        try { code = _proc?.ExitCode ?? -1; } catch { }
        Raise(new CompletionSignal
        {
            SourceId = SourceId,
            Kind = CompletionSignalKind.ProcessExit0,
            Confidence = SignalConfidence.High,
            ExitCode = code,
            Detail = "exit=" + code,
        });
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_proc == null) return;
        try { _proc.Exited -= OnExited; } catch { }
        _proc = null;
    }

    private void Raise(CompletionSignal s)
    {
        try { SignalRaised?.Invoke(s); } catch { }
    }
}

/// <summary>游戏进程退出信号源：轮询本会话的游戏进程，从"有"变"无"时抛信号（副信号）。</summary>
public sealed class GameProcessExitSignalSource : ICompletionSignalSource
{
    /// <inheritdoc />
    public string SourceId => "game-exit";

    /// <inheritdoc />
    public event Action<CompletionSignal>? SignalRaised;

    private string[] _names = Array.Empty<string>();
    private Timer? _timer;
    private int _seenAlive;
    private int _raised;

    /// <inheritdoc />
    public void Start(ToolRunContext ctx)
    {
        _names = ctx.Profile.GameProcessNames ?? Array.Empty<string>();
        if (_names.Length == 0) return;
        _timer = new Timer(_ => Poll(), null, 0, 2000);
    }

    private void Poll()
    {
        try
        {
            int alive = 0;
            foreach (string name in _names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                alive += ProcessTools.CountSessionProcesses(name);
            }
            if (alive > 0) { Interlocked.Exchange(ref _seenAlive, 1); return; }
            if (Volatile.Read(ref _seenAlive) == 0) return;          // 从未见过游戏进程 → 不作为信号
            if (Interlocked.Exchange(ref _raised, 1) == 1) return;   // 只抛一次
            Raise(new CompletionSignal
            {
                SourceId = SourceId,
                Kind = CompletionSignalKind.GameProcessExit,
                Confidence = SignalConfidence.Medium,
                Detail = "game 进程已全部退出",
            });
        }
        catch { }
    }

    /// <inheritdoc />
    public void Stop()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
    }

    private void Raise(CompletionSignal s)
    {
        try { SignalRaised?.Invoke(s); } catch { }
    }
}

/// <summary>
/// Webhook 信号源：订阅全局 <see cref="WebhookHub"/>（全库唯一 HttpListener），
/// 只把 dragon.end 转成完成信号。不再自己创建监听器，避免端口冲突与双判定。
/// </summary>
public sealed class WebhookSignalSource : ICompletionSignalSource
{
    /// <inheritdoc />
    public string SourceId => "webhook";

    /// <inheritdoc />
    public event Action<CompletionSignal>? SignalRaised;

    private int _started;

    /// <inheritdoc />
    public void Start(ToolRunContext ctx)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        WebhookHub.EventReceived += OnEvent;
    }

    private void OnEvent(SentinelEvent ev)
    {
        if (ev.Kind != SentinelEventKind.DragonEnded) return;
        Raise(new CompletionSignal
        {
            SourceId = SourceId,
            Kind = CompletionSignalKind.Webhook,
            Confidence = SignalConfidence.High,
            Detail = "dragon.end " + ev.Payload,
        });
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        WebhookHub.EventReceived -= OnEvent;
    }

    private void Raise(CompletionSignal s)
    {
        try { SignalRaised?.Invoke(s); } catch { }
    }
}

/// <summary>日志哨兵信号源：包装 <see cref="LogWatcher"/>，完成正则来自 profile。</summary>
public sealed class LogPatternSignalSource : ICompletionSignalSource
{
    /// <inheritdoc />
    public string SourceId => "log-pattern";

    /// <inheritdoc />
    public event Action<CompletionSignal>? SignalRaised;

    private LogWatcher? _watcher;
    private Timer? _timer;

    /// <inheritdoc />
    public void Start(ToolRunContext ctx)
    {
        string? pattern = ctx.Profile.LogCompletionPattern;
        if (string.IsNullOrWhiteSpace(pattern)) return;
        string? logDir = null;
        try
        {
            string? exeDir = System.IO.Path.GetDirectoryName(ctx.Profile.ExecutablePath);
            if (!string.IsNullOrEmpty(exeDir)) logDir = System.IO.Path.Combine(exeDir, "log");
        }
        catch { }

        _watcher = new LogWatcher(pattern, logDir);
        _watcher.OnEvent += ev =>
        {
            if (!string.Equals(ev.Source, "log-pattern", StringComparison.Ordinal)) return;
            Raise(new CompletionSignal
            {
                SourceId = SourceId,
                Kind = CompletionSignalKind.LogPattern,
                Confidence = SignalConfidence.Medium,
                Detail = ev.Payload,
            });
        };
        _watcher.Start();
        _timer = new Timer(_ => _watcher?.Pump(), null, 500, 1000);
    }

    /// <inheritdoc />
    public void Stop()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
        _watcher = null;
    }

    private void Raise(CompletionSignal s)
    {
        try { SignalRaised?.Invoke(s); } catch { }
    }
}

/// <summary>进程工具：按名字 + 当前会话过滤（SessionId 过滤语义，v1.3 起供所有工具复用）。</summary>
public static class ProcessTools
{
    /// <summary>统计本会话内指定进程名的实例数（取不到 SessionId 的忽略）。</summary>
    public static int CountSessionProcesses(string processName)
    {
        int me = Process.GetCurrentProcess().SessionId;
        int count = 0;
        Process[] list;
        try { list = Process.GetProcessesByName(processName); }
        catch { return 0; }
        foreach (var p in list)
        {
            try
            {
                if (p.SessionId == me) count++;
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        return count;
    }

    /// <summary>本会话内指定进程名的进程数组（调用方负责 Dispose）。</summary>
    public static Process[] GetSessionProcesses(string processName)
    {
        int me = Process.GetCurrentProcess().SessionId;
        var found = new System.Collections.Generic.List<Process>();
        Process[] list;
        try { list = Process.GetProcessesByName(processName); }
        catch { return Array.Empty<Process>(); }
        foreach (var p in list)
        {
            try
            {
                if (p.SessionId == me) found.Add(p);
                else p.Dispose();
            }
            catch { try { p.Dispose(); } catch { } }
        }
        return found.ToArray();
    }
}
