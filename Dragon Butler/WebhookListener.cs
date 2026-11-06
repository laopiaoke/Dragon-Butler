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
/// BGI 原生 Webhook 事件接收（SPBGI）：HttpListener 监听 http://127.0.0.1:{port}/webhook，
/// 后台线程 Accept，解析 JSON（BGI 事件格式含 event 字段），入并发队列；
/// SentinelService 心跳时在主线程消费队列。失败（端口占用等）降级为仅日志模式。
/// </summary>
public sealed class WebhookListener
{
    private HttpListener? _listener;
    private Thread? _thread;
    private volatile bool _running;
    private readonly ConcurrentQueue<SentinelEvent> _queue = new();

    /// <summary>
    /// 附加路由（v1.7）：路径不是 /webhook 时交给它，由调用方决定回什么。
    /// 参数依次是 绝对路径、查询串、请求头 <see cref="ControlApi.TokenHeader"/> 的值。
    /// 返回 状态码 / Content-Type / 正文；返回 null 表示「我不认识这个路径」，走 404。
    /// 用委托而不是直接调 WebhookHub，是为了让监听器只管收发、不管业务。
    /// </summary>
    public Func<string, string?, string?, (int Status, string ContentType, string Body)?>? Route { get; set; }

    /// <summary>是否正在监听</summary>
    public bool IsRunning => _running;

    /// <summary>累计收到并解析成功的 BGI 事件数（供界面展示进度识别是否真的在工作）</summary>
    public int EventCount => _eventCount;

    /// <summary>最近一次收到事件的时间</summary>
    public DateTime LastEventAt => _lastEventAt;

    private int _eventCount;
    private DateTime _lastEventAt;

    /// <summary>
    /// 启动监听；失败降级为仅日志模式并记日志。
    /// <paramref name="wildcard"/> = true 时监听所有网卡（局域网面板用），否则只监听回环。
    /// 通配前缀在非管理员下会被系统拒绝，此时自动退回回环并记一条日志——不静默失败。
    /// </summary>
    public void Start(int port, bool wildcard = false)
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
                    l = new HttpListener();
                    l.Prefixes.Add($"http://127.0.0.1:{port}/");
                    l.Start();
                }
            }
            else
            {
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
            }

            _listener = l;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "DragonButler-Webhook" };
            _thread.Start();
            string scope = boundWildcard ? "所有网卡（局域网）" : "仅本机";
            LogService.LogEvent($"Webhook 监听已启动：http://127.0.0.1:{port}/webhook（{scope}）");
        }
        catch (Exception ex)
        {
            _running = false;
            LogService.LogEvent($"Webhook 监听启动失败（降级为仅日志模式）：{ex.Message}");
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
                    var handled = Route?.Invoke(
                        ctx.Request.Url?.AbsolutePath ?? "",
                        ctx.Request.Url?.Query,
                        ctx.Request.Headers[ControlApi.TokenHeader]);
                    if (handled == null)
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }

                    byte[] bytes = Encoding.UTF8.GetBytes(handled.Value.Body ?? "");
                    ctx.Response.StatusCode = handled.Value.Status;
                    ctx.Response.ContentType = handled.Value.ContentType;
                    // 面板会被手机反复拉，控制 API 的响应也不该被任何中间层缓存
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                    continue;
                }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                string body = reader.ReadToEnd();
                Parse(body);
                byte[] ok = Encoding.UTF8.GetBytes("ok");
                ctx.Response.StatusCode = 200;
                ctx.Response.OutputStream.Write(ok, 0, ok.Length);
                ctx.Response.Close();
            }
            catch
            {
                if (!_running) break;
            }
        }
    }

    /// <summary>解析 BGI 事件 JSON 并入队</summary>
    private void Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            string? ev = null;
            if (root.TryGetProperty("event", out var e1)) ev = e1.GetString();
            else if (root.TryGetProperty("Event", out var e2)) ev = e2.GetString();
            if (string.IsNullOrEmpty(ev)) return;

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
            if (kind != null)
            {
                _queue.Enqueue(new SentinelEvent { Kind = kind.Value, Payload = name, Source = "webhook" });
                Interlocked.Increment(ref _eventCount);
                _lastEventAt = DateTime.Now;
            }
        }
        catch { }
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
