using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using DragonButler;

namespace CoordinatorProbe;

// v1.45.3 / #15：探针按功能组拆成多个 partial 文件，单文件 ≤ 20 KB。
// 断言正文一字未改，只换了所在文件。
internal static partial class Program
{
    /// <summary>
    /// v1.11 / G3：MCP（JSON-RPC over HTTP）。
    /// 判定逻辑是纯函数，所以这里全部离线验：握手、列工具、四种工具调用、令牌、各种畸形请求。
    /// 畸形请求那几条是重点——MCP 客户端五花八门，收到奇怪输入时**回一个 JSON-RPC 错误，
    /// 而不是 500 或者卡死**，是这层接口能不能用的分界线。
    /// </summary>
    private static int RunMcp()
    {
        var fails = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + detail : "")}");
            if (!ok) fails.Add(name);
        }

        const string token = "0123456789abcdef0123456789abcdef";
        const bool on = true;

        // 本地的检查助手：跑一次 Plan，顺便把 JSON 体解出来
        System.Text.Json.JsonElement Ask(string body, string? tk = token, bool enabled = on)
        {
            var plan = McpServer.Plan(body, enabled, tk, token);
            if (plan.Body.Length == 0) return default;
            using var d = System.Text.Json.JsonDocument.Parse(plan.Body);
            return d.RootElement.Clone();
        }

        // ---- 1) 开关与令牌：关着时连存在都不暴露 ----
        var closed = McpServer.Plan(Request("initialize"), apiEnabled: false, token, token);
        Check("关闭时 404", closed.Status == 404, closed.Status.ToString());
        Check("关闭时不执行任何动作", !closed.NeedsExecution);
        var badToken = McpServer.Plan(Request("initialize"), on, "wrong", token);
        Check("令牌不对 401", badToken.Status == 401, badToken.Status.ToString());
        var noToken = McpServer.Plan(Request("initialize"), on, null, token);
        Check("没带令牌 401", noToken.Status == 401);
        var noServerToken = McpServer.Plan(Request("initialize"), on, token, "");
        Check("服务端没令牌 503", noServerToken.Status == 503, noServerToken.Status.ToString());

        // ---- 2) initialize 握手 ----
        var init = Ask(Request("initialize"));
        Check("握手/有 result", init.TryGetProperty("result", out var initResult));
        Check("握手/协议版本",
            initResult.TryGetProperty("protocolVersion", out var pv) && pv.GetString() == McpServer.ProtocolVersion,
            initResult.TryGetProperty("protocolVersion", out var pv2) ? pv2.GetString() ?? "" : "缺少");
        Check("握手/声明 tools 能力",
            initResult.TryGetProperty("capabilities", out var caps)
            && caps.TryGetProperty("tools", out _));
        Check("握手/serverInfo 名字",
            initResult.TryGetProperty("serverInfo", out var si)
            && si.TryGetProperty("name", out var sn) && sn.GetString() == McpServer.ServerName);
        var init2 = Ask("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2099-01-01\"}}");
        Check("握手/客户端报别的版本就照回它",
            init2.GetProperty("result").GetProperty("protocolVersion").GetString() == "2099-01-01");

        // ---- 3) id 原样回显（数字 id 不该变成字符串） ----
        var numId = Ask("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\"}");
        Check("id/数字原样回数字", numId.GetProperty("id").ValueKind == System.Text.Json.JsonValueKind.Number,
            numId.GetProperty("id").ValueKind.ToString());
        var strId = Ask("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"tools/list\"}");
        Check("id/字符串原样回字符串", strId.GetProperty("id").ValueKind == System.Text.Json.JsonValueKind.String);

        // ---- 4) tools/list ----
        var list = Ask(Request("tools/list")).GetProperty("result").GetProperty("tools");
        var names = list.EnumerateArray().Select(t => t.GetProperty("name").GetString() ?? "").OrderBy(x => x).ToList();
        Check("工具/正好四个", list.GetArrayLength() == 4, list.GetArrayLength().ToString());
        Check("工具/名字齐全",
            names.SequenceEqual(new[] { "butler_run", "butler_start", "butler_status", "butler_stop" }),
            string.Join(",", names));
        bool allHaveSchema = list.EnumerateArray().All(t =>
            t.TryGetProperty("inputSchema", out var sc)
            && sc.TryGetProperty("type", out var ty) && ty.GetString() == "object");
        Check("工具/都有 object 入参 schema", allHaveSchema);
        bool allHaveDesc = list.EnumerateArray().All(t =>
            t.TryGetProperty("description", out var de) && (de.GetString() ?? "").Length > 10);
        Check("工具/都有像样的描述（AI 靠它选工具）", allHaveDesc);
        var runTool = list.EnumerateArray().First(t => t.GetProperty("name").GetString() == "butler_run");
        Check("工具/butler_run 要求 toolId",
            runTool.GetProperty("inputSchema").GetProperty("required").EnumerateArray()
                   .Any(r => r.GetString() == "toolId"));

        // ---- 5) tools/call 路由 ----
        var statusCall = McpServer.Plan(Call("butler_status", null), on, token, token);
        Check("调用/查状态要执行", statusCall.Action == McpAction.Status && statusCall.NeedsExecution);
        var startCall = McpServer.Plan(Call("butler_start", null), on, token, token);
        Check("调用/开跑", startCall.Action == McpAction.Start);
        var stopCall = McpServer.Plan(Call("butler_stop", null), on, token, token);
        Check("调用/停止", stopCall.Action == McpAction.Stop);
        var runCall = McpServer.Plan(Call("butler_run", "bettergi"), on, token, token);
        Check("调用/单跑带 toolId", runCall.Action == McpAction.Run && runCall.ToolId == "bettergi");

        var runNoId = Ask(Call("butler_run", null));
        Check("调用/单跑缺 toolId 报错",
            runNoId.TryGetProperty("error", out var e1) && e1.GetProperty("code").GetInt32() == -32602);

        var unknown = Ask(Call("butler_delete", null));
        Check("调用/未知工具是工具错误而非协议错误",
            unknown.GetProperty("result").GetProperty("isError").GetBoolean());

        // ---- 6) 畸形请求：一律 JSON-RPC 错误，不 500、不崩 ----
        var notJson = Ask("{ this is not json");
        Check("畸形/坏 JSON 回 -32700",
            notJson.TryGetProperty("error", out var e2) && e2.GetProperty("code").GetInt32() == -32700,
            notJson.GetProperty("error").GetProperty("code").GetInt32().ToString());

        var notObject = Ask("[1,2,3]");
        Check("畸形/非对象回 -32600",
            notObject.TryGetProperty("error", out var e3) && e3.GetProperty("code").GetInt32() == -32600);

        var noMethod = Ask("{\"jsonrpc\":\"2.0\",\"id\":1}");
        Check("畸形/缺 method 回 -32600",
            noMethod.TryGetProperty("error", out var e4) && e4.GetProperty("code").GetInt32() == -32600);

        var unknownMethod = Ask(Request("tools/delete"));
        Check("畸形/未知方法回 -32601",
            unknownMethod.TryGetProperty("error", out var e5) && e5.GetProperty("code").GetInt32() == -32601,
            unknownMethod.GetProperty("error").GetProperty("code").GetInt32().ToString());

        var emptyBody = McpServer.Plan("", on, token, token);
        Check("畸形/空请求体也是 400 不是崩", emptyBody.Status == 400);
        var nullBody = McpServer.Plan(null, on, token, token);
        Check("畸形/null 请求体也是 400", nullBody.Status == 400);

        var noParams = Ask("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"}");
        Check("畸形/tools/call 缺 params 回 -32602",
            noParams.TryGetProperty("error", out var e6) && e6.GetProperty("code").GetInt32() == -32602);

        var noName = Ask("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{}}");
        Check("畸形/tools/call 缺 name 回 -32602",
            noName.TryGetProperty("error", out var e7) && e7.GetProperty("code").GetInt32() == -32602);

        // ---- 7) 通知（没有 id）：不回 JSON-RPC 响应 ----
        var notif = McpServer.Plan("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", on, token, token);
        Check("通知/不执行动作", !notif.NeedsExecution);
        Check("通知/空体或 202", notif.Body.Length == 0 || notif.Status == 202, $"status={notif.Status} body={notif.Body.Length}");

        // ---- 8) 响应体结构：合法的 JSON-RPC 2.0 ----
        var body = McpServer.Plan(Request("tools/list"), on, token, token).Body;
        using var parsed = System.Text.Json.JsonDocument.Parse(body);
        var root2 = parsed.RootElement;
        Check("结构/带 jsonrpc 2.0",
            root2.TryGetProperty("jsonrpc", out var v) && v.GetString() == "2.0");
        Check("结构/有 id", root2.TryGetProperty("id", out _));
        Check("结构/result 与 error 不共存",
            !(root2.TryGetProperty("result", out _) && root2.TryGetProperty("error", out _)));

        // ---- 9) 工具结果与状态文案 ----
        string ok = McpServer.ToolResult("1", "已请求开跑");
        using var okDoc = System.Text.Json.JsonDocument.Parse(ok);
        var okRes = okDoc.RootElement.GetProperty("result");
        Check("结果/isError=false", !okRes.GetProperty("isError").GetBoolean());
        Check("结果/正文是 text 类型",
            okRes.GetProperty("content")[0].GetProperty("type").GetString() == "text"
            && okRes.GetProperty("content")[0].GetProperty("text").GetString() == "已请求开跑");

        string err = McpServer.ToolError("1", "队列没在运行");
        using var errDoc = System.Text.Json.JsonDocument.Parse(err);
        Check("结果/失败时 isError=true", errDoc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());

        string st = McpServer.StatusText(false, "", 2, 1, 3, "一条龙结束");
        Check("状态文案/空闲", st.StartsWith("空闲", StringComparison.Ordinal), st);
        Check("状态文案/含进度", st.Contains("2/3", StringComparison.Ordinal));
        Check("状态文案/含剩余", st.Contains("还剩 1 项", StringComparison.Ordinal));
        Check("状态文案/含最后事件", st.Contains("一条龙结束", StringComparison.Ordinal));
        string stRun = McpServer.StatusText(true, "BetterGI", 0, 3, 3, "");
        Check("状态文案/在跑且带当前项",
            stRun.StartsWith("正在跑", StringComparison.Ordinal) && stRun.Contains("BetterGI", StringComparison.Ordinal), stRun);

        // ---- 10) 源码级约束：没有新增监听器、没有引包 ----
        try
        {
            string mcpSrc = ReadRepoSources("Dragon Butler/McpServer.cs");
            Check("源码/MCP 不自己开监听器",
                !mcpSrc.Contains("HttpListener", StringComparison.Ordinal));
            Check("源码/MCP 不引包",
                !mcpSrc.Contains("using System.Net;", StringComparison.Ordinal)
                && !mcpSrc.Contains("using System.Net.Http;", StringComparison.Ordinal));

            // 把整库所有 .cs 扫一遍，确认 new HttpListener 仍只出现在 WebhookListener 里
            // 注意要带左括号：不带的话会把 ControlApi.cs / WebhookHub.cs 里
            // 「全库 new HttpListener 仅存在于…」这类注释也算成命中
            string root = Path.GetDirectoryName(typeof(QueueService).Assembly.Location) ?? ".";
            for (int i = 0; i < 5; i++) root = Path.GetDirectoryName(root) ?? root;
            var offenders = Directory.GetFiles(Path.Combine(root, "Dragon Butler"), "*.cs")
                .Where(f => File.ReadAllText(f).Contains("new HttpListener(", StringComparison.Ordinal))
                .Select(f => Path.GetFileName(f)!)
                .ToList();
            Check("源码/new HttpListener 仍只有一处文件",
                offenders.Count == 1 && offenders[0] == "WebhookListener.cs",
                string.Join(",", offenders));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  SKIP 源码扫描失败：" + ex.Message);
        }

        Console.WriteLine($"RESULT mcp fails={fails.Count}");
        return fails.Count == 0 ? 0 : 1;
    }

}
