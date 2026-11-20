using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DragonButler;

/// <summary>
/// 一个够用的 Chrome DevTools Protocol 客户端（v1.31）。
///
/// 为什么要有这东西：米游社的 CK 只存在于「登录之后的浏览器里」。用户自己去翻
/// F12 → Application → Cookie 再抄一长串，是这套东西里最劝退的一步。既然本机
/// 一定有 Edge（Windows 11 自带），那就让 Edge 登录、我们读它的 cookie——
/// 用户只需要在窗口里登录一次。
///
/// 为什么不用 WebView2 的 NuGet 包：本项目零第三方依赖，这条底线不为了一个登录框破。
/// 走 CDP 只需要 WebSocket，System.Net.WebSockets 是框架自带的。
/// </summary>
public sealed class EdgeCdp : IDisposable
{
    private readonly Process _proc;
    private readonly ClientWebSocket _ws;
    private readonly int _port;
    private int _id;

    /// <summary>只用来问调试端口要 /json/list。WebClient 在 net8.0 是过时 API，别用。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    private EdgeCdp(Process proc, ClientWebSocket ws, int port)
    {
        _proc = proc;
        _ws = ws;
        _port = port;
    }

    private static string? Json_List(int port)
    {
        try
        {
            return Http.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    /// <summary>调试端口。挑了 9333 这种不常见的号，撞上就往下挪。</summary>
    public static int PickPort(int start = 9333)
    {
        for (int p = start; p < start + 20; p++)
        {
            try
            {
                using var c = new TcpClient();
                c.Connect(IPAddress.Loopback, p);
                c.Close();
            }
            catch
            {
                return p;   // 连不上 = 这个端口空着
            }
        }
        return start;
    }

    /// <summary>
    /// 起一个独立的 Edge 实例（自己的 user-data-dir，不碰用户日常浏览器的配置和 cookie），
    /// 打开指定地址，连上它的调试端口。失败返回 null，reason 里写人话。
    /// </summary>
    public static async Task<(EdgeCdp? Cdp, string Reason)> LaunchAsync(string url, TimeSpan? timeout = null)
    {
        string? exe = FindEdge();
        if (exe == null) return (null, "这台机器上找不到 Edge。可以不扫码，改成手填 cookie。");

        int port = PickPort();
        string profile = Path.Combine(Path.GetTempPath(), "dragon-login-" + port);
        try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch { }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            Arguments = string.Join(" ", new[]
            {
                "--remote-debugging-port=" + port,
                "--remote-allow-origins=*",
                "--user-data-dir=\"" + profile + "\"",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-features=msEdgeSidebarV2",
                "--window-size=460,760",
                "--app=" + url,
            }),
        };

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            return (null, "打不开浏览器：" + ex.Message);
        }
        if (proc == null) return (null, "打不开浏览器。");

        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(25));
        string? wsUrl = null;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(400);
            string? text = Json_List(port);
            if (text == null) continue;
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("type", out var t) && t.GetString() == "page"
                        && el.TryGetProperty("webSocketDebuggerUrl", out var w))
                    {
                        wsUrl = w.GetString();
                        break;
                    }
                }
            }
            catch { }
            if (wsUrl != null) break;
        }

        if (wsUrl == null)
        {
            try { proc.Kill(); } catch { }
            return (null, "浏览器起来了，但没等到调试端口。再试一次，或者改成手填。");
        }

        var ws = new ClientWebSocket();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
        }
        catch (Exception ex)
        {
            try { proc.Kill(); } catch { }
            return (null, "连不上浏览器的调试端口：" + ex.Message);
        }

        return (new EdgeCdp(proc, ws, port), "");
    }

    /// <summary>Edge 的常见安装位置。用户装 32 位版本的比想象中多，两处都要看。</summary>
    public static string? FindEdge()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>当前标签页的地址。</summary>
    public string? CurrentUrl
    {
        get
        {
            string? text = Json_List(_port);
            if (text == null) return null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.TryGetProperty("type", out var t) && t.GetString() == "page"
                        && el.TryGetProperty("url", out var u))
                        return u.GetString();
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// 发一条 CDP 命令并等它的回包。会话里别的事件（Network.enable 之后会持续推事件）
    /// 一律丢掉——我们要的只有 id 对得上的那一条。
    /// </summary>
    public async Task<JsonElement?> CallAsync(string method, object? parameters, CancellationToken token)
    {
        int id = Interlocked.Increment(ref _id);
        var payload = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
        if (parameters != null) payload["params"] = parameters;

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);

        var buffer = new byte[1 << 20];
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            frameCts.CancelAfter(TimeSpan.FromSeconds(8));

            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), frameCts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);

            string text = sb.ToString();
            if (!text.Contains("\"id\":" + id + ",") && !text.Contains("\"id\":" + id + "}")) continue;

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("result", out var result)) return result.Clone();
            return null;
        }
        return null;
    }

    /// <summary>读这个浏览器实例里的全部 cookie。返回 name → value（同名取域名更具体的）。</summary>
    public async Task<Dictionary<string, string>> CookiesAsync(CancellationToken token)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { await CallAsync("Network.enable", null, token); } catch { }

        var result = await CallAsync("Network.getAllCookies", null, token);
        if (result == null) return map;
        if (!result.Value.TryGetProperty("cookies", out var arr)) return map;

        foreach (var ck in arr.EnumerateArray())
        {
            string name = ck.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string value = ck.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            string domain = ck.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
            if (name.Length == 0 || value.Length == 0) continue;
            if (!domain.Contains("mihoyo", StringComparison.OrdinalIgnoreCase)
                && !domain.Contains("miyoushe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!map.ContainsKey(name)) map[name] = value;
        }
        return map;
    }

    /// <summary>在页面里跑一段 JS，把结果当成字符串取回来。取不到返回 null。</summary>
    public async Task<string?> EvalAsync(string expression, CancellationToken token)
    {
        var result = await CallAsync("Runtime.evaluate",
            new { expression, returnByValue = true }, token);
        if (result == null) return null;
        if (!result.Value.TryGetProperty("result", out var r)) return null;
        if (!r.TryGetProperty("value", out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    /// <summary>把页面截成 PNG。用作二维码的兜底：万一本站不给图，至少能看整页。</summary>
    public async Task<byte[]?> ScreenshotAsync(CancellationToken token)
    {
        var result = await CallAsync("Page.captureScreenshot", new { format = "png" }, token);
        if (result == null || !result.Value.TryGetProperty("data", out var d)) return null;
        try { return Convert.FromBase64String(d.GetString() ?? ""); } catch { return null; }
    }

    /// <summary>已经登录了吗：地址离开登录页、或者出现了 cookie_token。</summary>
    public async Task<bool> LooksLoggedInAsync(CancellationToken token)
    {
        string? url = CurrentUrl;
        if (url == null) return false;
        if (url.Contains("login", StringComparison.OrdinalIgnoreCase)) return false;

        var cookies = await CookiesAsync(token);
        return cookies.ContainsKey("cookie_token") || cookies.ContainsKey("ltuid")
            || cookies.ContainsKey("ltuid_v2") || cookies.ContainsKey("account_id");
    }

    public void Dispose()
    {
        try { _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(500); } catch { }
        try { _ws.Dispose(); } catch { }
        try { _proc.Kill(); } catch { }
    }
}
