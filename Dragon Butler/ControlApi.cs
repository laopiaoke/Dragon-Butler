using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DragonButler;

/// <summary>外部请求想干的事。判定与执行分开，<see cref="Plan"/> 才能是纯函数。</summary>
public enum ApiAction
{
    /// <summary>什么都不干（只读查询，或请求被拒）。</summary>
    None = 0,
    Start,
    Stop,
    Run,
}

/// <summary>
/// 路由判定结果：<see cref="Status"/> 是 HTTP 状态码，<see cref="Body"/> 是 JSON 或纯文本。
/// </summary>
public sealed record ApiPlan(int Status, ApiAction Action, string ToolId, string Body)
{
    public bool Ok => Status == 200;
}

/// <summary>
/// 本机控制 API（v1.7 / F6）+ 只读面板（F7）。
///
/// 三条铁律：
/// 1) **不新增 HttpListener**。`WebhookHub.cs` 里写着验收标准「全库 new HttpListener 仅存在于
///    WebhookListener 内部一处」，所以这里只做路由与判定，真正的监听仍由 WebhookListener 承担。
/// 2) 判定与执行分离：<see cref="Plan"/> 是纯函数（不读设置、不碰队列、不碰时钟），
///    Probe 能拿固定输入验固定输出；副作用集中在 <see cref="WebhookHub.HandleApi"/>。
/// 3) 默认关，只收本机回环请求，开启后仍要令牌。局域网面板只开放读，不扩大控制范围。
/// </summary>
public static class ControlApi
{
    /// <summary>令牌请求头名。</summary>
    public const string TokenHeader = "X-Butler-Token";

    /// <summary>令牌长度（32 个十六进制字符 = 128 位随机量）。</summary>
    public const int TokenLength = 32;

    /// <summary>状态查询路径。</summary>
    public const string StatusPath = "/api/status";

    /// <summary>开跑路径。</summary>
    public const string StartPath = "/api/start";

    /// <summary>停止路径。</summary>
    public const string StopPath = "/api/stop";

    /// <summary>只跑某个工具：<c>/api/run?tool=bettergi</c>。</summary>
    public const string RunPath = "/api/run";

    private const string JsonType = "application/json; charset=utf-8";
    private const string TextType = "text/plain; charset=utf-8";

    private static readonly JsonSerializerOptions WriteOpt = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>生成一个新令牌（32 位十六进制，无连字符）。</summary>
    public static string NewToken() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 会话令牌是否可用：非空、长度合规、内容为十六进制。
    /// 用 <see cref="string.Equals(string?, string?, StringComparison)"/> 逐字节比，
    /// 不引入定时攻击面的复杂度（本机回环 + 用户自持令牌，够用）。
    /// </summary>
    public static bool IsUsableToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != TokenLength) return false;
        foreach (char c in token)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    /// <summary>已有令牌就沿用，没有（或不合规）就生成一个。空串进来 = 生成。</summary>
    public static string EnsureToken(string? current) => IsUsableToken(current) ? current! : NewToken();

    /// <summary>
    /// 纯函数路由：给定路径、查询串、请求头里的令牌、开关状态与应有令牌，算出该回什么、该干什么。
    /// 不做任何 IO，不读设置。
    /// </summary>
    /// <param name="path">绝对路径，形如 <c>/api/status</c>（末尾斜杠可有可无）。</param>
    /// <param name="providedToken">请求头里的令牌，没有就是 null。</param>
    /// <param name="apiEnabled">设置里「本机控制 API」是否打开。</param>
    /// <param name="expectedToken">设置里保存的令牌。</param>
    public static ApiPlan Plan(string? path, string? providedToken, bool apiEnabled, string? expectedToken)
    {
        string p = (path ?? "").TrimEnd('/');
        if (p.Length == 0) p = "/";

        if (!p.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            return new ApiPlan(404, ApiAction.None, "", Fail("未知路径：" + p));

        // 开关关着：一律 404，不暴露「这里有个 API 但你没权限」——那反而等于告诉扫描者该来爆破
        if (!apiEnabled) return new ApiPlan(404, ApiAction.None, "", Fail("未知路径：" + p));

        if (!IsUsableToken(expectedToken))
            return new ApiPlan(503, ApiAction.None, "", Fail("控制 API 缺少可用令牌，请在设置里重新生成"));

        if (!string.Equals(providedToken ?? "", expectedToken, StringComparison.Ordinal))
            return new ApiPlan(401, ApiAction.None, "", Fail("令牌不正确"));

        return p.ToLowerInvariant() switch
        {
            StatusPath => new ApiPlan(200, ApiAction.None, "", ""),
            StartPath => new ApiPlan(200, ApiAction.Start, "", ""),
            StopPath => new ApiPlan(200, ApiAction.Stop, "", ""),
            RunPath => new ApiPlan(200, ApiAction.Run, "", ""),
            _ => new ApiPlan(404, ApiAction.None, "", Fail("未知路径：" + p)),
        };
    }

    /// <summary>从查询串里取 <c>tool=</c>（大小写不敏感，值做去空格）。取不到返回空串。</summary>
    public static string ToolIdFromQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return "";
        string q = query.StartsWith("?", StringComparison.Ordinal) ? query[1..] : query;
        foreach (string pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals("tool", StringComparison.OrdinalIgnoreCase)) continue;
            string value = pair[(eq + 1)..];
            try { value = Uri.UnescapeDataString(value.Replace('+', ' ')); } catch { }
            return value.Trim();
        }
        return "";
    }

    /// <summary>
    /// 状态 JSON（纯函数，值由调用方传进来）。字段名稳定，脚本可依赖。
    /// </summary>
    public static string StatusJson(bool running, string currentTool, int doneTools, int unfinishedTools,
                                    int totalTools, string lastEvent)
    {
        var payload = new Dictionary<string, object?>
        {
            ["running"] = running,
            ["currentTool"] = currentTool ?? "",
            ["doneTools"] = doneTools,
            ["unfinishedTools"] = unfinishedTools,
            ["totalTools"] = totalTools,
            ["lastEvent"] = lastEvent ?? "",
            ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        return JsonSerializer.Serialize(payload, WriteOpt);
    }

    /// <summary>执行结果 JSON：<c>{"ok":true,"action":"start","message":"..."}</c>。</summary>
    public static string ActionJson(bool ok, string action, string message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["action"] = action ?? "",
            ["message"] = message ?? "",
        };
        return JsonSerializer.Serialize(payload, WriteOpt);
    }

    /// <summary>错误体：保持 JSON，免得脚本里还要区分返回格式。</summary>
    public static string Fail(string message) => ActionJson(false, "error", message ?? "");

    /// <summary>
    /// 只读面板 HTML（F7）。零 JS、零 npm、零构建：一个 meta refresh 自己刷。
    /// 只显示进度与最后一条事件，不含路径、令牌、配置——这页是可以被局域网里的人打开的。
    /// </summary>
    public static string PanelHtml(string title, bool running, string currentTool, int doneTools,
                                   int unfinishedTools, int totalTools, string lastEvent, int refreshSeconds)
    {
        int refresh = Math.Clamp(refreshSeconds, 2, 60);
        string state = running ? "正在跑" : "空闲";
        string current = string.IsNullOrWhiteSpace(currentTool) ? "—" : Escape(currentTool);
        string ev = string.IsNullOrWhiteSpace(lastEvent) ? "—" : Escape(lastEvent);
        int pct = totalTools <= 0 ? 0 : Math.Clamp((int)Math.Round(doneTools * 100.0 / totalTools), 0, 100);
        string updated = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var sb = new StringBuilder(2800);
        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">");
        sb.Append($"<meta http-equiv=\"refresh\" content=\"{refresh}\">");
        sb.Append($"<title>{Escape(title)}</title><style>");
        sb.Append(":root{--bg:#0D0F13;--card:#16191F;--card2:#1C2027;--border:#252A32;");
        sb.Append("--text:#EEF1F4;--muted:#A3ABB4;--dim:#8A929C;--brand:#3DDC84;");
        sb.Append("--brand-soft:rgba(61,220,132,.14);--brand-text:#3DDC84;");
        sb.Append("--idle:#7A8699;--idle-soft:rgba(122,134,153,.14);--track:#232830}");
        sb.Append("@media (prefers-color-scheme:light){:root{--bg:#EEF1F5;--card:#FFFFFF;--card2:#F6F8FA;");
        sb.Append("--border:#E1E6EB;--text:#12161B;--muted:#58616B;--dim:#667080;--brand:#0F9D58;");
        sb.Append("--brand-soft:#E2F3EB;--brand-text:#0C6B3D;--idle:#667080;--idle-soft:#ECEFF4;--track:#E4E9EF}}");
        sb.Append("*{box-sizing:border-box}body{margin:0;");
        sb.Append("padding:max(16px,env(safe-area-inset-top)) 16px max(24px,env(safe-area-inset-bottom));");
        sb.Append("background:var(--bg);color:var(--text);");
        sb.Append("font:14px/1.55 -apple-system,BlinkMacSystemFont,'Segoe UI','Microsoft YaHei',sans-serif}");
        sb.Append(".wrap{max-width:460px;margin:0 auto}");
        sb.Append(".hd{display:flex;justify-content:space-between;align-items:center;gap:12px;margin:2px 2px 14px}");
        sb.Append(".brand{display:flex;align-items:center;gap:8px;min-width:0}");
        sb.Append("h1{margin:0;font-size:16px;font-weight:600;letter-spacing:.02em;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}");
        sb.Append(".pill{display:inline-flex;align-items:center;gap:6px;padding:4px 10px;border-radius:999px;font-size:12px;font-weight:600;flex-shrink:0}");
        sb.Append(".pill.run{background:var(--brand-soft);color:var(--brand-text)}");
        sb.Append(".pill.idle{background:var(--idle-soft);color:var(--muted)}");
        sb.Append(".dot{width:7px;height:7px;border-radius:50%;background:currentColor}");
        sb.Append("@keyframes pulse{0%,100%{opacity:1;transform:scale(1)}50%{opacity:.45;transform:scale(.82)}}");
        sb.Append(".pill.run .dot{animation:pulse 1.6s ease-in-out infinite}");
        sb.Append(".card{background:var(--card);border:1px solid var(--border);border-radius:14px;padding:14px 16px;margin-bottom:10px}");
        sb.Append(".row{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:5px 0}");
        sb.Append(".k{color:var(--muted);font-size:12.5px;flex-shrink:0}");
        sb.Append(".v{font-variant-numeric:tabular-nums;text-align:right;overflow:hidden;text-overflow:ellipsis}");
        sb.Append(".prog{margin-top:10px}.prog-h{display:flex;justify-content:space-between;font-size:12px;color:var(--dim);margin-bottom:6px}");
        sb.Append(".bar{height:6px;border-radius:999px;background:var(--track);overflow:hidden}");
        sb.Append(".bar>i{display:block;height:100%;background:var(--brand);border-radius:999px}");
        sb.Append($".ev-box{{margin-top:8px;padding:10px 12px;border-radius:10px;background:var(--card2);border-left:3px solid {(running ? "var(--brand)" : "var(--border)")};word-break:break-word;line-height:1.55}}");
        sb.Append(".foot{color:var(--dim);font-size:11.5px;margin-top:14px;line-height:1.65;padding:0 2px}");
        sb.Append("</style></head><body><div class=\"wrap\">");

        sb.Append("<div class=\"hd\"><div class=\"brand\">");
        sb.Append("<svg width=\"18\" height=\"18\" viewBox=\"0 0 512 512\" fill=\"none\" aria-hidden=\"true\">");
        sb.Append("<path d=\"M408.6,215.1 A158,158 0 1 1 296.9,103.4\" stroke=\"var(--brand)\" stroke-width=\"46\" stroke-linecap=\"round\"/>");
        sb.Append("<circle cx=\"367.7\" cy=\"144.3\" r=\"46\" fill=\"var(--brand)\"/>");
        sb.Append("<path d=\"M256,268 L256,200 M256,268 L211,294\" stroke=\"var(--brand)\" stroke-width=\"16\" stroke-linecap=\"round\"/>");
        sb.Append("<circle cx=\"256\" cy=\"268\" r=\"9\" fill=\"var(--brand)\"/></svg>");
        sb.Append($"<h1>{Escape(title)}</h1></div>");
        sb.Append($"<span class=\"pill {(running ? "run" : "idle")}\"><i class=\"dot\"></i>{state}</span></div>");

        sb.Append("<div class=\"card\">");
        sb.Append($"<div class=\"row\"><span class=\"k\">当前</span><span class=\"v\">{current}</span></div>");
        sb.Append($"<div class=\"row\"><span class=\"k\">进度</span><span class=\"v\">{doneTools} / {totalTools}</span></div>");
        if (unfinishedTools > 0)
            sb.Append($"<div class=\"row\"><span class=\"k\">待跑</span><span class=\"v\">{unfinishedTools} 项</span></div>");
        sb.Append($"<div class=\"prog\"><div class=\"prog-h\"><span>完成度</span><span class=\"v\">{pct}%</span></div>");
        sb.Append($"<div class=\"bar\"><i style=\"width:{pct}%\"></i></div></div></div>");

        sb.Append("<div class=\"card\"><div class=\"k\">最后一条事件</div>");
        sb.Append($"<div class=\"ev-box\">{ev}</div></div>");

        sb.Append($"<div class=\"foot\">只读页面 · 更新于 {updated} · 每 {refresh} 秒自动刷新<br>");
        sb.Append("没有控制按钮，开跑或停止请在运行管家的电脑上操作。</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>HTML 转义（手写，避免引 System.Web）。</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>面板的 Content-Type。</summary>
    public const string PanelContentType = "text/html; charset=utf-8";

    /// <summary>JSON 的 Content-Type（供监听器设置响应头）。</summary>
    public const string JsonContentType = JsonType;

    /// <summary>纯文本的 Content-Type。</summary>
    public const string TextContentType = TextType;
}
