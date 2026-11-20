using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DragonButler;

/// <summary>消息中心里的一条。Kind 决定图标与配色，不是分类学。</summary>
public enum NoticeKind
{
    /// <summary>程序自己崩了 / 上次没收好尾。</summary>
    Crash,
    /// <summary>某件事没做成（工具启动失败、备份失败、通知没送到）。</summary>
    Error,
    /// <summary>值得知道但不用处理（自动重启过、跳过了某一项）。</summary>
    Warn,
    /// <summary>普通消息（跑完了、体检结果）。</summary>
    Info,
}

/// <summary>
/// 一条消息。<see cref="Raw"/> 存原始异常文本，<see cref="Body"/> 存翻成人话的解释——
/// 两者都留着，因为「发给作者」要前者，「看懂发生了什么」要后者。
/// </summary>
public sealed class Notice
{
    public DateTime At { get; set; }
    public NoticeKind Kind { get; set; }
    public string Title { get; set; } = "";
    /// <summary>人话解释（<see cref="ErrorExplain.Explained.Text"/>）。</summary>
    public string Body { get; set; } = "";
    /// <summary>原始堆栈/错误文本。可能为空。</summary>
    public string Raw { get; set; } = "";
    public bool Read { get; set; }

    /// <summary>是不是「管家自己的问题」——决定要不要在界面上说「这不是你配错了」。</summary>
    public bool OurFault => Kind == NoticeKind.Crash && ErrorExplain.IsOurFault(Raw);
}

/// <summary>
/// 消息中心（v1.28）。
///
/// <para>
/// <b>为什么要有这个</b>：以前出错靠模态弹窗。模态的问题是它会<b>堵住启动流程</b>——
/// 「检测到上次为异常退出」那个框是在 <c>CrashGuard.Install()</c> 里弹的，
/// 而它在 <c>OnStartup</c> 里排在自动开跑之前，于是人不在电脑前的时候，
/// 程序卡在一个没人点的对话框上，一条龙整晚都没跑。用户第二天看到的正是
/// 「只弹了个崩溃提示」——不是巧合，是同一个原因。
/// </para>
/// <para>
/// <b>所以这里的规矩</b>：
/// ① 只记录，不弹窗、不阻塞。写入是同步的（要保证崩之前落了盘），但绝不调 ShowDialog。
/// ② 落盘到 <c>config/notices.json</c>，重启后还在——「昨晚出过事」这件事不能因为关一次程序就忘。
/// ③ 有上限（<see cref="Capacity"/> 条环形）。消息中心不是审计日志，翻旧账请看日志目录。
/// ④ 已读/未读只影响标题栏那个红点，不删数据。
/// </para>
/// </summary>
public static class NoticeCenter
{
    /// <summary>最多留多少条。留太多等于没有——扫不完的清单没人看。</summary>
    public const int Capacity = 100;

    private static readonly object Gate = new();
    private static readonly List<Notice> Items = new();
    private static bool _loaded;

    /// <summary>
    /// 换一个落盘位置（只给探针用）。消息中心的断言必须写自己的临时文件——
    /// 拿真实的 config/notices.json 做实验，等于把用户的「昨晚出过什么事」擦掉。
    /// </summary>
    public static string? PathOverride { get; set; }

    private static string FilePath =>
        PathOverride ?? Path.Combine(AppContext.BaseDirectory, "config", "notices.json");

    /// <summary>把内存里的那份丢掉，下次访问重新读盘（换过 <see cref="PathOverride"/> 之后要调）。</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            Items.Clear();
            _loaded = false;
        }
    }

    private static readonly JsonSerializerOptions WriteOpt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions ReadOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>读盘（幂等）。读失败就当空的——消息中心自己不能成为新的故障点。</summary>
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<Notice>>(File.ReadAllText(FilePath, Encoding.UTF8), ReadOpt);
            if (list == null) return;
            Items.AddRange(list.Where(n => n != null).OrderBy(n => n.At).TakeLast(Capacity));
        }
        catch (Exception ex)
        {
            // 文件坏了就重新开始，但要留下痕迹：这条本身不进消息中心，避免递归
            try { LogService.LogEvent("消息中心读取失败：" + ex.Message); } catch { }
        }
    }

    private static void SaveLocked()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, WriteOpt), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>
    /// 记一条。**不弹窗**。返回记进去的那条（方便调用方立刻展示）。
    /// </summary>
    public static Notice Add(NoticeKind kind, string? title, string? body = "", string? raw = "")
    {
        var n = new Notice
        {
            At = DateTime.Now,
            Kind = kind,
            Title = (title ?? "").Trim(),
            Body = (body ?? "").Trim(),
            Raw = (raw ?? "").Trim(),
            Read = false,
        };
        if (n.Title.Length == 0) n.Title = "（没写标题的消息）";

        lock (Gate)
        {
            EnsureLoaded();
            Items.Add(n);
            while (Items.Count > Capacity) Items.RemoveAt(0);
            SaveLocked();
        }

        // 同时写一条运行日志：消息中心是给人看的，日志是给排查用的，两边都要有
        try { LogService.LogEvent($"[{KindName(kind)}] {n.Title}"); } catch { }
        return n;
    }

    /// <summary>
    /// 记一条崩溃：自动把堆栈翻成人话再存。
    /// 这是 <see cref="CrashGuard"/> 与各 catch 块的主入口。
    /// </summary>
    public static Notice AddCrash(string? scene, string? raw, string? title = null)
    {
        string explained = ErrorExplain.Explain(raw).Text;
        string head = title ?? ErrorExplain.Headline(raw);
        // 场景 + 标题本来是为了「哪儿出的：出了什么」，但调用方经常把同一句话
        // 既当场景又当标题，拼出来就是「上次没有正常收尾：上次没有正常收尾，…」。
        // 标题里已经说了场景就不再重复。
        string t = string.IsNullOrWhiteSpace(scene) || head.Contains(scene, StringComparison.Ordinal)
            ? head
            : $"{scene}：{head}";
        var n = Add(NoticeKind.Crash, t, explained, raw);
        App.Instance?.RefreshNoticeBadge();
        return n;
    }

    /// <summary>记一条「某事没做成」：解释推导交给调用方，这里只管存。</summary>
    public static Notice AddError(string? title, string? body = "", string? raw = "")
    {
        var n = Add(NoticeKind.Error, title, body, raw);
        App.Instance?.RefreshNoticeBadge();
        return n;
    }

    /// <summary>全部（最新的在前）。</summary>
    public static List<Notice> All()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return Items.OrderByDescending(n => n.At).ToList();
        }
    }

    /// <summary>最近的 n 条（最新的在前）。</summary>
    public static List<Notice> Recent(int n)
    {
        if (n <= 0) return new List<Notice>();
        lock (Gate)
        {
            EnsureLoaded();
            return Items.OrderByDescending(x => x.At).Take(n).ToList();
        }
    }

    /// <summary>未读数——标题栏红点用它。</summary>
    public static int UnreadCount
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return Items.Count(n => !n.Read);
            }
        }
    }

    /// <summary>一共留了多少条。</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return Items.Count;
            }
        }
    }

    /// <summary>全部标为已读（打开消息中心时调）。</summary>
    public static void MarkAllRead()
    {
        lock (Gate)
        {
            EnsureLoaded();
            foreach (var n in Items) n.Read = true;
            SaveLocked();
        }
        App.Instance?.RefreshNoticeBadge();
    }

    /// <summary>删一条。</summary>
    public static void Remove(Notice? n)
    {
        if (n == null) return;
        lock (Gate)
        {
            EnsureLoaded();
            Items.Remove(n);
            SaveLocked();
        }
        App.Instance?.RefreshNoticeBadge();
    }

    /// <summary>清空。清完红点必然消失。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            EnsureLoaded();
            Items.Clear();
            SaveLocked();
        }
        App.Instance?.RefreshNoticeBadge();
    }

    /// <summary>把一条消息整理成可粘贴的文字（发给作者时用）。</summary>
    public static string ToClipboard(Notice? n)
    {
        if (n == null) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"[{KindName(n.Kind)}] {n.At:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(n.Title);
        if (n.Body.Length > 0) sb.AppendLine(n.Body);
        if (n.Raw.Length > 0)
        {
            sb.AppendLine("---- 原始内容 ----");
            sb.AppendLine(n.Raw);
        }
        return sb.ToString();
    }

    public static string KindName(NoticeKind kind) => kind switch
    {
        NoticeKind.Crash => "崩溃",
        NoticeKind.Error => "出错",
        NoticeKind.Warn => "提醒",
        NoticeKind.Info => "消息",
        _ => "消息",
    };

    /// <summary>
    /// 给标题栏红点与消息中心标题的一句话摘要：「3 条没看」。
    /// 没消息返回空串——界面上不出现「暂无消息」这种占位。
    /// </summary>
    public static string BadgeText()
    {
        int unread = UnreadCount;
        return unread <= 0 ? "" : unread > 99 ? "99+" : unread.ToString();
    }
}
