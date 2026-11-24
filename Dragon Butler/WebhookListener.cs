using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DragonButler;

/// <summary>
/// Webhook 监听的实际绑定结果（v1.43 / 任务三）。
/// <see cref="Started"/> 为 false 时，界面上不能再展示任何「可用地址」。
/// <see cref="Wildcard"/> 表示真正绑上的是通配前缀，而不是「请求了通配」。
/// 这两件事必须分开：管理员权限缺失时会静默退回回环，用请求参数推断就会指着一个不可达的地址。
/// </summary>
public sealed record WebhookBindResult(bool Started, bool Wildcard, int Port, string Prefix, string Error)
{
    /// <summary>没启动时的空结果，方便调用方少写分支。</summary>
    public static WebhookBindResult NotStarted(int port, string error) => new(false, false, port, "", error);
}

/// <summary>
/// 一条 /webhook 报文的分类结果（v1.43 / 任务四）。
/// <see cref="Status"/> 直接当作 HTTP 状态码回给发送方：
/// 只有「已知事件且真的入队」才是 200，其余各自有自己的码，发送方才有办法区分。
/// </summary>
public sealed record WebhookParseResult(int Status, string Body, SentinelEvent? Event)
{
    public bool Accepted => Status == 200;
}

public sealed class WebhookListener
{
    private HttpListener? _listener;
    private Thread? _thread;
    private volatile bool _running;
    private readonly ConcurrentQueue<SentinelEvent> _queue = new();

    /// <summary>v1.43 / 任务三：真正绑上去的前缀（没起来时为空串）。</summary>
    private string _boundPrefix = "";

    /// <summary>
    /// 附加路由（v1.7）：路径不是 /webhook 时交给它，由调用方决定回什么。
    /// 参数依次是 绝对路径、查询串、请求头 <see cref="ControlApi.TokenHeader"/> 的值、请求体。
    /// 返回 状态码 / Content-Type / 正文；返回 null 表示「我不认识这个路径」，走 404。
    /// 用委托而不是直接调 WebhookHub，是为了让监听器只管收发、不管业务。
    /// 请求体是 v1.11 加的：MCP 是 POST + JSON-RPC，光有路径没法处理。
    /// </summary>
    public Func<string, string?, string?, string?, (int Status, string ContentType, string Body)?>? Route { get; set; }

    /// <summary>是否正在监听</summary>
    public bool IsRunning => _running;

    /// <summary>v1.43 / 任务三：实际绑定的前缀，例如 http://127.0.0.1:8765/ 或 http://+:8765/。</summary>
    public string BoundPrefix => _boundPrefix;

    /// <summary>v1.43 / 任务三：当前是否真的绑在通配前缀上（而不是被请求了通配）。</summary>
    public bool IsWildcardBound => _running && _boundPrefix.StartsWith("http://+:", StringComparison.Ordinal);

    /// <summary>累计收到并解析成功的 BGI 事件数（供界面展示进度识别是否真的在工作）</summary>
    public int EventCount => _eventCount;

    /// <summary>最近一次收到事件的时间</summary>
    public DateTime LastEventAt => _lastEventAt;

    private int _eventCount;
    private DateTime _lastEventAt;

    /// <summary>
    /// 启动监听。
    /// <paramref name="wildcard"/> = true 时优先监听所有网卡（局域网面板用），否则只监听回环。
    /// 通配前缀在非管理员下会被系统拒绝，此时自动退回回环并记一条日志——不静默失败。
    ///
    /// <para>
    /// v1.43 / 任务三：返回**实际**绑定结果，而不是「请求了什么」。
    /// 原来这个方法返回 void，调用方只能拿自己的参数猜——于是通配绑定失败、退回回环之后，
    /// 面板照旧把自己的局域网 IP 报给用户，手机打开是一片空白。
    /// </para>
    /// </summary>
    /// <returns>实际监听状态。<see cref="WebhookBindResult.Started"/> 为 false 时降级为仅日志。</returns>
    public WebhookBindResult Start(int port, bool wildcard = false)
    {
        Stop();
        bool boundWildcard = false;
        try
        {
            var l = new HttpListener();
            // HttpListener 前缀必须以 '/' 结尾：注册根前缀，在回调里按路径路由，
            // 这样无论 BGI 配置的端点带不带结尾斜杠（/webhook 或 /webhook/）都能收到
            if (wildcard)
            {
                try
                {
                    l.Prefixes.Add($"http://+:{port}/");
                    l.Start();
                    boundWildcard = true;
                }
                catch (Exception ex)
                {
                    LogService.LogEvent($"局域网监听失败（改用仅本机，如需局域网请以管理员运行 netsh http add urlacl url=http://+:{port}/ user=Everyone）：{ex.Message}");
                    try { l.Close(); } catch { }
                    try
                    {
                        l = new HttpListener();
                        l.Prefixes.Add($"http://127.0.0.1:{port}/");
                        l.Start();
                    }
                    catch (Exception ex2)
                    {
                        // 连回环都起不来：端口占用、被别的程序抢先注册，都落在这里。
                        // 这种时候必须如实说「没在监听」——UI 据此不再给可用地址。
                        try { l.Close(); } catch { }
                        _listener = null;
                        _running = false;
                        _boundPrefix = "";
                        LogService.LogEvent($"Webhook 监听启动失败（降级为仅日志模式）：{ex2.Message}");
                        return WebhookBindResult.NotStarted(port, ex2.Message);
                    }
                }
            }
            else
            {
                try
                {
                    l.Prefixes.Add($"http://127.0.0.1:{port}/");
                    l.Start();
                }
                catch (Exception ex)
                {
                    try { l.Close(); } catch { }
                    _listener = null;
                    _running = false;
                    _boundPrefix = "";
                    LogService.LogEvent($"Webhook 监听启动失败（降级为仅日志模式）：{ex.Message}");
                    return WebhookBindResult.NotStarted(port, ex.Message);
                }
            }

            _listener = l;
            _running = true;
            _boundPrefix = boundWildcard ? $"http://+:{port}/" : $"http://127.0.0.1:{port}/";
            _thread = new Thread(Loop) { IsBackground = true, Name = "DragonButler-Webhook" };
            _thread.Start();
            string scope = boundWildcard ? "所有网卡（局域网）" : "仅本机";
            LogService.LogEvent($"Webhook 监听已启动：http://127.0.0.1:{port}/webhook（{scope}）");
            return new WebhookBindResult(true, boundWildcard, port, _boundPrefix, "");
        }
        catch (Exception ex)
        {
            _running = false;
            _boundPrefix = "";
            LogService.LogEvent($"Webhook 监听启动失败（降级为仅日志模式）：{ex.Message}");
            return WebhookBindResult.NotStarted(port, ex.Message);
        }
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                var ctx = _listener!.GetContext();
                string path = (ctx.Request.Url?.AbsolutePath ?? "").TrimEnd('/');
                if (!path.EndsWith("/webhook", StringComparison.OrdinalIgnoreCase))
                {
                    // v1.7：非 /webhook 的路径交给附加路由（控制 API / 只读面板）
                    // v1.11：把请求体读出来一起传——MCP 走 POST + JSON-RPC
                    string routeBody = "";
                    if (ctx.Request.HasEntityBody)
                    {
                        using var routeReader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        routeBody = routeReader.ReadToEnd();
                    }

                    var handled = Route?.Invoke(
                        ctx.Request.Url?.AbsolutePath ?? "",
                        ctx.Request.Url?.Query,
                        ctx.Request.Headers[ControlApi.TokenHeader],
                        routeBody);
                    if (handled == null)
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }

                    byte[] bytes = Encoding.UTF8.GetBytes(handled.Value.Body ?? "");
                    ctx.Response.StatusCode = handled.Value.Status;
                    if (!string.IsNullOrEmpty(handled.Value.ContentType))
                        ctx.Response.ContentType = handled.Value.ContentType;
                    // 面板会被手机反复拉，控制 API 的响应也不该被任何中间层缓存
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                    continue;
                }
                if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    // v1.43 / 任务四：GET /webhook 以前也回 200，发送方以为打通了，
                    // 其实一个事件都没产生。方法不对要明说。
                    WritePlain(ctx, 405, "只接受 POST");
                    continue;
                }

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                string body = reader.ReadToEnd();
                WebhookParseResult parsed = Parse(body);
                if (parsed.Accepted)
                {
                    _queue.Enqueue(parsed.Event!);
                    Interlocked.Increment(ref _eventCount);
                    _lastEventAt = DateTime.Now;
                }
                // v1.43 / 任务四：只有「已知事件且真的入队」才回 200。
                // 非法 JSON、缺 event、未知事件各自有码——发送方才有办法区分
                // 「送进去了」和「格式不对」，而不是一律看到 ok。
                WritePlain(ctx, parsed.Status, parsed.Body);
            }
            catch
            {
                if (!_running) break;
            }
        }
    }

    /// <summary>
    /// 解析 BGI 事件 JSON。
    ///
    /// <para>
    /// v1.43 / 任务四：返回分类结果而不是 void。以前无论报文长什么样都回 200 "ok"，
    /// 于是「事件名写错了」「JSON 多了个逗号」这类问题在发送方那边看起来全是成功，
    /// 排查时只能去翻这一侧的日志。
    /// </para>
    /// <para>
    /// 这是纯函数（除了入队由调用方做），探针可以直接喂字符串断言状态码。
    /// </para>
    /// </summary>
    public static WebhookParseResult Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new WebhookParseResult(400, "空报体", null);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return new WebhookParseResult(400, "不是合法 JSON", null); }

        try
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new WebhookParseResult(400, "报体必须是 JSON 对象", null);

            string? ev = null;
            if (root.TryGetProperty("event", out var e1) && e1.ValueKind == JsonValueKind.String) ev = e1.GetString();
            else if (root.TryGetProperty("Event", out var e2) && e2.ValueKind == JsonValueKind.String) ev = e2.GetString();
            if (string.IsNullOrEmpty(ev)) return new WebhookParseResult(400, "缺少 event 字段", null);

            string name = "";
            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                name = n.GetString() ?? "";
            else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                     && d.TryGetProperty("name", out var n2) && n2.ValueKind == JsonValueKind.String)
                name = n2.GetString() ?? "";

            // 事件映射：dragon.start/end、group.start/end、task.error/cancel
            SentinelEventKind? kind = ev switch
            {
                "dragon.start" => SentinelEventKind.DragonStarted,
                "dragon.end" => SentinelEventKind.DragonEnded,
                "group.start" => SentinelEventKind.GroupStarted,
                "group.end" => SentinelEventKind.GroupEnded,
                "task.error" => SentinelEventKind.GroupError,
                "task.cancel" => SentinelEventKind.GroupError,
                _ => null,
            };
            if (kind == null)
            {
                // v1.43 / 任务四：未知事件不能伪装成功。
                // 回显的事件名截到 40 字——报体里可能是别人塞的任意长字符串，
                // 拿它当响应正文等于把请求方的输入原样吐回去。
                string echo = ev.Length > 40 ? ev[..40] + "…" : ev;
                return new WebhookParseResult(400, "不认识的事件：" + echo, null);
            }

            // 入队由调用方做（Loop 里判 Accepted 之后），Parse 自己保持纯函数。
            return new WebhookParseResult(200, "ok",
                new SentinelEvent { Kind = kind.Value, Payload = name, Source = "webhook" });
        }
        finally { doc.Dispose(); }
    }

    /// <summary>v1.43 / 任务四：回一段短正文，附 Content-Length，别让客户端挂着等。</summary>
    private static void WritePlain(HttpListenerContext ctx, int status, string text)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch { }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    /// <summary>取出并清空待处理事件队列（由 SentinelService 心跳在主线程调用）</summary>
    public List<SentinelEvent> Drain()
    {
        var list = new List<SentinelEvent>();
        while (_queue.TryDequeue(out var item)) list.Add(item);
        return list;
    }

    /// <summary>停止监听</summary>
    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }
}
