using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DragonButler;

/// <summary>MCP 工具调用最终要落到的动作。和 <see cref="ApiAction"/> 分开，是因为 MCP 还多一个只读的查询。</summary>
public enum McpAction
{
    /// <summary>不需要动队列（握手、列工具、报错、或者只是查状态）。</summary>
    None = 0,
    Status,
    Start,
    Stop,
    Run,
}

/// <summary>
/// 一次 MCP 请求的判定结果。
///
/// <para>
/// <see cref="Body"/> 是非空的时候，响应体已经完整算好了（握手、工具清单、各种错误）。
/// <see cref="Body"/> 为空表示「需要调用方去执行 <see cref="Action"/>」，执行完用
/// <see cref="McpServer.ToolResult"/> / <see cref="McpServer.ToolError"/> 补出响应体。
/// </para>
/// </summary>
/// <param name="Status">HTTP 状态码。</param>
/// <param name="Body">完整响应体；需要执行时为空串。</param>
/// <param name="Action">需要调用方执行的动作。</param>
/// <param name="ToolId">要单跑的工具 id（仅 Run 时有值）。</param>
/// <param name="IdJson">请求 id 的 JSON 原文（会被原样回显）；通知类请求为 null。</param>
public sealed record McpPlan(int Status, string Body, McpAction Action, string ToolId, string? IdJson)
{
    /// <summary>这条请求要不要真的去动队列。</summary>
    public bool NeedsExecution => Action != McpAction.None;

    /// <summary>是不是通知（JSON-RPC 规定通知没有 id，也不该有响应体）。</summary>
    public bool IsNotification => IdJson == null;
}

/// <summary>
/// 最小 MCP 服务端（v1.11 / G3）：JSON-RPC 2.0 over HTTP，只实现
/// <c>initialize</c> / <c>tools/list</c> / <c>tools/call</c> 三个方法。
///
/// <para>
/// 为什么手写而不是引包：① 项目立身之本是「零 PackageReference」，CI 里有一道依赖审计会拦；
/// ② MCP 的这三个方法加起来不到两百行 JSON 构造，引一整个 SDK 反而更难审。
/// </para>
/// <para>
/// 三条与 <see cref="ControlApi"/> 一致的铁律：不新增监听器（路由挂在 WebhookListener 上）、
/// 判定与执行分离（<see cref="Plan"/> 是纯函数）、默认关（关着时连「这里有个 MCP」都不暴露）。
/// </para>
/// <para>
/// 鉴权复用控制 API 的那把 32 位令牌——**打开控制 API 就等于同时打开 MCP**。
/// 这样只有一扇门要看，不用维护两套凭据。
/// </para>
/// </summary>
public static class McpServer
{
    /// <summary>路由路径。</summary>
    public const string Path = "/mcp";

    /// <summary>支持的协议版本（客户端要别的版本时原样回它，MCP 约定由服务端决定最终版本）。</summary>
    public const string ProtocolVersion = "2024-11-05";

    /// <summary>服务端名（出现在 initialize 的 serverInfo 里）。</summary>
    public const string ServerName = "dragon-butler";

    /// <summary>默认协议版本，客户端没写时用它。</summary>
    private const string DefaultProtocol = ProtocolVersion;

    private static readonly JsonSerializerOptions WriteOpt = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 纯函数路由：给定请求体、开关、令牌，算出该回什么 / 该干什么。不做任何 IO，不读设置、不碰队列。
    /// </summary>
    /// <param name="body">请求体原文（POST 上来的 JSON）。</param>
    /// <param name="apiEnabled">设置里「本机控制 API」是否打开（MCP 与它共用一道闸）。</param>
    /// <param name="providedToken">请求头里的令牌。</param>
    /// <param name="expectedToken">设置里保存的令牌。</param>
    public static McpPlan Plan(string? body, bool apiEnabled, string? providedToken, string? expectedToken)
    {
        // 关着时一律 404 且不解释——沿用控制 API 的策略：不告诉扫描者「有门但你没钥匙」
        if (!apiEnabled) return Reply(404, ErrorBody(null, -32600, "未找到"), McpAction.None, "");

        if (!ControlApi.IsUsableToken(expectedToken))
            return Reply(503, ErrorBody(null, -32603, "服务端缺少可用令牌，请在设置里重新生成"), McpAction.None, "");

        if (!string.Equals(providedToken ?? "", expectedToken, StringComparison.Ordinal))
            return Reply(401, ErrorBody(null, -32600, "令牌不正确"), McpAction.None, "");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body ?? "");
        }
        catch
        {
            return Reply(400, ErrorBody(null, -32700, "请求体不是合法 JSON"), McpAction.None, "");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Reply(400, ErrorBody(null, -32600, "请求必须是 JSON 对象"), McpAction.None, "");

            string? idJson = ReadIdJson(root);
            string method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? ""
                : "";
            if (method.Length == 0)
                return Reply(400, ErrorBody(idJson, -32600, "缺少 method 字段"), McpAction.None, "");

            // 通知（没有 id）：只回 202 空体，不回 JSON-RPC 响应
            if (idJson == null)
            {
                if (method == "notifications/initialized") return Reply(202, "", McpAction.None, "");
                return Reply(202, "", McpAction.None, "");
            }

            return method switch
            {
                "initialize" => Reply(200, ResultBody(idJson, InitializeResult(root)), McpAction.None, ""),
                "tools/list" => Reply(200, ResultBody(idJson, ToolsListResult()), McpAction.None, ""),
                "ping" => Reply(200, ResultBody(idJson, new Dictionary<string, object?>()), McpAction.None, ""),
                "tools/call" => PlanToolCall(idJson, root),
                _ => Reply(200, ErrorBody(idJson, -32601, "不支持的方法：" + method), McpAction.None, ""),
            };
        }
    }

    /// <summary>
    /// 请求体里 <c>id</c> 的 JSON 原文（数字、字符串都可能，原样回显最稳）。
    /// 没有 id 字段时返回 null——那就是一条通知。
    /// </summary>
    private static string? ReadIdJson(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id)) return null;
        if (id.ValueKind == JsonValueKind.Null) return "null";
        return id.GetRawText();
    }

    /// <summary>处理 tools/call：认得出的交给调用方执行，认不出的按工具错误返回。</summary>
    private static McpPlan PlanToolCall(string idJson, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var ps) || ps.ValueKind != JsonValueKind.Object)
            return Reply(200, ErrorBody(idJson, -32602, "tools/call 缺少 params"), McpAction.None, "");

        string name = ps.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? ""
            : "";
        if (name.Length == 0)
            return Reply(200, ErrorBody(idJson, -32602, "tools/call 缺少 name"), McpAction.None, "");

        string toolId = "";
        if (ps.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("toolId", out var t) && t.ValueKind == JsonValueKind.String)
            toolId = (t.GetString() ?? "").Trim();

        switch (name)
        {
            case "butler_status":
                // 查状态不需要执行任何东西，但正文得由调用方填（它才知道当前跑没跑）
                return new McpPlan(200, "", McpAction.Status, "", idJson);
            case "butler_start":
                return new McpPlan(200, "", McpAction.Start, "", idJson);
            case "butler_stop":
                return new McpPlan(200, "", McpAction.Stop, "", idJson);
            case "butler_run":
                if (toolId.Length == 0)
                    return Reply(200, ErrorBody(idJson, -32602, "butler_run 需要参数 toolId"), McpAction.None, "");
                return new McpPlan(200, "", McpAction.Run, toolId, idJson);
            default:
                // 未知工具按「工具执行失败」返回而不是协议错误：客户端会把它显示给用户，
                // 协议错误则容易被当成连接问题
                return Reply(200, ToolErrorBody(idJson, "未知工具：" + name), McpAction.None, "");
        }
    }

    /// <summary>initialize 的 result：协议版本、能力、服务端身份。</summary>
    private static Dictionary<string, object?> InitializeResult(JsonElement root)
    {
        // 客户端报的版本我们照收（本服务只用到 tools，没有版本敏感的特性）
        string proto = DefaultProtocol;
        if (root.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object
            && ps.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String)
        {
            string asked = pv.GetString() ?? "";
            if (asked.Length > 0) proto = asked;
        }

        return new Dictionary<string, object?>
        {
            ["protocolVersion"] = proto,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["tools"] = new Dictionary<string, object>(),
            },
            ["serverInfo"] = new Dictionary<string, object?>
            {
                ["name"] = ServerName,
                ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            },
        };
    }

    /// <summary>工具清单。四个工具，一一对应控制 API 的四个动作。</summary>
    public static List<Dictionary<string, object?>> ToolDefinitions()
    {
        var noArgs = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
            ["additionalProperties"] = false,
        };

        return new List<Dictionary<string, object?>>
        {
            new()
            {
                ["name"] = "butler_status",
                ["description"] = "查一条龙管家当前状态：跑没跑、正在跑哪一项、今天跑成几项、还剩几项、最后一条事件。只读，不改任何东西。",
                ["inputSchema"] = noArgs,
            },
            new()
            {
                ["name"] = "butler_start",
                ["description"] = "按队列顺序开跑整条一条龙。如果已经在跑会返回失败而不是排队再来一次。",
                ["inputSchema"] = noArgs,
            },
            new()
            {
                ["name"] = "butler_stop",
                ["description"] = "停止当前队列。正在跑的工具会被终止，剩余项不再执行。",
                ["inputSchema"] = noArgs,
            },
            new()
            {
                ["name"] = "butler_run",
                ["description"] = "只跑某一个工具，不影响队列顺序。toolId 常见值：bettergi、march7th、ok-ww，也可以用自定义工具的 id。",
                ["inputSchema"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["toolId"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "工具的 id，例如 bettergi",
                        },
                    },
                    ["required"] = new List<string> { "toolId" },
                    ["additionalProperties"] = false,
                },
            },
        };
    }

    private static Dictionary<string, object?> ToolsListResult() => new()
    {
        ["tools"] = ToolDefinitions(),
    };

    /// <summary>工具执行成功：包成 MCP 的 content 数组（纯文本）。</summary>
    public static string ToolResult(string? idJson, string text) =>
        ResultBody(idJson, new Dictionary<string, object?>
        {
            ["content"] = new List<Dictionary<string, object?>>
            {
                new() { ["type"] = "text", ["text"] = text ?? "" },
            },
            ["isError"] = false,
        });

    /// <summary>工具执行失败：<c>isError: true</c>，让客户端把话原样显示给用户。</summary>
    public static string ToolError(string? idJson, string text) => ToolErrorBody(idJson, text);

    private static string ToolErrorBody(string? idJson, string text) =>
        ResultBody(idJson, new Dictionary<string, object?>
        {
            ["content"] = new List<Dictionary<string, object?>>
            {
                new() { ["type"] = "text", ["text"] = text ?? "" },
            },
            ["isError"] = true,
        });

    /// <summary>把状态 JSON 变成一句人话——AI 读 JSON 没问题，但用户在聊天窗口里看到的是这句话。</summary>
    public static string StatusText(bool running, string currentTool, int doneTools, int unfinishedTools, int totalTools, string lastEvent)
    {
        var sb = new StringBuilder(180);
        sb.Append(running ? "正在跑" : "空闲");
        if (!string.IsNullOrWhiteSpace(currentTool)) sb.Append("，当前：").Append(currentTool);
        sb.Append($"。今天 {doneTools}/{totalTools} 项已跑成");
        if (unfinishedTools > 0) sb.Append($"，还剩 {unfinishedTools} 项");
        sb.Append('。');
        if (!string.IsNullOrWhiteSpace(lastEvent)) sb.Append("最后一条事件：").Append(lastEvent).Append('。');
        return sb.ToString();
    }

    private static McpPlan Reply(int status, string body, McpAction action, string toolId) =>
        new(status, body, action, toolId, null);

    private static string ResultBody(string? idJson, object result) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = RawId(idJson),
            ["result"] = result,
        }, WriteOpt);

    private static string ErrorBody(string? idJson, int code, string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = RawId(idJson),
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message ?? "",
            },
        }, WriteOpt);

    /// <summary>
    /// 把 id 的 JSON 原文塞回响应里。用 <see cref="JsonDocument"/> 解析一次再交回去，
    /// 这样 <c>1</c> 还是数字、<c>"abc"</c> 还是字符串，不会被统一成字符串（那会让部分客户端认不出自己的请求）。
    /// </summary>
    private static object? RawId(string? idJson)
    {
        if (string.IsNullOrEmpty(idJson)) return null;
        try
        {
            using var d = JsonDocument.Parse(idJson);
            return d.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
