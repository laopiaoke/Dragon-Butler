using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DragonButler;

/// <summary>
/// 米游社凭证（v1.31）。
///
/// 这套东西的麻烦全在「拿」上，不在「用」上：
///   1. 米游社的登录态是四件套 —— cookie（里面要有 cookie_token）、stoken、stuid、mid。
///   2. cookie 会过期，续期要 stoken；stoken 以 v2_ 开头时还必须带 mid，否则接口直接拒。
///   3. 旧路（拿 login_ticket 换 token）走不通，因为 login_ticket 只有半小时。
///
/// 所以这里的职责是：**从用户浏览器里捞出来的那一堆 cookie 中，认出哪几块是这四件套**，
/// 并且提供一个「用 stoken 把 cookie_token 换回来」的续期。认模式与 MihoyoBBSTools
/// 的 login.py 保持一致（ltuid_v2 / account_id_v2 这些新名字都要认，只认老名字会漏一半人）。
/// </summary>
public static class MiyousheSession
{
    // ===== 认法 =====

    private static readonly Regex ReLoginTicket = new(@"login_ticket=([^;]+)", RegexOptions.Compiled);
    private static readonly Regex ReCookieToken = new(@"cookie_token=([^;]+)", RegexOptions.Compiled);
    private static readonly Regex ReStoken = new(@"(?:^|;)\s*stoken=([^;]+)", RegexOptions.Compiled);
    private static readonly Regex ReStuid = new(@"(?:^|;)\s*stuid=([^;]+)", RegexOptions.Compiled);
    private static readonly Regex ReMid = new(@"(?:^|;)\s*(?:account_mid_v2|ltmid_v2|mid)=([^;]+)", RegexOptions.Compiled);

    /// <summary>uid 的候选名，按「新的优先」排——新版登录只会写 v2 那几个。</summary>
    private static readonly string[] UidKeys =
        { "account_id_v2", "ltuid_v2", "account_id", "ltuid", "login_uid" };

    /// <summary>把浏览器 cookie 字符串里的 uid 认出来。认不到返回 null。</summary>
    public static string? UidOf(Dictionary<string, string> cookies)
    {
        foreach (var key in UidKeys)
            if (cookies.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    public static string? MidOf(Dictionary<string, string> cookies)
    {
        foreach (var key in new[] { "account_mid_v2", "ltmid_v2", "mid" })
            if (cookies.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    /// <summary>stoken 以 v2_ 开头时必须带 mid。这个判断在几处都要用。</summary>
    public static bool NeedsMid(string? stoken) =>
        !string.IsNullOrWhiteSpace(stoken) && stoken.StartsWith("v2_", StringComparison.Ordinal);

    /// <summary>
    /// 从浏览器导出的整串 cookie 里组装四件套。缺 stoken 不算失败——cookie 还没过期时
    /// 照样能签到，只是过期后得再登一次。返回的 Reason 只在「连 cookie_token 都没有」时非空。
    /// </summary>
    public static (string Cookie, string Stoken, string Stuid, string Mid, string Reason) FromCookieString(
        string? raw, string? knownStuid = null, string? knownMid = null)
    {
        string s = raw ?? "";
        if (s.Length == 0) return ("", "", "", "", "没有读到 cookie。");

        var cookies = ParseCookieHeader(s);
        string cookieToken = cookies.TryGetValue("cookie_token", out var ct) ? ct : "";
        if (cookieToken.Length == 0)
            return ("", "", "", "",
                "这串 cookie 里没有 cookie_token，多半是只复制了一部分。"
                + "请在登录后的页面里全选复制，或者用上面的「用浏览器登录」让它自己读。");

        string uid = UidOf(cookies) ?? knownStuid ?? "";
        string stoken = cookies.TryGetValue("stoken", out var st) ? st : "";
        string mid = MidOf(cookies) ?? knownMid ?? "";

        // 只留下用得上的那几块：整串带过去容易撞长度上限，也容易把无关站点的 cookie 泄露给 BBS 接口。
        var keep = new List<string>();
        foreach (var key in new[] { "cookie_token", "account_id", "ltuid", "account_id_v2", "ltuid_v2", "ltoken", "ltmid", "ltmid_v2", "login_ticket" })
            if (cookies.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                keep.Add(key + "=" + v);

        return (string.Join("; ", keep), stoken, uid, mid, "");
    }

    /// <summary>把 "a=1; b=2" 拆成字典。分号后有空格没空格都见过，两边都吃。</summary>
    public static Dictionary<string, string> ParseCookieHeader(string? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return map;
        foreach (var part in raw.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string name = part.Substring(0, eq).Trim();
            string value = part.Substring(eq + 1).Trim();
            if (name.Length == 0 || value.Length == 0) continue;
            if (!map.ContainsKey(name)) map[name] = value;
        }
        return map;
    }

    /// <summary>拼请求头用的 cookie：cookie_token 那几块 + stuid/stoken/mid（换 token 时也要用）。</summary>
    public static string HeaderCookie(string cookie, string stuid, string stoken, string mid)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(cookie)) parts.Add(cookie);
        if (!string.IsNullOrWhiteSpace(stuid)) parts.Add("stuid=" + stuid);
        if (!string.IsNullOrWhiteSpace(stoken)) parts.Add("stoken=" + stoken);
        if (!string.IsNullOrWhiteSpace(mid)) parts.Add("mid=" + mid);
        return string.Join("; ", parts);
    }
}

/// <summary>
/// 米游社签到（v1.31）。
///
/// 只做两件事，都是官方 App 自己在做的事：
///   1. 社区签到（米游币）—— POST {bbs}/apihub/app/api/signIn，body {"gids":[...]}。
///   2. 查角色 —— GET  {web}/binding/api/getUserGameRolesByCookie，用来看这个账号绑了哪些游戏。
/// 游戏内签到（原神那套 luna/sign）需要 act_id，而 act_id 会随活动换，
/// 写死一个过几个月就失效——所以先不做，宁可少一个功能也不要给一个「点了没反应」的按钮。
///
/// 签名（ds）用的是 MihoyoBBSTools 里那两个 salt。这不是破解：它是官方客户端自己算的东西，
/// 服务端靠它区分「来自 App 的请求」和「来自网页的请求」。算法本身在客户端里是明文的。
/// </summary>
public static class MiyousheSign
{
    // 官方客户端里的 salt，公开在 MihoyoBBSTools/setting.py 里。
    // public 不是偷懒：探针要断言「签名用的是哪个盐」。把它藏起来，那条断言就只能
    // 去比对源码文本，而源码里的盐迟早会被误改。
    //
    // 两个名字按「哪个接口用它」取，不按「它属于谁」取——上层调用 count 得清哪个是哪个。
    public const string SaltApp = "LKsbRciURVKljyjuhSeobiyFngbAyjAh";   // 账号类（web=true）
    public const string SaltBbs = "t0qEgfub6cvueAPgR5m9aQWWVciEer7v";   // 社区类（web=false）
    private const string AppVersion = "2.90.1";
    private const string ClientType = "2";

    private const string BbsApi = "https://bbs-api.miyoushe.com";
    private const string WebApi = "https://api-takumi.mihoyo.com";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,          // cookie 我们自己拼，别让容器再插一手
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// 算 ds。web=false 走社区那套（不带 b/q），web=true 走网页那套。
    /// r 的取法两边不一样：社区版要 6 位随机，网页版要 10 万到 20 万的数字。
    /// </summary>
    public static string Ds(bool web, string? query = null, string? body = null)
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string salt = web ? SaltApp : SaltBbs;
        string r = web
            ? RandomNumberGenerator.GetInt32(100001, 200000).ToString(CultureInfo.InvariantCulture)
            : RandomString(6);
        string raw = web && body != null
            ? $"salt={salt}&t={t}&r={r}&b={body}&q={query ?? ""}"
            : $"salt={salt}&t={t}&r={r}";
        return $"{t},{r},{Md5(raw)}";
    }

    private static string RandomString(int len)
    {
        const string pool = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append(pool[RandomNumberGenerator.GetInt32(pool.Length)]);
        return sb.ToString();
    }

    private static string Md5(string s)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(32);
        foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>四个接口都要的这一套头。缺一个都会被判成「不是官方客户端」。</summary>
    private static HttpRequestMessage Request(string url, string cookie, string? jsonBody)
    {
        var req = new HttpRequestMessage(jsonBody == null ? HttpMethod.Get : HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("DS", Ds(web: false));
        req.Headers.TryAddWithoutValidation("x-rpc-app_version", AppVersion);
        req.Headers.TryAddWithoutValidation("x-rpc-client_type", ClientType);
        req.Headers.TryAddWithoutValidation("x-rpc-device_id", DeviceIdOf(cookie));
        req.Headers.TryAddWithoutValidation("x-rpc-device_name", "Xiaomi MI 8");
        req.Headers.TryAddWithoutValidation("x-rpc-device_model", "MI 8");
        req.Headers.TryAddWithoutValidation("x-rpc-sys_version", "12");
        req.Headers.TryAddWithoutValidation("x-rpc-channel", "miyousheluodi");
        req.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.9.3");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        if (jsonBody != null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>device_id 要是稳定的 GUID 形式，否则米游社把每次请求都当新设备。</summary>
    private static string DeviceIdOf(string cookie)
    {
        string seed = cookie.Length > 0 ? cookie : "dragon-butler";
        byte[] h = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        var hex = new StringBuilder(32);
        foreach (byte b in h) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        string x = hex.ToString();
        return $"{x.Substring(0, 8)}-{x.Substring(8, 4)}-{x.Substring(12, 4)}-{x.Substring(16, 4)}-{x.Substring(20, 12)}";
    }

    /// <summary>接口回包里取 retcode / message，顺便把 data 摘出来。</summary>
    public static (int Code, string Message, JsonElement Data) Split(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int code = root.TryGetProperty("retcode", out var c) && c.TryGetInt32(out var ci) ? ci : -1;
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            return (code, msg, data);
        }
        catch (Exception ex)
        {
            return (-1, "回包读不懂：" + ex.Message, default);
        }
    }

    /// <summary>把 retcode 翻成人话。米游社的码不统一，这里只覆盖签到真会撞上的几个。</summary>
    public static string Explain(int code) => code switch
    {
        0 => "",
        -100 => "登录态过期了。重新登录一次就行。",
        10001 => "登录态过期了。重新登录一次就行。",
        -10002 or 10002 => "登录态过期了。重新登录一次就行。",
        1034 or -3503 => "触发了米游社的风控（多半是请求太快或换了网络）。过一会儿再试。",
        5003 => "这个账号还没在米游社绑定角色，先去米游社 App 里绑一下。",
        // 没见过的码不硬猜。留空会让上层弹出的提示变成「失败：」，比这更糟——
        // 至少把码原样递出去，用户搜得到、我们也能从反馈里补规则。
        _ => $"米游社返回了一个没见过的错误码（{code}）。查一下这个码，或者过一会儿再试。",
    };

    /// <summary>当前登录的是谁。失败返回 null（不影响签到，只是界面上少个名字）。</summary>
    public static async Task<string?> NicknameAsync(string cookie, CancellationToken token)
    {
        try
        {
            using var req = Request($"{BbsApi}/apihub/api/getUserInfo", cookie, null);
            using var res = await Http.SendAsync(req, token);
            var (code, _, data) = Split(await res.Content.ReadAsStringAsync(token));
            if (code != 0) return null;
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("user_info", out var ui)
                && ui.TryGetProperty("nickname", out var nn))
                return nn.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>账号绑了哪些游戏的角色。用来判断「这个号有没有原神」。失败返回空表。</summary>
    public static async Task<List<(string Game, string Name, string Uid)>> RolesAsync(string cookie, CancellationToken token)
    {
        var list = new List<(string, string, string)>();
        try
        {
            using var req = Request($"{WebApi}/binding/api/getUserGameRolesByCookie", cookie, null);
            using var res = await Http.SendAsync(req, token);
            var (code, _, data) = Split(await res.Content.ReadAsStringAsync(token));
            if (code != 0) return list;
            if (!data.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in arr.EnumerateArray())
            {
                string game = el.TryGetProperty("game_name", out var g) ? g.GetString() ?? "" : "";
                string name = el.TryGetProperty("nickname", out var n) ? n.GetString() ?? "" : "";
                string uid = el.TryGetProperty("game_uid", out var u) ? u.GetString() ?? "" : "";
                if (game.Length > 0) list.Add((game, name, uid));
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 社区签到。gids 是版块号（1 崩3 / 2 原神 / 3 崩2 / 4 未定 / 5 大别野 / 6 星铁 / 8 绝区零）。
    /// 一次全签比一个个签稳——米游社自己的「一键签到」就是一次带全 gids。
    /// </summary>
    public static async Task<(bool Ok, string Message, int Coin)> SignInAsync(
        string cookie, IEnumerable<int> gids, CancellationToken token)
    {
        string body = "{\"gids\":" + JsonSerializer.Serialize(gids.ToArray()) + "}";
        try
        {
            using var req = Request($"{BbsApi}/apihub/app/api/signIn", cookie, body);
            using var res = await Http.SendAsync(req, token);
            string text = await res.Content.ReadAsStringAsync(token);

            // 网关自己挡下来的时候回的是 HTML 的 Forbidden，不是 JSON。
            // 直接扔给 Split 会得到「回包读不懂：'F' is an invalid start of a value」——
            // 那句话对用户毫无用处，它只会让人以为软件坏了。
            if (!res.IsSuccessStatusCode)
            {
                int http = (int)res.StatusCode;
                return (false, http switch
                {
                    403 => "米游社把这次请求挡回来了（403）。多半是这份凭证已经失效——重新登录一次。",
                    401 => "凭证不被接受（401）。重新登录一次。",
                    >= 500 => $"米游社那边出错了（{http}），过一会儿再试。",
                    _ => $"请求被拒（HTTP {http}）。",
                }, 0);
            }

            var (code, msg, data) = Split(text);
            if (code != 0)
                return (false, Explain(code) is { Length: > 0 } human ? human : $"签到没成（retcode {code}）：{msg}", 0);

            // 回包里每个版块一条，有的成功有的「今天已签」。米游币数量在 points 里。
            int coin = 0;
            var notes = new List<string>();
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("list", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    bool signed = el.TryGetProperty("is_sign", out var s) && s.ValueKind == JsonValueKind.True;
                    if (el.TryGetProperty("points", out var p) && p.TryGetInt32(out var pi)) coin += pi;
                    if (signed && name.Length > 0) notes.Add(name);
                }
            }
            string summary = notes.Count > 0
                ? $"签到了 {notes.Count} 个版块（{string.Join("、", notes.Take(4))}{(notes.Count > 4 ? "…" : "")}）"
                : "签到成功";
            if (coin > 0) summary += $"，米游币 +{coin}";
            return (true, summary, coin);
        }
        catch (Exception ex)
        {
            return (false, "连不上米游社：" + ex.Message, 0);
        }
    }

    /// <summary>
    /// 用 stoken 换一份新的 cookie_token。这是「登录一次就能长期用」的关键：
    /// cookie 过期了，stoken 还在，就不用再打扰用户。
    /// </summary>
    public static async Task<(bool Ok, string CookieToken, string Message)> RefreshCookieTokenAsync(
        string stuid, string stoken, string mid, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(stoken)) return (false, "", "没有 stoken，没法自动续期。");
        if (MiyousheSession.NeedsMid(stoken) && string.IsNullOrWhiteSpace(mid))
            return (false, "", "stoken 是 v2_ 开头的，必须同时有 mid 才能续期——重新登录一次就好。");

        try
        {
            string cookie = MiyousheSession.HeaderCookie("", stuid, stoken, mid);
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{WebApi}/auth/api/getCookieAccountInfoBySToken");
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.9.3");
            using var res = await Http.SendAsync(req, token);
            var (code, msg, data) = Split(await res.Content.ReadAsStringAsync(token));
            if (code != 0) return (false, "", $"续期失败（retcode {code}）：{msg}");
            string ct = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("cookie_token", out var v)
                ? v.GetString() ?? "" : "";
            return ct.Length > 0 ? (true, ct, "") : (false, "", "接口没给 cookie_token。");
        }
        catch (Exception ex)
        {
            return (false, "", "续期时连不上：" + ex.Message);
        }
    }
}
