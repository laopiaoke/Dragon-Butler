using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace DragonButler;

/// <summary>通知分级：Info=完成/启动；Warning=异常/卡死；Critical=BGI崩、反复崩</summary>
public enum NotifyLevel { Info, Warning, Critical }

/// <summary>推送渠道（与 BetterGI 全局通知支持的渠道对齐）</summary>
public enum PushChannel
{
    Off,        // 不推送（默认：跑完不弹任何东西，界面里自己看结果）
    Toast,      // Windows 系统通知
    Balloon,    // 仅托盘气泡
    Wecom,      // 企业微信群机器人
    Feishu,     // 飞书自定义机器人
    DingTalk,   // 钉钉自定义机器人（支持加签）
    Telegram,   // Telegram Bot
    Bark,       // Bark（iOS）
    Email,      // SMTP 邮件
    Xxtui,      // xxtui 聚合推送
    Webhook,    // 自定义 Webhook（POST JSON）
    WebSocket,  // WebSocket 推送（连上发一条 JSON 即关闭，与 BGI 行为一致）
}

/// <summary>
/// 通知服务（SPBGI）：本地 + 远程共 11 个渠道，默认不推送。
/// 不推送：什么都不弹，结果只留在界面与日志里。
/// 本地：Toast（失败降级托盘气泡）/ 仅托盘气泡。
/// 远程：企业微信、飞书、钉钉、Telegram、Bark、邮件、xxtui、自定义 Webhook、WebSocket。
/// 任何失败只记日志，绝不影响主流程；远程推送异步执行，不阻塞 UI。
///
/// v1.25：默认从「Windows 通知」改成「不推送」。挂机场景下 Windows 通知会在运行途中
/// 一条条中断桌面（每次至少一条，队列长了就是一串），而它又不会把结果带到人不在的地方。
/// 真正有用的是「推到手机」，那需要人自己填一次地址——所以默认留给不打扰，
/// 想要的人第一次打开向导时就会看见选项。
/// </summary>
public static class NotifyService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>渠道清单（界面按此顺序展示）</summary>
    public static readonly (PushChannel Channel, string Name)[] AllChannels =
    {
        (PushChannel.Off, "不推送"),
        (PushChannel.Toast, "Windows 通知"),
        (PushChannel.Balloon, "托盘气泡"),
        (PushChannel.Wecom, "企业微信"),
        (PushChannel.Feishu, "飞书"),
        (PushChannel.DingTalk, "钉钉"),
        (PushChannel.Telegram, "Telegram"),
        (PushChannel.Bark, "Bark"),
        (PushChannel.Email, "邮件"),
        (PushChannel.Xxtui, "xxtui"),
        (PushChannel.Webhook, "自定义 Webhook"),
        (PushChannel.WebSocket, "WebSocket"),
    };

    /// <summary>Windows toast 脚本（WinRT API，经 powershell 调用；无需第三方依赖）</summary>
    private const string ToastScript = @"
$t=$env:SBGI_TITLE; $m=$env:SBGI_MSG
if (-not $t) { $t = '一条龙管家' }
if (-not $m) { $m = '通知' }
[void][Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]
[void][Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom,ContentType=WindowsRuntime]
$x=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
$s=$x.GetElementsByTagName('text')
$s.Item(0).AppendChild($x.CreateTextNode($t))|Out-Null
$s.Item(1).AppendChild($x.CreateTextNode($m))|Out-Null
$toast=[Windows.UI.Notifications.ToastNotification]::new($x)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Dragon Butler').Show($toast)
Write-Output 'TOAST_OK'
";

    /// <summary>设置的字符串 → 渠道枚举</summary>
    public static PushChannel ParseChannel(string? mode) => mode switch
    {
        "Off" => PushChannel.Off,
        "Wecom" => PushChannel.Wecom,
        "Feishu" => PushChannel.Feishu,
        "DingTalk" => PushChannel.DingTalk,
        "Telegram" => PushChannel.Telegram,
        "Bark" => PushChannel.Bark,
        "Email" => PushChannel.Email,
        "Xxtui" => PushChannel.Xxtui,
        "Webhook" => PushChannel.Webhook,
        "WebSocket" => PushChannel.WebSocket,
        "Balloon" => PushChannel.Balloon,
        "Toast" => PushChannel.Toast,
        // 空/没见过的值一律当「不推送」：老配置升级上来时字段是缺的，
        // 缺的时候弹一堆 Windows 通知才是最坏的结果。
        _ => PushChannel.Off,
    };

    public static string NameOf(PushChannel c)
    {
        foreach (var (ch, name) in AllChannels) if (ch == c) return name;
        return c.ToString();
    }

    /// <summary>弹一条通知（按设置的渠道分发）</summary>
    public static void Show(NotifyLevel level, string title, string message)
    {
        try
        {
            var ch = ParseChannel(AppState.Settings.NotifyMode);

            // 不推送：连降级气泡都不弹。选了这个就是不想被任何一条打断。
            if (ch == PushChannel.Off) return;

            if (ch == PushChannel.Toast)
            {
                // v1.43 / 任务五：toast 失败降级到气泡，气泡再失败也不能静默结束。
                // 两条路都断了，用户就是一条消息都没收到——这件事必须留痕。
                if (!ShowToast(title, message) && !ShowBalloonOrRecord(level, title, message))
                    NoticeCenter.AddError("测试通知发不出去",
                        "toast 和托盘气泡都没能弹出来。系统通知可能被关掉了，或者托盘图标还没建好。", "");
                return;
            }
            if (ch == PushChannel.Balloon)
            {
                ShowBalloonOrRecord(level, title, message);
                return;
            }

            // 远程渠道：默认只推异常可以少打扰；关闭开关则完成/启动也推
            if (AppState.Settings.NotifyOnlyImportant && level == NotifyLevel.Info) return;
            _ = SendAndLogAsync(ch, level, title, message);
        }
        catch { }
    }

    // ===== v1.43 / 任务五：远程推送的有限重试 =====
    //
    // 原来一次失败就结束：网络抖一下（地铁、Wi-Fi 换信道、路由器重启），
    // 「今晚跑完了」这条就永远丢了，日志里只有一行「推送失败」，人不在电脑前根本看不到。
    //
    // 重试是业务层的、有限的、指数退避的：1s / 3s / 7s，第四次是终局。
    // 不新增设置项——「要不要重试」不该是用户的负担，「重试几次」更不该。
    //
    // v1.44 / 修复 #6（D21）：这三个数字只在这一处写。原先叫 RetryDelaysMs，
    // 名字太泛，谁都能再建一份；现在叫 PushRetryDelaysMs 并公开出来，
    // 探针能直接断言「节奏就是这三个」，也让「哪里还有一套自己的重试间隔」一眼可查。
    //
    // 注意：<b>不做配置化</b>。用户词汇里没有「退避间隔」，把它做成设置项，
    // 等于把一个不该由用户决定的技术细节摊到设置页上；节奏该由我们定，
    // 用户只需要知道「没发出去的话，大概十几秒内会再试三次」。
    public static readonly int[] PushRetryDelaysMs = { 1000, 3000, 7000 };

    /// <summary>应用退出时喊停，别让重试等待把进程拖住。</summary>
    private static readonly System.Threading.CancellationTokenSource RetryCts = new();

    /// <summary>v1.43 / 任务五：退出时调一次，取消所有还在等待的重试。</summary>
    public static void StopPendingSends()
    {
        try { RetryCts.Cancel(); } catch { }
    }

    private static async Task SendAndLogAsync(PushChannel ch, NotifyLevel level, string title, string message)
    {
        string err = "";
        bool ok = false;
        for (int attempt = 0; attempt <= PushRetryDelaysMs.Length; attempt++)
        {
            (ok, err) = await SendAsync(ch, level, title, message).ConfigureAwait(false);
            if (ok) break;
            if (attempt >= PushRetryDelaysMs.Length) break;
            if (!IsRetriable(err)) break;
            LogService.LogEvent($"推送失败（{NameOf(ch)}），{PushRetryDelaysMs[attempt] / 1000} 秒后重试（第 {attempt + 1} 次）：{err}");
            try { await Task.Delay(PushRetryDelaysMs[attempt], RetryCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        if (ok)
        {
            LogService.LogEvent($"推送成功（{NameOf(ch)}）：{title}");
            return;
        }

        LogService.LogEvent($"推送最终失败（{NameOf(ch)}）：{err}");
        try { AppState.PublishLog($"✗ {NameOf(ch)}推送失败：{err}"); } catch { }
        // v1.44 / 修复 #6（D21）：三次用尽之后，两条留痕都要有，缺一不可。
        //
        // ① 「今日」那一栏（DecisionLog）——用户第二天早上翻「今日」时，
        //    看到的是「推送没送到」这件事本身，而不是一条线索都没有。
        // ② 消息中心（NoticeCenter）——日志是给排查用的，人不会主动翻；
        //    消息中心是「昨晚出过什么事」的清单，跑完的结果丢了要能在这里看见。
        //
        // 只做其中一条等于没做：只有日志没人看，只有弹窗第二天就关了。
        // 留痕里必须带渠道名和最终错误摘要——「推送失败」四个字对用户没有任何用，
        // 「企业微信推送没送到：HTTP 401 令牌失效」才能让下一步动作自己浮出来。
        string shortErr = Snippet(err);
        DecisionLog.Add(DecisionSource.System, $"推送没送到（{NameOf(ch)}）",
            $"重试三次都失败了，最后一次说：{shortErr}。这一条「{title}」今晚没发出去。");
        NoticeCenter.AddError($"{NameOf(ch)} 推送没送到",
            $"「{title}」这条没能发出去：{err}。网络恢复后可以手动重发，或者换个渠道。", err);
    }

    /// <summary>
    /// v1.43 / 任务五：值不值得重试。
    ///
    /// <para>
    /// 只对「过一会儿可能就好了」的错重试：超时、连接被拒、5xx。
    /// 「没填地址」「401 令牌不对」这种重试一万次也一样，白等七秒还耽误下一条。
    /// </para>
    /// </summary>
    private static bool IsRetriable(string err)
    {
        if (string.IsNullOrWhiteSpace(err)) return true;
        if (err.Contains("尚未填写", StringComparison.Ordinal)) return false;
        if (err.Contains("不支持远程发送", StringComparison.Ordinal)) return false;
        if (err.Contains("HTTP 4", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>v1.43 / 任务五：弹气泡并留痕；返回气泡是否真的弹出来了。</summary>
    private static bool ShowBalloonOrRecord(NotifyLevel level, string title, string message)
    {
        bool shown = App.Instance?.ShowBalloon(level, title, message) == true;
        if (!shown) LogService.LogEvent($"托盘气泡没弹出来（{title}）");
        return shown;
    }

    /// <summary>发送 Windows 系统通知（toast）；成功返回 true，失败返回 false 供降级</summary>
    public static bool ShowToast(string title, string message)
    {
        try
        {
            string ps1 = Path.Combine(Path.GetTempPath(), "DragonButler_toast.ps1");
            File.WriteAllText(ps1, ToastScript, new UTF8Encoding(false));
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{ps1}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables["SBGI_TITLE"] = title ?? "";
            psi.EnvironmentVariables["SBGI_MSG"] = message ?? "";
            using var p = Process.Start(psi);
            if (p == null) return false;
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return p.ExitCode == 0 && outp.Contains("TOAST_OK");
        }
        catch { return false; }
    }

    // v1.35：ShowTrayNote / ResetTrayNote 一起删了。
    //
    // v1.28 给「关窗口还在后台」这条提示加过闸，但闸的判据是「通知渠道 != 不推送」——
    // 而「关窗口」跟「跑完了要不要通知我」是两件事。选了 Windows 通知的人，在意的是
    // 跑完的结果，不是每次关窗都被系统弹一次「还在后台」。
    //
    // 判据应该只有一条：这一刻用户就坐在屏幕前，看着窗口消失，他知道自己关了什么。
    // 托盘图标还在，双击能开回来——不需要谁告诉他。这条提示整个不要了。

    /// <summary>发送测试消息（默认当前渠道，也可指定渠道）</summary>
    public static async Task<(bool ok, string err)> SendTestAsync(PushChannel? channel = null)
    {
        var ch = channel ?? ParseChannel(AppState.Settings.NotifyMode);
        string title = "一条龙管家";
        string body = $"测试消息：{NameOf(ch)} 通道连接成功（{DateTime.Now:HH:mm:ss}）";

        if (ch == PushChannel.Off)
            return (false, "现在是「不推送」，先在上面选一个渠道再测");

        if (ch == PushChannel.Toast)
        {
            bool ok = ShowToast(title, body);
            return ok ? (true, "") : (false, "toast 发送失败，请检查系统通知设置，或改用托盘气泡");
        }
        if (ch == PushChannel.Balloon)
        {
            // v1.43 / 任务五：这里原来固定 return (true, "")，于是托盘还没建好时
            // 测试也显示「连接成功」——用户在屏幕前等一个永远不来的气泡。
            bool shown = App.Instance?.ShowBalloon(NotifyLevel.Info, title, body) == true;
            return shown ? (true, "") : (false, "气泡没弹出来：托盘图标可能还没建好，或系统把通知关掉了");
        }
        return await SendAsync(ch, NotifyLevel.Info, title, body);
    }

    /// <summary>按渠道发送；返回失败原因供界面提示</summary>
    public static async Task<(bool ok, string err)> SendAsync(PushChannel ch, NotifyLevel level, string title, string message)
    {
        try
        {
            var s = AppState.Settings;
            string text = string.IsNullOrWhiteSpace(title) ? message : title + "\n" + message;
            switch (ch)
            {
                case PushChannel.Wecom:
                    if (string.IsNullOrWhiteSpace(s.NotifyWecomUrl)) return (false, "尚未填写企业微信 Webhook 地址");
                    return await PostJsonAsync(s.NotifyWecomUrl, BuildWecomJson(title, message));

                case PushChannel.Feishu:
                    if (string.IsNullOrWhiteSpace(s.NotifyFeishuUrl)) return (false, "尚未填写飞书 Webhook 地址");
                    return await PostJsonAsync(s.NotifyFeishuUrl, BuildFeishuJson(title, message, s.NotifyFeishuSecret));

                case PushChannel.DingTalk:
                    if (string.IsNullOrWhiteSpace(s.NotifyDingUrl)) return (false, "尚未填写钉钉 Webhook 地址");
                    return await PostJsonAsync(BuildDingUrl(s.NotifyDingUrl, s.NotifyDingSecret), BuildDingJson(title, message));

                case PushChannel.Telegram:
                    if (string.IsNullOrWhiteSpace(s.NotifyTelegramToken) || string.IsNullOrWhiteSpace(s.NotifyTelegramChat))
                        return (false, "尚未填写 Telegram Bot Token / Chat ID");
                    return await PostJsonAsync(
                        $"https://api.telegram.org/bot{s.NotifyTelegramToken.Trim()}/sendMessage",
                        BuildTelegramJson(s.NotifyTelegramChat, text));

                case PushChannel.Bark:
                    if (string.IsNullOrWhiteSpace(s.NotifyBarkUrl)) return (false, "尚未填写 Bark 推送地址（含设备 Key）");
                    return await GetAsync(BuildBarkUrl(s.NotifyBarkUrl, title, message));

                case PushChannel.Email:
                    return await SendEmailAsync(title, message);

                case PushChannel.Xxtui:
                    if (string.IsNullOrWhiteSpace(s.NotifyXxtuiUrl)) return (false, "尚未填写 xxtui API 地址或 Key");
                    return await PostJsonAsync(NormalizeXxtuiUrl(s.NotifyXxtuiUrl), BuildXxtuiJson(title, message));

                case PushChannel.Webhook:
                    if (string.IsNullOrWhiteSpace(s.NotifyWebhookUrl)) return (false, "尚未填写自定义 Webhook 地址");
                    return await PostJsonAsync(s.NotifyWebhookUrl, BuildCustomWebhookJson(level, title, message));

                case PushChannel.WebSocket:
                    if (string.IsNullOrWhiteSpace(s.NotifyWebSocketUrl)) return (false, "尚未填写 WebSocket 地址");
                    return await SendWebSocketAsync(s.NotifyWebSocketUrl, BuildCustomWebhookJson(level, title, message));

                default:
                    return (false, "该渠道不支持远程发送");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ===== 各渠道报文（public 便于自动化验证）=====

    public static string BuildWecomJson(string title, string body) => JsonSerializer.Serialize(new
    {
        msgtype = "text",
        text = new { content = Truncate($"{title}\n{body}", 1800) },
    });

    public static string BuildFeishuJson(string title, string body, string? secret, long? timestampSeconds = null)
    {
        long ts = timestampSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new Dictionary<string, object>
        {
            ["msg_type"] = "text",
            ["content"] = new { text = $"{title}\n{body}" },
            ["timestamp"] = ts.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(secret))
            payload["sign"] = FeishuSign(ts, secret!);
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>飞书加签：HMAC-SHA256(key = timestamp + "\n" + secret, data = 空) → Base64</summary>
    public static string FeishuSign(long timestampSeconds, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(timestampSeconds + "\n" + secret));
        return Convert.ToBase64String(hmac.ComputeHash(Array.Empty<byte>()));
    }

    public static string BuildDingJson(string title, string body) => JsonSerializer.Serialize(new
    {
        msgtype = "text",
        text = new { content = $"{title}\n{body}" },
    });

    /// <summary>钉钉加签：timestamp(ms) + "\n" + secret 用 secret 做 HMAC-SHA256 → Base64 → UrlEncode</summary>
    public static string BuildDingUrl(string url, string? secret, long? timestampMs = null)
    {
        if (string.IsNullOrWhiteSpace(secret)) return url;
        long ts = timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        string sign = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(ts + "\n" + secret)));
        return url + (url.Contains('?') ? "&" : "?") + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
    }

    public static string BuildTelegramJson(string chatId, string text) => JsonSerializer.Serialize(new
    {
        chat_id = chatId,
        text,
        disable_web_page_preview = true,
    });

    public static string BuildBarkUrl(string baseUrl, string title, string body)
    {
        string url = baseUrl.Trim().TrimEnd('/');
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://api.day.app/" + url.TrimStart('/');
        return $"{url}/{Uri.EscapeDataString(title)}/{Uri.EscapeDataString(body)}";
    }

    public static string NormalizeXxtuiUrl(string raw)
    {
        string v = raw.Trim();
        if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return v;
        return "https://www.xxtui.com/xxtui/" + v.TrimStart('/');
    }

    public static string BuildXxtuiJson(string title, string body) => JsonSerializer.Serialize(new
    {
        from = "一条龙管家",
        title,
        content = body,
    });

    public static string BuildCustomWebhookJson(NotifyLevel level, string title, string body) => JsonSerializer.Serialize(new
    {
        source = "Dragon Butler",
        level = level.ToString(),
        title,
        message = body,
        timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
    });

    // ===== HTTP =====

    private static async Task<(bool ok, string err)> PostJsonAsync(string url, string json)
    {
        try
        {
            using var resp = await Client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode} {Snippet(body)}");
            // 企业微信/飞书/钉钉在 HTTP 200 里也可能返回错误码
            string? apiErr = ApiError(body);
            return apiErr == null ? (true, "") : (false, apiErr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>WebSocket 推送：连接 → 发一条 JSON → 关闭（与 BGI 的短连接行为一致）</summary>
    private static async Task<(bool ok, string err)> SendWebSocketAsync(string url, string json)
    {
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(url), cts.Token);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token); } catch { }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(bool ok, string err)> GetAsync(string url)
    {
        try
        {
            using var resp = await Client.GetAsync(url);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode} {Snippet(body)}");
            string? apiErr = ApiError(body);
            return apiErr == null ? (true, "") : (false, apiErr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>识别常见推送服务的业务错误码（errcode/code/status）</summary>
    private static string? ApiError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("errcode", out var ec) && ec.TryGetInt32(out int code) && code != 0)
                return $"接口返回 errcode={code} {Str(root, "errmsg")}".Trim();
            // Bark / xxtui 等用 code=200 表示成功，不能一律当成错误
            if (root.TryGetProperty("code", out var c2) && c2.TryGetInt32(out int code2) && code2 != 0 && code2 != 200)
                return $"接口返回 code={code2} {Str(root, "msg")}{Str(root, "message")}".Trim();
            if (root.TryGetProperty("StatusCode", out var sc) && sc.TryGetInt32(out int code3) && code3 != 0)
                return $"接口返回 StatusCode={code3} {Str(root, "StatusMessage")}".Trim();
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                return "接口返回 ok=false " + Str(root, "description");
        }
        catch { }
        return null;
    }

    private static string Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Snippet(string s)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= 160 ? s : s[..160] + "…";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ===== 邮件 =====

    private static async Task<(bool ok, string err)> SendEmailAsync(string title, string body)
    {
        try
        {
            var s = AppState.Settings;
            if (string.IsNullOrWhiteSpace(s.NotifyEmailHost)) return (false, "尚未填写 SMTP 服务器");
            if (string.IsNullOrWhiteSpace(s.NotifyEmailTo)) return (false, "尚未填写收件人邮箱");
            if (string.IsNullOrWhiteSpace(s.NotifyEmailUser)) return (false, "尚未填写 SMTP 用户名");

            string from = string.IsNullOrWhiteSpace(s.NotifyEmailFrom) ? s.NotifyEmailUser : s.NotifyEmailFrom;
            using var msg = new MailMessage
            {
                From = new MailAddress(from),
                Subject = "[一条龙管家] " + title,
                Body = body + "\n\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8,
            };
            foreach (var to in s.NotifyEmailTo.Split(new[] { ';', ',', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                msg.To.Add(to.Trim());

            int port = s.NotifyEmailPort is > 0 and < 65536 ? s.NotifyEmailPort : 465;
            using var smtp = new SmtpClient(s.NotifyEmailHost.Trim(), port)
            {
                EnableSsl = port != 25,
                Credentials = new NetworkCredential(s.NotifyEmailUser.Trim(), s.NotifyEmailPass),
                Timeout = 15000,
            };
            await smtp.SendMailAsync(msg);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
