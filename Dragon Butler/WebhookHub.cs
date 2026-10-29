using System;
using System.Threading;

namespace DragonButler;

/// <summary>
/// Webhook 事件总线：全库唯一的 HttpListener 实例（<see cref="WebhookListener"/>）在这里创建与驱动。
/// 事件扇出给：正在运行的 <see cref="WebhookSignalSource"/>（完成判定）与
/// <see cref="GroupArchiveWatcher"/>（组归档统计）。
/// 验收标准：全库 <c>new HttpListener</c> 仅存在于 WebhookListener 内部一处。
/// </summary>
public static class WebhookHub
{
    private static readonly WebhookListener Listener = new();
    private static Timer? _drainTimer;
    private static int _port;

    /// <summary>收到 BGI 事件（线程池线程；订阅方自行保证线程安全）。</summary>
    public static event Action<SentinelEvent>? EventReceived;

    /// <summary>是否正在监听。</summary>
    public static bool IsRunning => Listener.IsRunning;

    /// <summary>当前监听端口。</summary>
    public static int Port => _port;

    /// <summary>累计解析成功的事件数。</summary>
    public static int EventCount => Listener.EventCount;

    /// <summary>最近事件时间。</summary>
    public static DateTime LastEventAt => Listener.LastEventAt;

    /// <summary>启动（幂等）：端口变化时重启监听。</summary>
    public static void Start(int port)
    {
        if (Listener.IsRunning && _port == port) return;
        _port = port;
        Listener.Start(port);
        _drainTimer ??= new Timer(_ => Drain(), null, 300, 300);
        LogService.LogEvent($"Webhook 总线：端口 {port}，运行={Listener.IsRunning}");
    }

    /// <summary>停止监听。</summary>
    public static void Stop()
    {
        try { _drainTimer?.Dispose(); } catch { }
        _drainTimer = null;
        try { Listener.Stop(); } catch { }
    }

    private static void Drain()
    {
        try
        {
            foreach (var ev in Listener.Drain())
            {
                try { EventReceived?.Invoke(ev); }
                catch (Exception ex) { LogService.LogEvent("Webhook 事件订阅者异常：" + ex.Message); }
            }
        }
        catch { }
    }
}

/// <summary>
/// 组归档接驳：只负责把"组开始/组结束"落到 <see cref="GroupArchive"/>。
/// 数据源 = Webhook 总线 + 日志尾随（无 Webhook 时靠日志），与完成判定完全解耦。
/// </summary>
public static class GroupArchiveWatcher
{
    private static LogWatcher? _log;
    private static Timer? _pump;
    private static bool _started;

    /// <summary>启动（幂等）。</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            GroupArchive.Load();
            WebhookHub.EventReceived += OnWebhook;

            _log = new LogWatcher();
            _log.OnEvent += OnLog;
            _log.Start();
            _pump = new Timer(_ => { try { _log?.Pump(); } catch { } }, null, 1000, 1000);
            LogService.LogEvent("组归档：已接驳（Webhook + 日志）");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("组归档接驳失败：" + ex.Message);
        }
    }

    /// <summary>停止。</summary>
    public static void Stop()
    {
        try { WebhookHub.EventReceived -= OnWebhook; } catch { }
        try { _pump?.Dispose(); } catch { }
        _pump = null;
        _log = null;
        _started = false;
    }

    private static void OnWebhook(SentinelEvent ev)
    {
        switch (ev.Kind)
        {
            case SentinelEventKind.GroupStarted: GroupArchive.RecordStart(ev.Payload, "webhook"); break;
            case SentinelEventKind.GroupEnded: GroupArchive.RecordEnd(ev.Payload, "webhook"); break;
            case SentinelEventKind.GroupError:
                LogService.LogEvent("组归档：任务异常（" + ev.Payload + "）");
                break;
        }
    }

    private static void OnLog(SentinelEvent ev)
    {
        switch (ev.Kind)
        {
            case SentinelEventKind.GroupStarted: GroupArchive.RecordStart(ev.Payload, "log"); break;
            case SentinelEventKind.GroupEnded: GroupArchive.RecordEnd(ev.Payload, "log"); break;
        }
    }
}
