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
    private static WebhookListener Listener = new();
    private static Timer? _drainTimer;
    private static int _port;
    private static bool _wildcard;

    /// <summary>收到 BGI 事件（线程池线程；订阅方自行保证线程安全）。</summary>
    public static event Action<SentinelEvent>? EventReceived;

    /// <summary>是否正在监听。</summary>
    public static bool IsRunning => Listener.IsRunning;

    /// <summary>当前监听端口。</summary>
    public static int Port => _port;

    /// <summary>
    /// 只读面板的地址（v1.7 F7）。局域网监听时给本机在局域网里的 IPv4，
    /// 回环监听时给 127.0.0.1。拿不到网卡就退回回环——宁可给个连不上的地址，也不猜。
    /// </summary>
    public static string PanelAddress()
    {
        if (!_wildcard || _port <= 0) return "http://127.0.0.1:" + _port + "/";
        try
        {
            foreach (var ip in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
            {
                if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                string s = ip.ToString();
                if (s.StartsWith("127.", StringComparison.Ordinal)) continue;
                return "http://" + s + ":" + _port + "/";
            }
        }
        catch { }
        return "http://127.0.0.1:" + _port + "/";
    }

    /// <summary>累计解析成功的事件数。</summary>
    public static int EventCount => Listener.EventCount;

    /// <summary>最近事件时间。</summary>
    public static DateTime LastEventAt => Listener.LastEventAt;

    /// <summary>启动（幂等）：端口变化时重启监听。<paramref name="wildcard"/> 打开时同时监听局域网。</summary>
    public static void Start(int port, bool wildcard = false)
    {
        if (Listener.IsRunning && _port == port && _wildcard == wildcard) return;
        _port = port;
        _wildcard = wildcard;
        Listener.Route = HandleRoute;
        Listener.Start(port, wildcard);
        _drainTimer ??= new Timer(_ => Drain(), null, 300, 300);
        LogService.LogEvent($"Webhook 总线：端口 {port}，运行={Listener.IsRunning}，局域网={wildcard}");
    }

    /// <summary>
    /// 非 /webhook 路径的统一入口（v1.7）：只读面板与受令牌保护的控制 API。
    /// 返回 null = 不认识这个路径，让监听器回 404。
    /// v1.11 起多一个 MCP 分支（<paramref name="body"/> 是 POST 上来的原文）。
    /// </summary>
    private static (int Status, string ContentType, string Body)? HandleRoute(
        string path, string? query, string? token, string? body)
    {
        try
        {
            string p = (path ?? "").TrimEnd('/');
            if (p.Length == 0) p = "/";

            // F7：只读面板（根路径）。开着才能访问；面板不含任何控制入口。
            if (p == "/")
            {
                if (!AppState.Settings.LanPanelEnabled) return null;
                return (200, ControlApi.PanelContentType, BuildPanel());
            }

            // G3：MCP（JSON-RPC over HTTP）。与控制 API 共用开关与令牌。
            if (p.Equals(McpServer.Path, StringComparison.OrdinalIgnoreCase))
                return HandleMcp(body, token);

            // F6：控制 API。判定完全交给 ControlApi.Plan（纯函数）
            var plan = ControlApi.Plan(p, token, AppState.Settings.ControlApiEnabled, AppState.Settings.ControlApiToken);
            if (plan.Status == 404) return null;

            if (!plan.Ok)
            {
                LogService.LogEvent($"控制 API：{p} 拒绝（{plan.Status}）");
                return (plan.Status, ControlApi.JsonContentType, plan.Body);
            }

            if (plan.Action == ApiAction.None) // /api/status
                return (200, ControlApi.JsonContentType, BuildStatus());

            string toolId = ControlApi.ToolIdFromQuery(query);
            string action = plan.Action switch
            {
                ApiAction.Start => "start",
                ApiAction.Stop => "stop",
                _ => "run",
            };

            LogService.LogEvent($"控制 API：请求 {action}" + (toolId.Length > 0 ? $"（{toolId}）" : ""));
            var (ok, message) = Execute(plan.Action, toolId);
            return (200, ControlApi.JsonContentType, ControlApi.ActionJson(ok, action, message));
        }
        catch (Exception ex)
        {
            LogService.LogEvent("控制 API 处理异常：" + ex.Message);
            return (500, ControlApi.JsonContentType, ControlApi.Fail("服务端异常：" + ex.Message));
        }
    }

    /// <summary>
    /// MCP 分支（v1.11 / G3）：判定交给 <see cref="McpServer.Plan"/>，需要动队列时在这里执行。
    /// 与 <see cref="HandleRoute"/> 分开，是因为「判定 → 执行 → 组装响应」比控制 API 多一步。
    /// </summary>
    private static (int Status, string ContentType, string Body) HandleMcp(string? body, string? token)
    {
        var plan = McpServer.Plan(body, AppState.Settings.ControlApiEnabled,
                                  token, AppState.Settings.ControlApiToken);

        // 开门见山记一笔：MCP 是给外部 AI 用的，出问题时日志里得能对上号
        if (plan.Status is 401 or 503 or 400)
            LogService.LogEvent($"MCP：拒绝（{plan.Status}）");

        if (plan.IsNotification && plan.Body.Length == 0) return (plan.Status, "", "");
        if (!plan.NeedsExecution) return (plan.Status, ControlApi.JsonContentType, plan.Body);

        switch (plan.Action)
        {
            case McpAction.Status:
                var done = QueueService.DoneToolIdsToday();
                var unfinished = QueueService.UnfinishedToday();
                int total = AppState.Settings.Queue.Count(q => q.Enabled);
                string text = McpServer.StatusText(
                    QueueService.IsRunning,
                    QueueService.IsRunning ? QueueService.CurrentToolId : "",
                    done.Count, unfinished.Count, total, LastEventText);
                return (200, ControlApi.JsonContentType, McpServer.ToolResult(plan.IdJson, text));

            case McpAction.Start:
            case McpAction.Stop:
            case McpAction.Run:
                var action = plan.Action switch
                {
                    McpAction.Start => ApiAction.Start,
                    McpAction.Stop => ApiAction.Stop,
                    _ => ApiAction.Run,
                };
                LogService.LogEvent($"MCP：请求 {plan.Action}" + (plan.ToolId.Length > 0 ? $"（{plan.ToolId}）" : ""));
                var (ok, message) = Execute(action, plan.ToolId);
                return (200, ControlApi.JsonContentType,
                        ok ? McpServer.ToolResult(plan.IdJson, message)
                           : McpServer.ToolError(plan.IdJson, message));

            default:
                return (200, ControlApi.JsonContentType, McpServer.ToolError(plan.IdJson, "不支持的动作"));
        }
    }

    /// <summary>状态快照（纯读取，不改任何状态）。</summary>
    private static string BuildStatus()
    {
        var done = QueueService.DoneToolIdsToday();
        var unfinished = QueueService.UnfinishedToday();
        int total = AppState.Settings.Queue.Count(q => q.Enabled);
        return ControlApi.StatusJson(
            QueueService.IsRunning,
            QueueService.IsRunning ? QueueService.CurrentToolId : "",
            done.Count,
            unfinished.Count,
            total,
            LastEventText);
    }

    /// <summary>只读面板 HTML（数据与 <see cref="BuildStatus"/> 同源）。</summary>
    private static string BuildPanel()
    {
        var done = QueueService.DoneToolIdsToday();
        var unfinished = QueueService.UnfinishedToday();
        int total = AppState.Settings.Queue.Count(q => q.Enabled);
        return ControlApi.PanelHtml(
            "一条龙管家",
            QueueService.IsRunning,
            QueueService.IsRunning ? QueueService.CurrentToolId : "",
            done.Count,
            unfinished.Count,
            total,
            LastEventText,
            5);
    }

    /// <summary>最近一条事件文本（界面、状态 API、面板共用一句话）。</summary>
    public static string LastEventText { get; private set; } = "";

    /// <summary>执行控制动作。返回 (是否成功, 给人看的一句话)。</summary>
    private static (bool Ok, string Message) Execute(ApiAction action, string toolId)
    {
        switch (action)
        {
            case ApiAction.Start:
                if (QueueService.IsRunning) return (false, "队列正在运行");
                _ = QueueService.StartAsync(QueueTrigger.Manual);
                return (true, "已请求开跑");

            case ApiAction.Stop:
                if (!QueueService.IsRunning) return (false, "队列没在运行");
                QueueService.Stop();
                return (true, "已请求停止");

            case ApiAction.Run:
                if (toolId.Length == 0) return (false, "缺少 tool 参数");
                if (QueueService.IsRunning) return (false, "队列正在运行，等它结束");
                var profile = ProfileRegistry.Get(toolId);
                if (profile == null) return (false, "未知工具：" + toolId);
                _ = QueueService.StartSingleAsync(profile, QueueTrigger.Manual);
                return (true, "已请求单跑 " + profile.DisplayName);

            default:
                return (false, "不支持的动作");
        }
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
                LastEventText = Describe(ev);
                try { EventReceived?.Invoke(ev); }
                catch (Exception ex) { LogService.LogEvent("Webhook 事件订阅者异常：" + ex.Message); }
            }
        }
        catch { }
    }

    /// <summary>事件 → 一句话（面板与状态 API 用，不追求详尽，只求一眼看懂）。</summary>
    private static string Describe(SentinelEvent ev) => ev.Kind switch
    {
        SentinelEventKind.DragonStarted => "一条龙开始" + Suffix(ev),
        SentinelEventKind.DragonEnded => "一条龙结束" + Suffix(ev),
        SentinelEventKind.GroupStarted => "组开始：" + (ev.Payload ?? ""),
        SentinelEventKind.GroupEnded => "组结束：" + (ev.Payload ?? ""),
        SentinelEventKind.GroupError => "组异常：" + (ev.Payload ?? ""),
        SentinelEventKind.BgiStarted => "BGI 已启动",
        SentinelEventKind.BgiExited => "BGI 已退出",
        SentinelEventKind.BgiRestarted => "BGI 已重启",
        SentinelEventKind.GameLost => "游戏进程丢失",
        SentinelEventKind.ScreenFrozen => "画面疑似冻结",
        SentinelEventKind.MemoryHigh => "内存占用偏高",
        _ => ev.Kind.ToString(),
    };

    private static string Suffix(SentinelEvent ev)
        => string.IsNullOrWhiteSpace(ev.Source) ? "" : "（" + ev.Source + "）";
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
