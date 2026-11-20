using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DragonButler;

/// <summary>
/// 米游社扫码登录（v1.31）。
///
/// 这一步是这个版本最值得做的一件事：以前用户要「按 F12、找 Network、从一堆请求头里
/// 挑出 Cookie 整行、再单独找 stoken」——那不是给普通玩家用的流程。现在手机上点两下就行。
///
/// 几个必须守住的细节（都实测踩过）：
/// 1. createQRLogin 必须带米游社 App 自己的 app_id <c>ddxf5qt290cg</c>。带上坛的
///    <c>bll8iq97uem</c> 或者 1/2/8 一律返回 retcode -3005「参数不合法」，
///    看不出是参数错还是接口没了。
/// 2. 轮询必须复用**创建时的同一个 device_id**。换一个就会被判定「换了设备」，
///    返回 -3503「当前设备或网络环境存在风险」。
/// 3. 扫码页在桌面浏览器里不渲染真二维码（它让用户扫手机上的码），所以二维码得我们自己画，
///    见 <see cref="QrCode"/>。
/// </summary>
public static class MiyousheQr
{
    public const string AppId = "ddxf5qt290cg";
    private const string Passport = "https://passport-api.miyoushe.com";
    private const string AppVersion = "2.90.1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>一次扫码会话。DeviceId 要一直带着，否则轮询会被风控挡。</summary>
    public sealed class Session
    {
        public string Ticket { get; init; } = "";
        public string Url { get; init; } = "";
        public string DeviceId { get; init; } = "";
        public DateTime CreatedAt { get; init; } = DateTime.Now;
        /// <summary>二维码有效期：官方给 3 分钟，本地再留 10 秒余量提醒用户。</summary>
        public bool Expired => (DateTime.Now - CreatedAt).TotalSeconds > 170;
    }

    public enum QrState
    {
        Unknown,
        Created,        // 还没被扫
        Scanned,        // 扫了，手机上还没点确认
        Confirmed,      // 点了确认，tokens 有了
        Expired,
        Canceled,
        Failed,
    }

    /// <summary>扫完拿到的凭证。</summary>
    public sealed class Credential
    {
        public string Stoken { get; init; } = "";
        public string Stuid { get; init; } = "";
        public string Mid { get; init; } = "";
        public string Cookie { get; init; } = "";
        public string Nickname { get; init; } = "";
    }

    public static async Task<(Session? Session, string Reason)> CreateAsync(CancellationToken token)
    {
        string deviceId = Guid.NewGuid().ToString();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                Passport + "/account/ma-cn-passport/app/createQRLogin");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            Headers(req, deviceId);

            using var res = await Http.SendAsync(req, token);
            string text = await res.Content.ReadAsStringAsync(token);
            if (!res.IsSuccessStatusCode)
                return (null, $"扫码请求没发出去（HTTP {(int)res.StatusCode}），过一会儿再试。");

            var (code, msg, data) = MiyousheSign.Split(text);
            if (code != 0 || data.ValueKind != JsonValueKind.Object)
                return (null, $"米游社没给二维码（retcode {code}：{msg}）。过一会儿再试，或者先用浏览器登录。");

            string ticket = data.TryGetProperty("ticket", out var t) ? t.GetString() ?? "" : "";
            string url = data.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (ticket.Length == 0 || url.Length == 0)
                return (null, "米游社给了空二维码。过一会儿再试。");

            return (new Session { Ticket = ticket, Url = url, DeviceId = deviceId }, "");
        }
        catch (Exception ex)
        {
            return (null, "连不上米游社：" + ex.Message);
        }
    }

    /// <summary>问一次「扫了没」。Confirmed 时顺手把 tokens 解析成凭证。</summary>
    public static async Task<(QrState State, Credential? Credential, string Reason)> PollAsync(
        Session session, CancellationToken token)
    {
        if (session.Expired) return (QrState.Expired, null, "二维码过期了，重新点一次「扫码登录」。");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                Passport + "/account/ma-cn-passport/app/queryQRLoginStatus");
            req.Content = new StringContent(
                "{\"ticket\":\"" + session.Ticket + "\"}", Encoding.UTF8, "application/json");
            Headers(req, session.DeviceId);

            using var res = await Http.SendAsync(req, token);
            string text = await res.Content.ReadAsStringAsync(token);
            var (code, msg, data) = MiyousheSign.Split(text);

            if (code == -3503)
                return (QrState.Failed, null, "米游社觉得这台设备有点可疑（-3503）。过一会儿重开一次扫码。");
            if (code != 0 || data.ValueKind != JsonValueKind.Object)
                return (QrState.Unknown, null, $"查询扫码状态失败（{code}）：{msg}");

            string status = data.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            switch (status)
            {
                case "Created":
                    return (QrState.Created, null, "");
                case "Scanned":
                    return (QrState.Scanned, null, "扫到了，在手机上点一下确认。");
                case "Confirmed":
                {
                    var cred = ParseConfirmed(data, out string why);
                    return cred != null
                        ? (QrState.Confirmed, cred, "")
                        : (QrState.Failed, null, why);
                }
                case "Expired":
                    return (QrState.Expired, null, "二维码过期了，重新点一次「扫码登录」。");
                case "Canceled":
                    return (QrState.Canceled, null, "手机上取消了。要登就再点一次。");
                default:
                    return (QrState.Unknown, null, $"没法识别的状态：{status}");
            }
        }
        catch (Exception ex)
        {
            return (QrState.Unknown, null, "查询失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 从确认回包里把凭证拆出来。tokens 是 [{name, token}] 形状，
    /// 我们要的是 stoken / stoken_v2 / cookie_token 这几个。
    /// 官方字段名有过变动，所以按「名字里含 stoken」这种宽口径找，而不是死记一个名字。
    /// </summary>
    private static Credential? ParseConfirmed(JsonElement data, out string why)
    {
        why = "";
        string stoken = "";
        string cookieToken = "";
        string stuid = "";
        string mid = "";
        string nickname = "";

        if (data.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tokens.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string val = item.TryGetProperty("token", out var v) ? v.GetString() ?? "" : "";
                if (val.Length == 0) continue;

                if (name.Contains("cookie_token", StringComparison.OrdinalIgnoreCase))
                    cookieToken = val;
                else if (name.Contains("stoken", StringComparison.OrdinalIgnoreCase))
                {
                    // stoken 与 stoken_v2 同时给时，v2 更长效，优先它
                    if (stoken.Length == 0 || name.Contains("v2", StringComparison.OrdinalIgnoreCase))
                        stoken = val;
                }
            }
        }

        if (data.TryGetProperty("user_info", out var ui) && ui.ValueKind == JsonValueKind.Object)
        {
            nickname = ui.TryGetProperty("nickname", out var nn) ? nn.GetString() ?? "" : "";
            foreach (string key in new[] { "uid", "aid", "mid" })
                if (ui.TryGetProperty(key, out var k) && k.ValueKind == JsonValueKind.String)
                {
                    string val = k.GetString() ?? "";
                    if (val.Length == 0) continue;
                    if (key == "mid") mid = val;
                    else if (stuid.Length == 0) stuid = val;
                }
        }

        if (stoken.Length == 0)
        {
            why = "扫是扫上了，但米游社没给续期凭证（stoken）。这种情况下 cookie 只能用一次，"
                + "下次过期要重新扫。可以先用着。";
        }

        if (stuid.Length == 0 && stoken.Length > 0)
        {
            // stoken 是 v2_ 开头时，stuid 就是它前面那一截的归属；这里留空，
            // 交给 MiyousheService 用已有的那个 uid 或者再向接口要。
        }

        var cred = new Credential
        {
            Stoken = stoken,
            Stuid = stuid,
            Mid = mid,
            Cookie = cookieToken.Length > 0
                ? $"cookie_token={cookieToken}" + (stuid.Length > 0 ? $"; ltuid={stuid}" : "")
                : "",
            Nickname = nickname,
        };
        return cred;
    }

    private static void Headers(HttpRequestMessage req, string deviceId)
    {
        req.Headers.TryAddWithoutValidation("x-rpc-app_id", AppId);
        req.Headers.TryAddWithoutValidation("x-rpc-client_type", "2");
        req.Headers.TryAddWithoutValidation("x-rpc-device_id", deviceId);
        req.Headers.TryAddWithoutValidation("x-rpc-app_version", AppVersion);
        req.Headers.TryAddWithoutValidation("x-rpc-sys_version", "12");
        req.Headers.TryAddWithoutValidation("x-rpc-channel", "miyousheluodi");
        req.Headers.TryAddWithoutValidation("x-rpc-device_name", "Xiaomi");
        req.Headers.TryAddWithoutValidation("x-rpc-device_model", "MI 8");
        req.Headers.TryAddWithoutValidation("x-rpc-platform", "android");
        req.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.9.3");
    }
}
