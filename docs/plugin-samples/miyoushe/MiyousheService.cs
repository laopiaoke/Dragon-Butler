using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DragonButler;

/// <summary>
/// 米游社签到的收口（v1.31）。
///
/// 之所以单独有一层，是因为「签到」这件事有四个步骤很容易只做对一半：
///   1. 检查有没有凭证（没有就别假装在跑）；
///   2. 顺手续期（stoken 在就别等过期了才发现）；
///   3. 真的签（一次带全 gids）；
///   4. 把结果写成一句人话，存进设置也给消息中心留一条。
/// 顺序错了就会出现「说签到成功但其实用的是过期 cookie」这种事。
/// </summary>
public static class MiyousheService
{
    /// <summary>默认签这几个版块：崩3 / 原神 / 崩2 / 未定 / 大别野 / 星铁 / 绝区零。</summary>
    private static readonly int[] DefaultGids = { 1, 2, 3, 4, 5, 6, 8 };

    public static bool HasCredential => !string.IsNullOrWhiteSpace(AppState.Settings.MiyousheCookie);

    /// <summary>
    /// 签一次。返回一句能直接显示的话。
    /// 不发通知：签到是顺手做的事，为它单独弹一条通知属于打扰。
    /// 失败进消息中心——那才是「你不在的时候出过事」该去的地方。
    /// </summary>
    public static async Task<string> SignNowAsync(CancellationToken token = default)
    {
        var s = AppState.Settings;
        if (!HasCredential)
        {
            s.MiyousheLastResult = "还没登录米游社。";
            AppState.SaveSettings();
            return s.MiyousheLastResult;
        }

        // 步骤 2：有 stoken 就先换一次 cookie_token。
        // 换失败不算死——手上这份可能还没过期，接着试。
        if (!string.IsNullOrWhiteSpace(s.MiyousheStoken))
        {
            var (ok, fresh, why) = await MiyousheSign.RefreshCookieTokenAsync(
                s.MiyousheStuid, s.MiyousheStoken, s.MiyousheMid, token);
            if (ok && fresh.Length > 0)
            {
                s.MiyousheCookie = ReplaceCookieToken(s.MiyousheCookie, fresh);
                AppState.SaveSettings();
                LogService.LogEvent("米游社：用 stoken 换到了新的 cookie_token");
            }
            else if (why.Length > 0)
            {
                LogService.LogEvent("米游社：续期没成——" + why);
            }
        }

        string cookie = MiyousheSession.HeaderCookie(
            s.MiyousheCookie, s.MiyousheStuid, s.MiyousheStoken, s.MiyousheMid);

        // 顺手把昵称补上（换账号后清空过，这里是一次性的便宜操作）。
        if (string.IsNullOrWhiteSpace(s.MiyousheNickname))
        {
            string? nick = await MiyousheSign.NicknameAsync(cookie, token);
            if (!string.IsNullOrWhiteSpace(nick))
            {
                s.MiyousheNickname = nick;
                AppState.SaveSettings();
            }
        }

        // 步骤 3：签到。
        var (signOk, message, coin) = await MiyousheSign.SignInAsync(cookie, DefaultGids, token);

        s.MiyousheLastAt = DateTime.Now;
        s.MiyousheLastResult = message;
        AppState.SaveSettings();

        LogService.LogEvent("米游社签到：" + message);

        if (!signOk)
        {
            // 失败进消息中心。带上说的是什么，人回来看得懂。
            NoticeCenter.AddError("米游社签到没成", message,
                $"cookie_token={Passive(s.MiyousheCookie.Length)} stoken={(string.IsNullOrEmpty(s.MiyousheStoken) ? "无" : "有")} coin={coin}");
        }
        return message;
    }

    private static string Passive(int len) => len > 0 ? "有" : "无";

    /// <summary>
    /// 把 cookie 串里的 cookie_token 换成新的。其余字段原样保留——
    /// 只改这一块，别的地方我们并不知道它们是什么，就不动。
    /// </summary>
    public static string ReplaceCookieToken(string cookie, string fresh)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return "cookie_token=" + fresh;
        var parts = new List<string>();
        bool replaced = false;
        foreach (var part in cookie.Split(';'))
        {
            string p = part.Trim();
            if (p.Length == 0) continue;
            if (p.StartsWith("cookie_token=", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("cookie_token=" + fresh);
                replaced = true;
            }
            else parts.Add(p);
        }
        if (!replaced) parts.Add("cookie_token=" + fresh);
        return string.Join("; ", parts);
    }
}
