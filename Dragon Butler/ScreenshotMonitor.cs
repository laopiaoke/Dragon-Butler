using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DragonButler;

/// <summary>
/// 截图监控（SPBGI）：定时抓游戏窗口（找不到则全屏）→ 缩放到 64×64 灰度 → 逐像素差值归一化 0~100。
/// 判定：差异分 &lt; 2 且连续 2 次 → ScreenFrozen 事件（只通知，绝不自动重启）。
/// 截图存 全部日志\screenshots\（保留最近 10 张，滚动删除），文件名 HHmmss.png。
/// 比对在后台线程算，PNG 编码经主线程调度（WPF 对象线程亲和）。
/// </summary>
public sealed class ScreenshotMonitor
{
    public event Action<SentinelEvent>? OnEvent;

    /// <summary>最近一次真正执行截图比对的时间（供界面显示"X 秒前检查"）</summary>
    public DateTime LastCheckAt { get; private set; }

    /// <summary>本次运行累计判定冻结次数</summary>
    public int FrozenCount { get; private set; }

    /// <summary>最近一次判定冻结的时间（供界面判断"近期异常"，而不是永久标黄）</summary>
    public DateTime LastFrozenAt { get; private set; }

    /// <summary>最近一次画面差异分（0~100）</summary>
    public double LastScore { get; private set; }

    private DateTime _lastShot = DateTime.MinValue;
    private byte[]? _prev64;
    private int _frozenStreak;
    private double _score1;
    private double _score2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int nWidth, int nHeight);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hDestDc, int xDest, int yDest, int nWidth, int nHeight, IntPtr hSrcDc, int xSrc, int ySrc, int dwRop);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hDc, IntPtr hBmp, uint uStartScan, uint cScanLines, byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    private const int SRCCOPY = 0x00CC0020;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_CMONITORS = 80;
    private const int Dim = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public short biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    /// <summary>
    /// 心跳（由 SentinelService 5 秒驱动）：到 2 分钟且一条龙进行中才截图比对
    /// </summary>
    public void Heartbeat(bool dragonRunning)
    {
        try
        {
            if (!AppState.Settings.ScreenshotCheckEnabled) return;
            if (!dragonRunning) return;
            if ((DateTime.Now - _lastShot).TotalMinutes < 2) return;
            _lastShot = DateTime.Now;
            LastCheckAt = DateTime.Now;

            var (buf, w, h) = Capture();
            if (buf == null) return;

            var cur64 = Downsample(buf, w, h);
            if (_prev64 != null && _prev64.Length == cur64.Length)
            {
                double score = Diff(_prev64, cur64);
                LastScore = score;
                _score2 = _score1;
                _score1 = score;
                if (score < 2)
                {
                    _frozenStreak++;
                    if (_frozenStreak >= 2)
                    {
                        _frozenStreak = 0;
                        FrozenCount++;
                        LastFrozenAt = DateTime.Now;
                        Emit(SentinelEventKind.ScreenFrozen, $"差异分 {_score2:F1} / {_score1:F1}");
                    }
                }
                else
                {
                    _frozenStreak = 0;
                }
            }
            _prev64 = cur64;
            SavePng(buf, w, h);
        }
        catch (Exception ex)
        {
            LogService.LogEvent("ScreenshotMonitor 心跳失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 抓屏：优先游戏窗口区域（本会话），找不到则全屏。
    /// v1.6 / F3：从 private 改为 internal —— 现场包要复用同一套抓屏逻辑，
    /// 不再写第二份 GDI 代码（重复的抓屏代码是那种「改一处忘一处」的经典来源）。
    /// </summary>
    internal static (byte[]? buf, int w, int h) Capture()
    {
        var c = CaptureEx();
        return (c.Buf, c.W, c.H);
    }

    /// <summary>
    /// 一次抓屏的结果（v1.43 / 任务六 P2-4）。
    ///
    /// <para>
    /// 原来只回 (buf, w, h)，于是「抓到了」和「抓到的是主屏而不是游戏窗口」在调用方看来一模一样。
    /// 现场包里放一张主屏截图、却不说明只抓到主屏，等于给了个误导性的证据。
    /// </para>
    /// </summary>
    /// <param name="Scope">人话的截图范围，直接写进现场包。</param>
    /// <param name="GameWindow">true = 抓到的是游戏窗口区域；false = 主屏回退。</param>
    internal sealed record ScreenCapture(byte[]? Buf, int W, int H, string Scope, bool GameWindow, string? Error);

    /// <summary>
    /// 抓屏并如实报告结果（v1.43）。检查每一次 GDI 调用的返回值——
    /// BitBlt 失败时缓冲区是空的，直接编码就是一张全黑图，看着像「截到了」。
    /// </summary>
    internal static ScreenCapture CaptureEx()
    {
        IntPtr hwnd = IntPtr.Zero;
        foreach (var p in ProfileRegistry.BetterGi.GameProcessNames
                     .SelectMany(ProcessTools.GetSessionProcesses))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero) { hwnd = p.MainWindowHandle; break; }
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        int x, y, w, h;
        bool gameWindow = false;
        string scope;
        if (hwnd != IntPtr.Zero)
        {
            if (!GetWindowRect(hwnd, out var r))
            {
                return new ScreenCapture(null, 0, 0, "没抓到", false, "GetWindowRect 失败（窗口可能已经关了）");
            }
            x = r.Left; y = r.Top; w = r.Right - r.Left; h = r.Bottom - r.Top;
            gameWindow = true;
            scope = $"游戏窗口 {w}x{h}";
        }
        else
        {
            x = 0; y = 0;
            w = GetSystemMetrics(SM_CXSCREEN);
            h = GetSystemMetrics(SM_CYSCREEN);
            int monitors = GetSystemMetrics(SM_CMONITORS);
            // 多显示器时主屏只是一部分。这件事必须写出来——
            // 不然看现场的人会以为「屏幕就这么大，游戏没开」。
            scope = monitors > 1
                ? $"主屏 {w}x{h}（有 {monitors} 台显示器，没找到游戏窗口，其余屏幕没抓到）"
                : $"主屏 {w}x{h}（没找到游戏窗口）";
        }
        if (w <= 0 || h <= 0)
            return new ScreenCapture(null, 0, 0, scope, gameWindow, $"截取区域无效（{w}x{h}）");

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return new ScreenCapture(null, 0, 0, scope, gameWindow, "取不到屏幕 DC");
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hbm = CreateCompatibleBitmap(screenDc, w, h);
        if (memDc == IntPtr.Zero || hbm == IntPtr.Zero)
        {
            try { if (hbm != IntPtr.Zero) DeleteObject(hbm); } catch { }
            try { if (memDc != IntPtr.Zero) DeleteDC(memDc); } catch { }
            ReleaseDC(IntPtr.Zero, screenDc);
            return new ScreenCapture(null, 0, 0, scope, gameWindow, "建不出兼容位图");
        }
        IntPtr old = SelectObject(memDc, hbm);
        if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY))
        {
            int err = Marshal.GetLastWin32Error();
            try { SelectObject(memDc, old); } catch { }
            try { DeleteObject(hbm); } catch { }
            try { DeleteDC(memDc); } catch { }
            ReleaseDC(IntPtr.Zero, screenDc);
            return new ScreenCapture(null, 0, 0, scope, gameWindow, $"BitBlt 失败（Win32 {err}）");
        }

        var buf = new byte[w * h * 4];
        var bi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        // GetDIBits 返回「实际写了几行」；0 表示失败，缓冲区里什么都没有。
        int lines = GetDIBits(memDc, hbm, 0, (uint)h, buf, ref bi, 0);

        SelectObject(memDc, old);
        DeleteObject(hbm);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        if (lines <= 0)
            return new ScreenCapture(null, 0, 0, scope, gameWindow, "GetDIBits 没取到像素");
        return new ScreenCapture(buf, w, h, scope, gameWindow, null);
    }

    /// <summary>缩放到 64×64 灰度（块平均）</summary>
    private static byte[] Downsample(byte[] src, int w, int h)
    {
        var dst = new byte[Dim * Dim];
        for (int ty = 0; ty < Dim; ty++)
        {
            for (int tx = 0; tx < Dim; tx++)
            {
                int sx0 = Math.Min(tx * w / Dim, w - 1);
                int sx1 = Math.Min((tx + 1) * w / Dim, w);
                int sy0 = Math.Min(ty * h / Dim, h - 1);
                int sy1 = Math.Min((ty + 1) * h / Dim, h);
                long sum = 0;
                int cnt = 0;
                for (int sy = sy0; sy < Math.Max(sy1, sy0 + 1); sy++)
                {
                    for (int sx = sx0; sx < Math.Max(sx1, sx0 + 1); sx++)
                    {
                        int idx = (sy * w + sx) * 4;
                        if (idx + 2 >= src.Length) continue;
                        sum += (src[idx] + src[idx + 1] + src[idx + 2]) / 3;
                        cnt++;
                    }
                }
                dst[ty * Dim + tx] = (byte)(cnt > 0 ? sum / cnt : 0);
            }
        }
        return dst;
    }

    /// <summary>逐像素差值绝对值之和，归一化到 0~100</summary>
    private static double Diff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum * 100.0 / (a.Length * 255.0);
    }

    /// <summary>
    /// 把截到的原始像素写成 PNG。
    /// v1.8 / F12：抽成可指定目标路径的静态方法，「现在就到」按时间戳命名另存，
    /// 与心跳监视的 screenshots 目录互不干扰（心跳只留 10 张、按 HHmmss 命名，
    /// 两套命名混在一个目录里会互相挤掉）。
    /// </summary>
    internal static void SavePngTo(byte[] buf, int w, int h, string path)
    {
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, buf, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.Create);
        enc.Save(fs);
    }

    /// <summary>保存 PNG（经主线程调度）并滚动删除旧图（保留最近 10 张）</summary>
    private static void SavePng(byte[] buf, int w, int h)
    {
        try
        {
            var d = Application.Current?.Dispatcher;
            if (d == null) return;
            d.BeginInvoke(new Action(() =>
            {
                try
                {
                    var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, buf, w * 4);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(src));
                    string dir = Path.Combine(LogService.Dir, "screenshots");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, DateTime.Now.ToString("HHmmss") + ".png");
                    using var fs = new FileStream(path, FileMode.Create);
                    enc.Save(fs);
                    TrimOld(dir);
                }
                catch { }
            }));
        }
        catch { }
    }

    private static void TrimOld(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "*.png")
                .OrderByDescending(f => f)
                .Skip(10)
                .ToList();
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private void Emit(SentinelEventKind kind, string payload)
    {
        try
        {
            OnEvent?.Invoke(new SentinelEvent { Kind = kind, Payload = payload, Source = "screen" });
        }
        catch { }
    }
}
