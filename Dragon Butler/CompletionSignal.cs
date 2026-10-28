using System;
using System.Diagnostics;

namespace DragonButler;

/// <summary>
/// 一次工具执行的上下文：告诉信号源"这次跑的是谁、进程是哪个、参数是什么"。
/// </summary>
public sealed class ToolRunContext
{
    /// <summary>本次运行的唯一编号（日志用）。</summary>
    public string RunId { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>本次使用的工具档案（副本）。</summary>
    public ToolProfile Profile { get; init; } = new();

    /// <summary>工具主进程（启动成功后赋值）。</summary>
    public Process? Process { get; set; }

    /// <summary>实际使用的启动参数。</summary>
    public string Arguments { get; init; } = "";

    /// <summary>启动时刻。</summary>
    public DateTime StartedAt { get; init; } = DateTime.Now;
}

/// <summary>
/// 完成信号：某个信号源在某一时刻给出的"可能完成了"的证据。
/// </summary>
public sealed class CompletionSignal
{
    /// <summary>信号源标识（process-exit / game-exit / webhook / log-pattern）。</summary>
    public string SourceId { get; init; } = "";

    /// <summary>信号类别。</summary>
    public CompletionSignalKind Kind { get; init; }

    /// <summary>置信级别。</summary>
    public SignalConfidence Confidence { get; init; } = SignalConfidence.Medium;

    /// <summary>发生时刻。</summary>
    public DateTime At { get; init; } = DateTime.Now;

    /// <summary>进程退出码（仅 process-exit 信号有值）。</summary>
    public int? ExitCode { get; init; }

    /// <summary>补充信息（组名、命中行等），仅进日志。</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// 完成信号源：主/副信号统一接口。实现方负责启动、停止与在合适时机抛事件。
/// </summary>
public interface ICompletionSignalSource
{
    /// <summary>信号源标识。</summary>
    string SourceId { get; }

    /// <summary>开始监听（幂等）。</summary>
    void Start(ToolRunContext ctx);

    /// <summary>信号到达（可能在后台线程触发）。</summary>
    event Action<CompletionSignal>? SignalRaised;

    /// <summary>停止监听并释放资源（幂等）。</summary>
    void Stop();
}
