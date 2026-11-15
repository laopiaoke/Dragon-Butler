using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DragonButler;

/// <summary>
/// v1.16：看有没有新版本。
///
/// <para>
/// 边界先写清楚：**只检查、只提示、给个链接。不下载、不替换、不自动重启。**
/// 这条和 v1.15 方案里 M5 定的一致——替换正在运行的 exe 是这类工具最容易把用户
/// 搞崩的一步，而收益只有「少点两下」。
/// </para>
/// <para>
/// 另一条边界：**不发任何本机信息**。请求就是一次 GET GitHub 的 latest release，
/// 没有 UA 里的机器名、没有安装 ID、没有使用统计。程序其余部分本来就零遥测，
/// 这里不能开第一个口子。
/// </para>
/// </summary>
public static class UpdateCheck
{
    /// <summary>GitHub latest release 的 API。匿名请求有速率限制（每小时 60 次），够用。</summary>
    public const string ApiUrl = "https://api.github.com/repos/laopiaoke/Dragon-Butler/releases/latest";

    /// <summary>给人点的地方。</summary>
    public const string ReleasesUrl = "https://github.com/laopiaoke/Dragon-Butler/releases/latest";

    /// <summary>一次检查最长等多久。启动时顺手查，不能卡住界面。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>两次检查之间至少隔多久（同一个进程内）。</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromHours(6);

    public sealed record Result(
        bool Ok,
        string LatestTag,
        string Message,
        bool HasNewer,
        string Url,
        string Detail = "");

    /// <summary>
    /// 版本号归一：`v1.15`、`1.15.0`、`v1.15.0-beta` 都拆成可比的三段 + 预发布标记。
    /// 拆不出来返回 null——**认不出版本就不提示**，比提示错强。
    /// </summary>
    public static (int Major, int Minor, int Patch, bool Pre)? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string t = tag.Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);

        bool pre = t.Contains('-', StringComparison.Ordinal);
        var m = Regex.Match(t, @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success) return null;

        int major = int.TryParse(m.Groups[1].Value, out var a) ? a : 0;
        int minor = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var b) ? b : 0;
        int patch = m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var c) ? c : 0;
        return (major, minor, patch, pre);
    }

    /// <summary>
    /// 有没有更新的。返回 1 = latest 更新，0 = 一样，-1 = latest 更旧（本地是开发版），null = 比不了。
    ///
    /// <para>
    /// 预发布要特殊对待：`1.16.0-beta` 比 `1.16.0` 旧。用「三段数字相同则正式版更大」来表达，
    /// 不用字符串比较——字符串比较会把 `1.9` 判成比 `1.10` 新。
    /// </para>
    /// </summary>
    public static int? Compare(string? latest, string? current)
    {
        var l = Parse(latest);
        var c = Parse(current);
        if (l == null || c == null) return null;

        var (lv, cv) = (l.Value, c.Value);
        if (lv.Major != cv.Major) return lv.Major > cv.Major ? 1 : -1;
        if (lv.Minor != cv.Minor) return lv.Minor > cv.Minor ? 1 : -1;
        if (lv.Patch != cv.Patch) return lv.Patch > cv.Patch ? 1 : -1;

        // 数字完全一样：正式版 > 预发布 > 都是预发布（不比了，当作一样）
        if (lv.Pre == cv.Pre) return 0;
        return cv.Pre ? 1 : -1;
    }

    /// <summary>把一次检查翻成一句话。**每次都要能落到「所以我现在干什么」。**</summary>
    public static Result Describe(string? latestTag, string? currentVersion, bool requestOk, string? error)
    {
        if (!requestOk)
            return new Result(false, "", "查不到新版本（" + (string.IsNullOrWhiteSpace(error) ? "网络不通" : error) + "）。不影响使用。", false, ReleasesUrl);

        var cmp = Compare(latestTag, currentVersion);
        if (cmp == null)
            return new Result(false, latestTag ?? "", "看出来有新版本，但版本号格式不认得，没敢乱提示。", false, ReleasesUrl);

        if (cmp > 0)
            return new Result(true, latestTag ?? "", $"有新版本 {latestTag}（你现在是 {currentVersion}）。", true, ReleasesUrl);

        if (cmp == 0)
            return new Result(true, latestTag ?? "", $"已经是最新的 {currentVersion}。", false, ReleasesUrl);

        return new Result(true, latestTag ?? "", $"你用的是开发版 {currentVersion}，比发布版 {latestTag} 还新。", false, ReleasesUrl);
    }

    /// <summary>
    /// 真的去查一次。任何异常都收成 Ok=false + 一句人话——检查更新失败不该弹错误框。
    /// </summary>
    public static async Task<Result> CheckAsync(string? currentVersion, HttpClient? client = null)
    {
        bool owned = client == null;
        var http = client ?? new HttpClient { Timeout = Timeout };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "Dragon-Butler-UpdateCheck");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Describe(null, currentVersion, false, "服务返回 " + (int)resp.StatusCode);

            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            string tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            return Describe(tag, currentVersion, true, null);
        }
        catch (TaskCanceledException)
        {
            return Describe(null, currentVersion, false, "等太久没回应");
        }
        catch (Exception ex)
        {
            LogService.LogEvent("检查更新失败：" + ex.Message);
            return Describe(null, currentVersion, false, "连不上 GitHub");
        }
        finally
        {
            if (owned) http.Dispose();
        }
    }
}
