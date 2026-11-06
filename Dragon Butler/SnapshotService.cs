using System;
using System.IO;
using System.Text;

namespace DragonButler;

/// <summary>
/// 「现在就到」（v1.8 / F12）。
///
/// 治的病：跑一条龙的时候人不在电脑前，想知道"现在到底什么情况"，
/// 只能等下次完成通知——而那时已经晚了。
/// 这里做的是：抓一张当前的游戏画面，存到本地，把路径塞进推送文本里。
///
/// 为什么不直接把图发出去：11 个推送渠道里只有自定义 Webhook 与 WebSocket
/// 能带附件，企业微信/飞书/钉钉/Telegram/Bark/邮件/xxtui 都是纯文本。
/// 与其做一半渠道能看图、一半不能，不如统一成「存本地 + 推文本带路径」，
/// 谁需要图，本机或同步盘上自己取——这是一致的体验，而不是分渠道的运气。
/// </summary>
public static class SnapshotService
{
    /// <summary>落盘目录：与日志、现场包同级，方便用户一个地方找全。</summary>
    public static string Dir => Path.Combine(LogService.Dir, "snapshots");

    /// <summary>保留份数，超出丢弃最旧的（抓屏就是为了「看一眼现在」，不需要囤积）。</summary>
    public const int Keep = 20;

    /// <summary>
    /// 抓一张当前画面并落盘。返回完整路径；抓不到返回 null。
    /// 抓屏复用 <see cref="ScreenshotMonitor.Capture"/>（同一套 GDI 代码，
    /// 优先游戏窗口、找不到则全屏），不再写第二份——重复的抓屏代码是「改一处忘一处」的经典来源。
    /// </summary>
    public static string? Capture(string why = "手动")
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var (buf, w, h) = ScreenshotMonitor.Capture();
            if (buf == null || w <= 0 || h <= 0) return null;

            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            string path = Path.Combine(Dir, name);

            // BitmapSource / PngBitmapEncoder 走 WPF，必须在 UI 线程；
            // 这里用 Invoke 同步等结果，因为调用方要拿到路径塞进通知文本。
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null) return null;
            if (d.CheckAccess())
                ScreenshotMonitor.SavePngTo(buf, w, h, path);
            else
                d.Invoke(new Action(() => ScreenshotMonitor.SavePngTo(buf, w, h, path)));

            Trim();
            LogService.LogEvent($"快照：已保存（{why}）→ {name}");
            return path;
        }
        catch (Exception ex)
        {
            LogService.LogEvent("快照：抓取失败 " + ex.Message);
            return null;
        }
    }

    /// <summary>抓一张并把结果写成一句可以推进通知里的话。</summary>
    public static string CaptureMessage(string why = "手动")
    {
        string? path = Capture(why);
        return path == null
            ? "现在就到：抓屏失败（可能没有可见窗口或权限不足）。"
            : "现在就到：已抓取当前画面 → " + path;
    }

    /// <summary>只保留最近 <see cref="Keep"/> 张。</summary>
    private static void Trim()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.png");
            if (files.Length <= Keep) return;

            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            for (int i = 0; i < files.Length - Keep; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }
}
